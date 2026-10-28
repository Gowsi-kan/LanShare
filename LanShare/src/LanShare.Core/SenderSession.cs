using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace LanShare.Core;

/// <param name="Streams">Parallel data connections. 0 = auto-tune (starts at 2, adds streams until throughput plateaus, max 8).</param>
/// <param name="Compress">Adaptive LZ4: tried per chunk, backs off automatically on incompressible data.</param>
/// <param name="Limiter">Optional shared outgoing bandwidth limiter.</param>
public sealed record SendOptions(
    int Streams = 0,
    bool Compress = true,
    RateLimiter? Limiter = null
);

/// <summary>
/// Sender side. Per data connection a 3-stage pipeline runs: read+hash(+compress) -> socket write -> ack reader,
/// keeping up to <see cref="Protocol.PipelineDepth"/> frames in flight. Small files are packed into bundle frames.
/// A chunk only counts once the receiver has verified and persisted it; on any stream failure every unacked
/// chunk of that stream is put back on the shared queue and the stream reconnects.
/// </summary>
public sealed class SenderSession
{
    const int MaxConsecutiveFailures = 8;
    const int MaxAutoStreams = 8;
    const int InitialAutoStreams = 2;
    const int AckTimeoutSeconds = 120; // receiver alive but not acknowledging -> treat the stream as dead

    readonly record struct WorkItem(int File, int Chunk); // File == -1 -> bundle #Chunk

    sealed class Prepared
    {
        public WorkItem Item;
        public byte[] Data = [];
        public int RawLen,
            WireLen;
        public ulong Hash;
        public long Key => ((long)Item.File << 32) | (uint)Item.Chunk;
    }

    sealed class CompressionGate
    {
        int _skip,
            _misses;

        public bool ShouldTry()
        {
            if (_skip > 0)
            {
                _skip--;
                return false;
            }
            return true;
        }

        public void Report(bool useful)
        {
            if (useful)
                _misses = 0;
            else if (++_misses >= 4)
            {
                _skip = 64;
                _misses = 0;
            }
        }
    }

    sealed class StreamCtx
    {
        public int Acked;
        public readonly CompressionGate Gate = new();
    }

    readonly IPEndPoint _remote;
    readonly string _senderName;
    readonly (string Full, string Rel)[] _files;
    readonly FileEntry[] _entries;
    readonly SendOptions _options;
    readonly Guid _id = Guid.NewGuid();

    public TransferState State { get; }

    public SenderSession(
        IPEndPoint remote,
        string senderName,
        IEnumerable<(string Full, string Rel)> files,
        SendOptions? options = null
    )
    {
        _remote = remote;
        _senderName = senderName;
        _options = options ?? new SendOptions();
        _files = files.ToArray();
        if (_files.Length == 0)
            throw new ArgumentException("No files to send.");

        _entries = _files
            .Select(f =>
            {
                var fi = new FileInfo(f.Full);
                if (!fi.Exists)
                    throw new FileNotFoundException("File not found.", f.Full);
                return new FileEntry(
                    f.Rel,
                    fi.Length,
                    $"{f.Rel}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"
                );
            })
            .ToArray();

        State = new TransferState(_entries.Sum(e => e.Size), _entries.Length)
        {
            Phase = Phase.Connecting,
        };
    }

