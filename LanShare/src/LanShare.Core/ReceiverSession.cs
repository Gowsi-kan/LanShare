using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace LanShare.Core;

/// <summary>
/// Receiver side of one session. Per data connection: reader task -> bounded frame channel -> writer task
/// (decompress, verify XxHash3, write, ack), so network receive and disk write overlap.
/// Big files: pre-allocated .lfspart, random-access chunk writes, crash-safe bitmap (.lfsstate), rename on completion.
/// Small files: arrive packed in bundles, written whole (no open handles), progress kept in a per-batch journal.
/// </summary>
public sealed class ReceiverSession : IDisposable
{
    sealed class FileState(FileEntry entry, string finalBase, byte[] done, int index, bool small)
    {
        public FileEntry Entry { get; } = entry;
        public string FinalBase { get; } = finalBase;
        public int Index { get; } = index;
        public bool IsSmall { get; } = small;
        public string PartPath => FinalBase + ".lfspart";
        public string StatePath => FinalBase + ".lfsstate";
        public byte[] Done { get; } = done;
        public object Gate { get; } = new();
        public bool Resumed { get; init; }
        public bool Claimed;
        public SafeFileHandle? Handle;
        public int Remaining;
        public volatile bool Finalized;
        public volatile bool Dirty;
    }

    readonly record struct Frame(
        int File,
        int Chunk,
        int RawLen,
        int WireLen,
        ulong Hash,
        byte[] Buf
    );

    readonly Offer _offer;
    readonly FileState[] _files;
    readonly string _journalPath;
    readonly ConcurrentQueue<(int Index, string Path)> _smallPending = new();
    readonly TaskCompletionSource _allDone = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    int _remaining;
    bool _disposed;

    public TransferState State { get; }
    public Task AllDone => _allDone.Task;
    public string PeerFingerprint { get; set; } = "";

    ReceiverSession(Offer offer, TransferState state, FileState[] files, string journalPath)
    {
        _offer = offer;
        State = state;
        _files = files;
        _journalPath = journalPath;
    }

    public static ReceiverSession Prepare(Offer offer, string root, TransferState state)
    {
        Directory.CreateDirectory(root);
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var journalPath = Path.Combine(rootFull, $".lfs-{BatchId(offer)}.journal");
        var journal = LoadJournal(journalPath);

        var files = new FileState[offer.Files.Count];
        long needed = 0;

        for (int i = 0; i < files.Length; i++)
        {
            var e = offer.Files[i];
            var finalBase = Resolve(rootFull, e.RelativePath);
            int chunks = ChunkMath.Count(e.Size, offer.ChunkSize);
            bool small = e.Size > 0 && e.Size <= Protocol.SmallFileLimit;

            byte[]? done;
            if (small)
            {
                bool journaled =
                    journal.TryGetValue(i, out var jp)
                    && File.Exists(jp)
                    && new FileInfo(jp).Length == e.Size;
                done = journaled ? new byte[] { 1 } : null;
            }
            else
            {
                done = TryLoadState(finalBase + ".lfsstate", finalBase + ".lfspart", e, chunks);
            }

            bool resume = done is not null;
            if (!resume)
                needed += e.Size;
            files[i] = new FileState(e, finalBase, done ?? new byte[chunks], i, small)
            {
                Resumed = resume,
            };
        }

        long free = FreeSpace(rootFull);
        DiagLog.Info(
            $"Receiver prepare: {files.Length} files, need {Format.Bytes(needed)}, free {Format.Bytes(free)}"
        );
        if (needed + (64L << 20) > free)
            throw new InsufficientSpaceException(needed, free);

        var session = new ReceiverSession(offer, state, files, journalPath);
        try
        {
            foreach (var f in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.FinalBase)!);

                if (f.Entry.Size == 0)
                {
                    File.WriteAllBytes(UniquePath(f.FinalBase), []);
                    f.Finalized = true;
                    state.FileDone();
                    continue;
                }

                if (f.IsSmall)
                {
                    if (f.Resumed)
                    {
                        f.Finalized = true;
                        state.AddBytes(f.Entry.Size);
                        state.FileDone();
                    }
                    else
                    {
                        f.Remaining = 1;
                        session._remaining++;
                    }
                    continue;
                }

                f.Handle = File.OpenHandle(
                    f.PartPath,
                    f.Resumed ? FileMode.Open : FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    FileOptions.Asynchronous | FileOptions.RandomAccess,
                    f.Resumed ? 0 : f.Entry.Size
                );
                if (RandomAccess.GetLength(f.Handle) < f.Entry.Size)
                    RandomAccess.SetLength(f.Handle, f.Entry.Size);

                int rem = 0;
                for (int j = 0; j < f.Done.Length; j++)
                {
                    if (f.Done[j] == 0)
                        rem++;
                    else
                        state.AddBytes(ChunkMath.Length(f.Entry.Size, offer.ChunkSize, j));
                }
                f.Remaining = rem;
                session._remaining += rem;
                if (rem == 0)
                    session.FinalizeFile(f);
            }
            if (session._remaining == 0)
                session._allDone.TrySetResult();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public byte[][] SnapshotDone()
    {
        var maps = new byte[_files.Length][];
        for (int i = 0; i < _files.Length; i++)
            lock (_files[i].Gate)
                maps[i] = (byte[])_files[i].Done.Clone();
        return maps;
    }

