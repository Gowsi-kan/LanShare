using System.Buffers.Binary;
using System.Text;

namespace LanShare.Core;

public enum Phase
{
    Connecting,
    WaitingForPeer,
    AwaitingYou,
    Transferring,
    Completed,
    Failed,
    Cancelled,
    Declined,
    Paused,
}

public enum Decision : byte
{
    Decline = 0,
    Accept = 1,
    NoSpace = 2,
}

public static class Protocol
{
    public const int DiscoveryPort = 45455;
    public const int DefaultTcpPort = 45454;
    public const int ChunkSize = 2 * 1024 * 1024;
    public const int DefaultStreams = 4;
    public const int MaxFrame = 64 * 1024 * 1024;
    public const int DataHeaderSize = 24; // fileIdx i32 | chunkIdx i32 | rawLen i32 | wireLen i32 | xxh3(raw) u64
    public const int BundleFile = -1; // fileIdx marker: frame carries a bundle of small files
    public const int SmallFileLimit = 256 * 1024;
    public const int MaxBundleFiles = 4096;
    public const int PipelineDepth = 4; // unacked frames in flight per stream

    public const byte KindSession = 1;
    public const byte KindData = 2;
    public const byte AckOk = 1;
    public const byte AckBad = 0;
    public const byte Done = 0xD0;

    public const byte ControlPause = 0x50; // control-connection message: "pause sending"
    public const byte ControlResume = 0x52; // control-connection message: "resume sending"

    public static readonly byte[] Magic = "LFS2"u8.ToArray();
}

public sealed record FileEntry(string RelativePath, long Size, string Key);

public sealed record Offer(
    Guid SessionId,
    string SenderName,
    int Streams,
    int ChunkSize,
    IReadOnlyList<FileEntry> Files
)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}

public static class ChunkMath
{
    public static int Count(long size, int chunk) => (int)((size + chunk - 1) / chunk);

    public static int Length(long size, int chunk, int index) =>
        (int)Math.Min(chunk, size - (long)index * chunk);
}

public sealed class TransferRejectedException(Decision decision)
    : Exception(
        decision == Decision.NoSpace
            ? "Receiver has not enough free disk space."
            : "Receiver declined the transfer."
    )
{
    public Decision Decision { get; } = decision;
}

public sealed class InsufficientSpaceException(long needed, long free)
    : IOException($"Need {Format.Bytes(needed)}, only {Format.Bytes(free)} free.");

internal static class Wire
{
    public static async Task WriteHandshakeAsync(Stream s, byte kind, CancellationToken ct)
    {
        var buf = new byte[5];
        Protocol.Magic.CopyTo(buf, 0);
        buf[4] = kind;
        await s.WriteAsync(buf, ct);
    }

    public static async Task<byte> ReadHandshakeAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[5];
        await s.ReadExactlyAsync(buf, ct);
        if (!buf.AsSpan(0, 4).SequenceEqual(Protocol.Magic))
            throw new InvalidDataException("Bad protocol magic.");
        return buf[4];
    }

    public static async Task WriteFrameAsync(
        Stream s,
        Action<BinaryWriter> write,
        CancellationToken ct
    )
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(0); // length placeholder
        write(bw);
        bw.Flush();
        var arr = ms.GetBuffer();
        int total = (int)ms.Length;
        BinaryPrimitives.WriteInt32LittleEndian(arr, total - 4);
        await s.WriteAsync(arr.AsMemory(0, total), ct);
    }

    public static async Task<BinaryReader> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var hdr = new byte[4];
        await s.ReadExactlyAsync(hdr, ct);
        int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
        if (len < 0 || len > Protocol.MaxFrame)
            throw new InvalidDataException("Bad frame length.");
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct);
        return new BinaryReader(new MemoryStream(body), Encoding.UTF8);
    }

    public static void WriteOffer(BinaryWriter w, Offer o)
    {
        w.Write(o.SessionId.ToByteArray());
        w.Write(o.SenderName);
        w.Write(o.Streams);
        w.Write(o.ChunkSize);
        w.Write(o.Files.Count);
        foreach (var f in o.Files)
        {
            w.Write(f.RelativePath);
            w.Write(f.Size);
            w.Write(f.Key);
        }
    }

    public static Offer ReadOffer(BinaryReader r)
    {
        var id = new Guid(r.ReadBytes(16));
        var name = r.ReadString();
        int streams = r.ReadInt32();
        int chunk = r.ReadInt32();
        int n = r.ReadInt32();
        if (chunk < 64 * 1024 || chunk > 64 * 1024 * 1024)
            throw new InvalidDataException("Bad chunk size.");
        if (streams is < 1 or > 64)
            throw new InvalidDataException("Bad stream count.");
        if (n is < 0 or > 1_000_000)
            throw new InvalidDataException("Bad file count.");
        var files = new FileEntry[n];
        for (int i = 0; i < n; i++)
        {
            var rel = r.ReadString();
            long size = r.ReadInt64();
            var key = r.ReadString();
            if (size < 0)
                throw new InvalidDataException("Bad file size.");
            files[i] = new FileEntry(rel, size, key);
        }
        return new Offer(id, name, streams, chunk, files);
    }
}