    /// <summary>Never throws; outcome is reflected in <see cref="State"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(ct);
            State.Phase = Phase.Completed;
        }
        catch (OperationCanceledException)
        {
            State.Phase = Phase.Cancelled;
        }
        catch (TransferRejectedException e)
        {
            State.Error = e.Message;
            State.Phase = e.Decision == Decision.Decline ? Phase.Declined : Phase.Failed;
        }
        catch (Exception e)
        {
            State.Error = e.Message;
            State.Phase = Phase.Failed;
        }
    }

    async Task RunCoreAsync(CancellationToken ct)
    {
        int chunk = Protocol.ChunkSize;
        State.Phase = Phase.Connecting;
        var (sock, cs) = await ConnectAsync(_remote, ct);
        using var csGuard = cs;
        State.LinkBps = NetInfo.LinkBitsPerSecond(sock.LocalEndPoint);

        int initialStreams =
            _options.Streams > 0 ? Math.Clamp(_options.Streams, 1, 32) : InitialAutoStreams;
        await Wire.WriteHandshakeAsync(cs, Protocol.KindSession, ct);
        var offer = new Offer(_id, _senderName, initialStreams, chunk, _entries);
        await Wire.WriteFrameAsync(cs, w => Wire.WriteOffer(w, offer), ct);

        State.Phase = Phase.WaitingForPeer;
        byte[][] done;
        using (var r = await Wire.ReadFrameAsync(cs, ct))
        {
            var decision = (Decision)r.ReadByte();
            if (decision != Decision.Accept)
                throw new TransferRejectedException(decision);
            done = new byte[_entries.Length][];
            for (int i = 0; i < done.Length; i++)
            {
                int expected = ChunkMath.Count(_entries[i].Size, chunk);
                done[i] = r.ReadBytes(expected);
                if (done[i].Length != expected)
                    throw new InvalidDataException("Bad resume map.");
            }
        }

        // ---- build the work queue: big files -> chunks, small files -> packed bundles ----
        var queue = Channel.CreateUnbounded<WorkItem>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }
        );
        var fileRemaining = new int[_entries.Length];
        var bundleList = new List<int[]>();
        var cur = new List<int>();
        int curBytes = 4;
        int remaining = 0;

        void FlushBundle()
        {
            if (cur.Count == 0)
                return;
            bundleList.Add(cur.ToArray());
            cur.Clear();
            curBytes = 4;
        }

        for (int i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (entry.Size > 0 && entry.Size <= Protocol.SmallFileLimit)
            {
                if (done[i][0] != 0)
                {
                    State.AddBytes(entry.Size);
                    State.FileDone();
                    continue;
                }
                if (cur.Count >= Protocol.MaxBundleFiles || curBytes + 8 + entry.Size > chunk)
                    FlushBundle();
                cur.Add(i);
                curBytes += 8 + (int)entry.Size;
                continue;
            }

            for (int j = 0; j < done[i].Length; j++)
            {
                if (done[i][j] != 0)
                    State.AddBytes(ChunkMath.Length(entry.Size, chunk, j));
                else
                {
                    queue.Writer.TryWrite(new WorkItem(i, j));
                    fileRemaining[i]++;
                    remaining++;
                }
            }
            if (fileRemaining[i] == 0)
                State.FileDone();
        }
        FlushBundle();
        var bundles = bundleList.ToArray();
        for (int k = 0; k < bundles.Length; k++)
        {
            queue.Writer.TryWrite(new WorkItem(-1, k));
            remaining++;
        }
        if (remaining == 0)
            queue.Writer.TryComplete();

        State.Phase = Phase.Transferring;
        using var lcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? fatal = null;
        bool receivedDone = false;
        var workerTasks = new List<Task>();

        void Fail(Exception ex)
        {
            Interlocked.CompareExchange(ref fatal, ex, null);
            lcts.Cancel();
        }

        void Release(Prepared p)
        {
            if (p.Data.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(p.Data);
                p.Data = [];
            }
        }

        void Complete(Prepared p)
        {
            State.AddSent(p.RawLen, p.WireLen);
            if (p.Item.File >= 0)
            {
                State.AddBytes(p.RawLen);
                State.CurrentFile = _entries[p.Item.File].RelativePath;
                if (Interlocked.Decrement(ref fileRemaining[p.Item.File]) == 0)
                    State.FileDone();
            }
            else
            {
                var idxs = bundles[p.Item.Chunk];
                foreach (var idx in idxs)
                {
                    State.AddBytes(_entries[idx].Size);
                    State.FileDone();
                }
                State.CurrentFile = $"{idxs.Length} small files";
            }
            Release(p);
            if (Interlocked.Decrement(ref remaining) == 0)
                queue.Writer.TryComplete();
        }

        async Task WaitDoneAsync()
        {
            try
            {
                var one = new byte[1];
                int n = await cs.ReadAsync(one, lcts.Token);
                if (n == 0)
                    throw new IOException("Receiver closed the connection before finishing.");
                if (one[0] != Protocol.Done)
                    throw new InvalidDataException("Unexpected control byte.");
                receivedDone = true;
            }
            catch (OperationCanceledException) when (lcts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        async Task RunPipelineAsync(NetworkStream ns, StreamCtx ctx, CancellationToken outer)
        {
            using var pcts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            var pt = pcts.Token;
            var owned = new ConcurrentDictionary<long, Prepared>();
            var prepared = Channel.CreateBounded<Prepared>(
                new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true }
            );
            var inflight = Channel.CreateBounded<Prepared>(
                new BoundedChannelOptions(Protocol.PipelineDepth)
                {
                    SingleReader = true,
                    SingleWriter = true,
                }
            );
            Exception? failure = null;

            void Abort(Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                pcts.Cancel();
            }

            // stage 1: take work, read from disk, hash, maybe compress
            async Task ProduceAsync()
            {
                try
                {
                    await foreach (var item in queue.Reader.ReadAllAsync(pt))
                    {
                        var p = new Prepared { Item = item };
                        owned[p.Key] = p;
                        await LoadAsync(p, bundles, ctx.Gate, pt);
                        await prepared.Writer.WriteAsync(p, pt);
                    }
                    prepared.Writer.TryComplete();
                }
                catch (OperationCanceledException) when (pt.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Abort(ex);
                }
            }

            // stage 2: write frames to the socket (bandwidth limited)
            async Task SendFramesAsync()
            {
                var hdr = new byte[Protocol.DataHeaderSize];
                try
                {
                    await foreach (var p in prepared.Reader.ReadAllAsync(pt))
                    {
                        if (_options.Limiter is { } lim)
                            await lim.WaitAsync(p.WireLen, pt);
                        BinaryPrimitives.WriteInt32LittleEndian(hdr, p.Item.File);
                        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(4), p.Item.Chunk);
                        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(8), p.RawLen);
                        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(12), p.WireLen);
                        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(16), p.Hash);
                        await ns.WriteAsync(hdr, pt);
                        await ns.WriteAsync(p.Data.AsMemory(0, p.WireLen), pt);
                        await inflight.Writer.WriteAsync(p, pt);
                    }
                    inflight.Writer.TryComplete();
                }
                catch (OperationCanceledException) when (pt.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Abort(ex);
                }
            }

            // stage 3: read acks in send order
            async Task AckAsync()
            {
                var ack = new byte[1];
                int badInRow = 0;
                try
                {
                    await foreach (var p in inflight.Reader.ReadAllAsync(pt))
                    {
                        using (var tcts = CancellationTokenSource.CreateLinkedTokenSource(pt))
                        {
                            tcts.CancelAfter(TimeSpan.FromSeconds(AckTimeoutSeconds));
                            try
                            {
                                await ns.ReadExactlyAsync(ack, tcts.Token);
                            }
                            catch (OperationCanceledException) when (!pt.IsCancellationRequested)
                            {
                                throw new IOException("Receiver stopped acknowledging (stalled).");
                            }
                        }
                        owned.TryRemove(p.Key, out _);
                        if (ack[0] == Protocol.AckOk)
                        {
                            badInRow = 0;
                            Interlocked.Increment(ref ctx.Acked);
                            Complete(p);
                        }
                        else
                        {
                            State.Retry();
                            if (++badInRow > MaxConsecutiveFailures)
                                throw new InvalidDataException("Repeated checksum failures.");
                            Release(p);
                            queue.Writer.TryWrite(p.Item);
                        }
                    }
                }
                catch (OperationCanceledException) when (pt.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Abort(ex);
                }
            }

            await Task.WhenAll(
                Task.Run(ProduceAsync, CancellationToken.None),
                Task.Run(SendFramesAsync, CancellationToken.None),
                Task.Run(AckAsync, CancellationToken.None)
            );

            if (failure is not null || outer.IsCancellationRequested)
            {
                foreach (var p in owned.Values)
                {
                    Release(p);
                    queue.Writer.TryWrite(p.Item);
                }
                owned.Clear();
            }
            if (failure is not null)
                throw failure;
            outer.ThrowIfCancellationRequested();
        }

        async Task WorkerAsync()
        {
            var ctx = new StreamCtx();
            var token = lcts.Token;
            int failures = 0;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int ackedBefore = Volatile.Read(ref ctx.Acked);
                    NetworkStream? ns = null;
                    try
                    {
                        ns = await ConnectDataAsync(token);
                        State.StreamUp();
                        await RunPipelineAsync(ns, ctx, token);
                        return;
                    }
                    catch (Exception ex)
                        when (ex
                                is IOException
                                    or SocketException
                                    or InvalidDataException
                                    or ObjectDisposedException
                        )
                    {
                        State.Retry();
                        if (Volatile.Read(ref ctx.Acked) > ackedBefore)
                            failures = 0;
                        if (++failures > MaxConsecutiveFailures)
                            throw new IOException(
                                $"Giving up after {failures} attempts: {ex.Message}",
                                ex
                            );
                        await Task.Delay(Math.Min(250 * failures, 2000), token);
                    }
                    finally
                    {
                        if (ns is not null)
                        {
                            State.StreamDown();
                            ns.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (lcts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        void StartWorker()
        {
            lock (workerTasks)
                workerTasks.Add(Task.Run(WorkerAsync, CancellationToken.None));
        }

        // sleeps; returns false if the transfer finished meanwhile
        async Task<bool> SleepAsync(int ms, CancellationToken token)
        {
            var delay = Task.Delay(ms, token);
            var winner = await Task.WhenAny(delay, queue.Reader.Completion);
            if (winner != delay)
                return false;
            await delay;
            return true;
        }

        async Task<double> MeasureAsync(int ms, CancellationToken token)
        {
            long b0 = State.DoneBytes;
            long t0 = Stopwatch.GetTimestamp();
            if (!await SleepAsync(ms, token))
                return -1;
            return (State.DoneBytes - b0) / Stopwatch.GetElapsedTime(t0).TotalSeconds;
        }

        // Hill-climb: add a stream while throughput still improves by >= 8%, then settle.
        async Task AutoTuneAsync()
        {
            var token = lcts.Token;
            try
            {
                int count = InitialAutoStreams;
                if (!await SleepAsync(1500, token))
                    return;
                double prev = await MeasureAsync(2000, token);
                while (
                    prev >= 0
                    && count < MaxAutoStreams
                    && Volatile.Read(ref remaining) > count * (Protocol.PipelineDepth + 2) * 2
                )
                {
                    StartWorker();
                    count++;
                    if (!await SleepAsync(1000, token))
                        return;
                    double now = await MeasureAsync(2000, token);
                    if (now < 0 || now < prev * 1.08)
                        return;
                    prev = now;
                }
            }
            catch (OperationCanceledException) when (lcts.IsCancellationRequested) { }
        }

        for (int i = 0; i < initialStreams; i++)
            StartWorker();
        Task tune = _options.Streams > 0 ? Task.CompletedTask : AutoTuneAsync();
        await Task.WhenAll(WaitDoneAsync(), tune);

        Task[] snapshot;
        lock (workerTasks)
            snapshot = workerTasks.ToArray();
        await Task.WhenAll(snapshot);

        if (fatal is not null)
            throw fatal;
        ct.ThrowIfCancellationRequested();
        if (!receivedDone)
            throw new IOException("Receiver did not confirm completion.");
    }

    /// <summary>Reads a chunk (or packs a bundle of small files) into a pooled buffer, hashes it, optionally LZ4-compresses it.</summary>
    async Task LoadAsync(Prepared p, int[][] bundles, CompressionGate gate, CancellationToken ct)
    {
        int chunk = Protocol.ChunkSize;
        var raw = ArrayPool<byte>.Shared.Rent(chunk);
        p.Data = raw;
        int len;

        if (p.Item.File >= 0)
        {
            var entry = _entries[p.Item.File];
            len = ChunkMath.Length(entry.Size, chunk, p.Item.Chunk);
            using var h = File.OpenHandle(
                _files[p.Item.File].Full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous
            );
            await ReadFullyAsync(h, raw.AsMemory(0, len), (long)p.Item.Chunk * chunk, ct);
        }
        else
        {
            var idxs = bundles[p.Item.Chunk];
            int pos = 4 + 8 * idxs.Length;
            BinaryPrimitives.WriteInt32LittleEndian(raw, idxs.Length);
            for (int k = 0; k < idxs.Length; k++)
            {
                var entry = _entries[idxs[k]];
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4 + 8 * k), idxs[k]);
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8 + 8 * k), (int)entry.Size);
                using var h = File.OpenHandle(
                    _files[idxs[k]].Full,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.Asynchronous
                );
                await ReadFullyAsync(h, raw.AsMemory(pos, (int)entry.Size), 0, ct);
                pos += (int)entry.Size;
            }
            len = pos;
        }

        p.RawLen = len;
        p.WireLen = len;
        p.Hash = XxHash3.HashToUInt64(raw.AsSpan(0, len));

        if (_options.Compress && len >= 4096 && gate.ShouldTry())
        {
            var c = ArrayPool<byte>.Shared.Rent(chunk);
            int limit = len - len / 10; // only worth it if we save >= 10%
            int n = LZ4Codec.Encode(raw.AsSpan(0, len), c.AsSpan(0, limit));
            if (n > 0 && n < len)
            {
                ArrayPool<byte>.Shared.Return(raw);
                p.Data = c;
                p.WireLen = n;
                gate.Report(true);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(c);
                gate.Report(false);
            }
        }
    }

    async Task<NetworkStream> ConnectDataAsync(CancellationToken ct)
    {
        var (_, ns) = await ConnectAsync(_remote, ct);
        try
        {
            await Wire.WriteHandshakeAsync(ns, Protocol.KindData, ct);
            await ns.WriteAsync(_id.ToByteArray(), ct);
            return ns;
        }
        catch
        {
            ns.Dispose();
            throw;
        }
    }

    static async Task<(Socket Socket, NetworkStream Stream)> ConnectAsync(
        IPEndPoint ep,
        CancellationToken ct
    )
    {
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        SocketTuning.Apply(s);
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(5));
            await s.ConnectAsync(ep, t.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            s.Dispose();
            throw new IOException("Connection timed out.");
        }
        catch
        {
            s.Dispose();
            throw;
        }
        return (s, new NetworkStream(s, ownsSocket: true));
    }

    static async ValueTask ReadFullyAsync(
        SafeFileHandle h,
        Memory<byte> m,
        long offset,
        CancellationToken ct
    )
    {
        while (m.Length > 0)
        {
            int n = await RandomAccess.ReadAsync(h, m, offset, ct);
            if (n == 0)
                throw new EndOfStreamException("Source file changed during transfer.");
            m = m[n..];
            offset += n;
        }
    }
}