    /// <summary>Serves one data connection until the sender closes it or an I/O error occurs.</summary>
    public async Task ReceiveLoopAsync(Stream ns, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;
        var frames = Channel.CreateBounded<Frame>(
            new BoundedChannelOptions(Protocol.PipelineDepth)
            {
                SingleReader = true,
                SingleWriter = true,
            }
        );

        async Task Guarded(Func<Task> body)
        {
            try
            {
                await body();
            }
            catch (Exception e)
                when (e
                        is IOException
                            or SocketException
                            or OperationCanceledException
                            or ObjectDisposedException
                )
            {
                cts.Cancel(); // connection lost / session closed: sender retries unacked chunks
            }
            catch
            {
                cts.Cancel();
                throw;
            }
        }

        State.StreamUp();
        try
        {
            await Task.WhenAll(
                Task.Run(
                    () => Guarded(() => ReadFramesAsync(ns, frames.Writer, ct)),
                    CancellationToken.None
                ),
                Task.Run(
                    () => Guarded(() => WriteFramesAsync(ns, frames.Reader, ct)),
                    CancellationToken.None
                )
            );
        }
        finally
        {
            State.StreamDown();
            while (frames.Reader.TryRead(out var left))
                ArrayPool<byte>.Shared.Return(left.Buf);
        }
    }

    async Task ReadFramesAsync(Stream ns, ChannelWriter<Frame> writer, CancellationToken ct)
    {
        var hdr = new byte[Protocol.DataHeaderSize];
        try
        {
            while (true)
            {
                long tn = Stopwatch.GetTimestamp();
                if (await ns.ReadAsync(hdr.AsMemory(0, 1), ct) == 0)
                    break;
                await ns.ReadExactlyAsync(hdr.AsMemory(1), ct);
                State.AddStage(Stage.NetReceive, tn);

                int fi = BinaryPrimitives.ReadInt32LittleEndian(hdr);
                int ci = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(4));
                int raw = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(8));
                int wire = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(12));
                ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(16));

                if (raw <= 0 || raw > _offer.ChunkSize || wire <= 0 || wire > raw)
                    throw new InvalidDataException("Bad frame sizes.");
                if (fi != Protocol.BundleFile)
                {
                    if ((uint)fi >= (uint)_files.Length)
                        throw new InvalidDataException("Bad file index.");
                    var f = _files[fi];
                    if (f.IsSmall || (uint)ci >= (uint)f.Done.Length)
                        throw new InvalidDataException("Bad chunk index.");
                    if (raw != ChunkMath.Length(f.Entry.Size, _offer.ChunkSize, ci))
                        throw new InvalidDataException("Bad chunk length.");
                }

                var buf = ArrayPool<byte>.Shared.Rent(_offer.ChunkSize);
                try
                {
                    await ns.ReadExactlyAsync(buf.AsMemory(0, wire), ct);
                    await writer.WriteAsync(new Frame(fi, ci, raw, wire, hash, buf), ct);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    throw;
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    async Task WriteFramesAsync(Stream ns, ChannelReader<Frame> reader, CancellationToken ct)
    {
        byte[]? scratch = null; // decompression buffer
        var ack = new byte[1];
        try
        {
            await foreach (var f in reader.ReadAllAsync(ct))
            {
                try
                {
                    ReadOnlyMemory<byte> data;
                    if (f.WireLen == f.RawLen)
                        data = f.Buf.AsMemory(0, f.RawLen);
                    else
                    {
                        scratch ??= ArrayPool<byte>.Shared.Rent(_offer.ChunkSize);
                        long td = Stopwatch.GetTimestamp();
                        int n = LZ4Codec.Decode(
                            f.Buf.AsSpan(0, f.WireLen),
                            scratch.AsSpan(0, f.RawLen)
                        );
                        State.AddStage(Stage.Decompress, td);
                        if (n != f.RawLen)
                        {
                            ack[0] = Protocol.AckBad;
                            await ns.WriteAsync(ack, ct);
                            continue;
                        }
                        data = scratch.AsMemory(0, f.RawLen);
                    }

                    bool ok =
                        f.File == Protocol.BundleFile
                            ? await WriteBundleAsync(data, f.Hash, ct)
                            : await WriteChunkAsync(_files[f.File], f.Chunk, data, f.Hash, ct);
                    ack[0] = ok ? Protocol.AckOk : Protocol.AckBad;
                    await ns.WriteAsync(ack, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(f.Buf);
                }
            }
        }
        finally
        {
            if (scratch is not null)
                ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    async ValueTask<bool> WriteChunkAsync(
        FileState f,
        int ci,
        ReadOnlyMemory<byte> data,
        ulong hash,
        CancellationToken ct
    )
    {
        if (f.Finalized)
            return true; // duplicate of an already-acked chunk
        long th = Stopwatch.GetTimestamp();
        bool hashOk = XxHash3.HashToUInt64(data.Span) == hash;
        State.AddStage(Stage.Hash, th);
        if (!hashOk)
            return false;

        long tw = Stopwatch.GetTimestamp();
        await RandomAccess.WriteAsync(f.Handle!, data, (long)ci * _offer.ChunkSize, ct);
        State.AddStage(Stage.Disk, tw);

        bool first;
        lock (f.Gate)
        {
            first = f.Done[ci] == 0;
            f.Done[ci] = 1;
        }
        if (!first)
            return true;

        State.AddBytes(data.Length);
        State.CurrentFile = f.Entry.RelativePath;
        f.Dirty = true;

        try
        {
            if (Interlocked.Decrement(ref f.Remaining) == 0)
                await Task.Run(() => FinalizeFile(f), CancellationToken.None);
            if (Interlocked.Decrement(ref _remaining) == 0)
                _allDone.TrySetResult();
        }
        catch (Exception e)
        {
            _allDone.TrySetException(e);
            throw;
        }
        return true;
    }

    async ValueTask<bool> WriteBundleAsync(
        ReadOnlyMemory<byte> data,
        ulong hash,
        CancellationToken ct
    )
    {
        if (XxHash3.HashToUInt64(data.Span) != hash)
            return false;
        var entries = ParseBundle(data);
        bool all = true;
        foreach (var (fi, off, len) in entries)
            all &= await WriteSmallAsync(_files[fi], data.Slice(off, len), ct);
        return all; // false -> AckBad -> sender resends (a concurrent duplicate may not have finished yet)
    }

    (int File, int Offset, int Length)[] ParseBundle(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (span.Length < 4)
            throw new InvalidDataException("Bad bundle.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(span);
        if (count <= 0 || count > Protocol.MaxBundleFiles || 4L + 8L * count > span.Length)
            throw new InvalidDataException("Bad bundle.");

        var list = new (int File, int Offset, int Length)[count];
        long pos = 4 + 8L * count;
        for (int k = 0; k < count; k++)
        {
            int fi = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4 + 8 * k));
            int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8 + 8 * k));
            if (
                (uint)fi >= (uint)_files.Length
                || !_files[fi].IsSmall
                || len != _files[fi].Entry.Size
            )
                throw new InvalidDataException("Bad bundle entry.");
            list[k] = (fi, (int)pos, len);
            pos += len;
            if (pos > span.Length)
                throw new InvalidDataException("Truncated bundle.");
        }
        if (pos != span.Length)
            throw new InvalidDataException("Bundle length mismatch.");
        return list;
    }

    async Task<bool> WriteSmallAsync(FileState f, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        bool claim;
        lock (f.Gate)
        {
            claim = !f.Claimed;
            f.Claimed = true;
        }
        if (!claim)
            return f.Finalized; // duplicate resend: only acknowledge once the file really exists

        try
        {
            await File.WriteAllBytesAsync(f.PartPath, data, ct);
            var dest = UniquePath(f.FinalBase);
            File.Move(f.PartPath, dest);
            lock (f.Gate)
                f.Done[0] = 1;
            f.Finalized = true;
            State.AddBytes(f.Entry.Size);
            State.CurrentFile = f.Entry.RelativePath;
            State.FileDone();
            _smallPending.Enqueue((f.Index, dest));
        }
        catch
        {
            lock (f.Gate)
                f.Claimed = false;
            throw;
        }

        if (Interlocked.Decrement(ref _remaining) == 0)
            _allDone.TrySetResult();
        return true;
    }

    void FinalizeFile(FileState f)
    {
        f.Finalized = true;
        var h = f.Handle!;
        RandomAccess.FlushToDisk(h);
        h.Dispose();
        File.Move(f.PartPath, UniquePath(f.FinalBase));
        TryDelete(f.StatePath);
        State.FileDone();
    }

    /// <summary>Flushes written data to disk, then atomically persists resume state (big-file bitmaps + small-file journal).</summary>
    public void Persist(TimeSpan? budget = null)
    {
        foreach (var f in _files)
        {
            if (f.Finalized || f.Handle is null || !f.Dirty)
                continue;
            f.Dirty = false;
            byte[] snap;
            lock (f.Gate)
                snap = (byte[])f.Done.Clone();
            try
            {
                RandomAccess.FlushToDisk(f.Handle); // data hits disk BEFORE the bitmap claims it
                var tmp = f.StatePath + ".tmp";
                using (
                    var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
                )
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(f.Entry.Key);
                    w.Write(snap.Length);
                    w.Write(snap);
                    w.Flush();
                    fs.Flush(true);
                }
                File.Move(tmp, f.StatePath, overwrite: true);
            }
            catch (Exception e)
                when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                f.Dirty = true;
            }
        }
        PersistSmall(budget);
    }

    void PersistSmall(TimeSpan? budget)
    {
        if (_smallPending.IsEmpty)
            return;
        long t0 = Stopwatch.GetTimestamp();
        var batch = new List<(int Index, string Path)>();
        while (
            (budget is null || Stopwatch.GetElapsedTime(t0) < budget.Value)
            && _smallPending.TryDequeue(out var item)
        )
        {
            try
            {
                using var h = File.OpenHandle(
                    item.Path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete
                );
                RandomAccess.FlushToDisk(h);
                batch.Add(item);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (batch.Count == 0)
            return;

        try
        {
            using var fs = new FileStream(
                _journalPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read
            );
            using var w = new BinaryWriter(fs);
            foreach (var (idx, path) in batch)
            {
                w.Write(idx);
                w.Write(path);
            }
            w.Flush();
            fs.Flush(true);
        }
        catch (IOException)
        {
            foreach (var b in batch)
                _smallPending.Enqueue(b);
        }
    }

    /// <summary>Call after the whole session succeeded: the small-file journal is no longer needed.</summary>
    public void FinishCleanup()
    {
        while (_smallPending.TryDequeue(out _)) { }
        TryDelete(_journalPath);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Persist();
        foreach (var f in _files)
            if (!f.Finalized)
                f.Handle?.Dispose();
    }

    static string BatchId(Offer offer)
    {
        var h = new XxHash3();
        foreach (var f in offer.Files)
            h.Append(Encoding.UTF8.GetBytes(f.Key));
        return h.GetCurrentHashAsUInt64().ToString("x16");
    }

    static Dictionary<int, string> LoadJournal(string path)
    {
        var map = new Dictionary<int, string>();
        try
        {
            if (!File.Exists(path))
                return map;
            using var r = new BinaryReader(File.OpenRead(path));
            while (true)
            {
                int idx = r.ReadInt32();
                map[idx] = r.ReadString();
            }
        }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (FormatException) { }
        return map;
    }

    static byte[]? TryLoadState(string statePath, string partPath, FileEntry e, int chunks)
    {
        try
        {
            if (!File.Exists(statePath) || !File.Exists(partPath))
                return null;
            if (new FileInfo(partPath).Length != e.Size)
                return null;
            using var r = new BinaryReader(File.OpenRead(statePath));
            if (r.ReadString() != e.Key || r.ReadInt32() != chunks)
                return null;
            var d = r.ReadBytes(chunks);
            return d.Length == chunks ? d : null;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or EndOfStreamException
                        or FormatException
                        or UnauthorizedAccessException
            )
        {
            return null;
        }
    }

    internal static string Resolve(string rootFull, string rel)
    {
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidDataException("Empty path.");
        foreach (var p in parts)
            if (p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Unsafe path: {rel}");

        var full = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(parts)));
        var prefix = Path.EndsInDirectorySeparator(rootFull)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Path escapes target: {rel}");
        return full;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p))
                return p;
        }
    }

    static long FreeSpace(string root)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
    }
}
