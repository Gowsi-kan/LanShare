using LanShare.Core;

namespace LanShare.Tests;

public class ResumeStateTests : IDisposable
{
    const int Chunk = 65_536;
    const string Key = "a.bin|1|1";
    static readonly long Size = 5L * Chunk + 100; // 6 chunks, the last one is 100 bytes

    readonly string _root = Path.Combine(Path.GetTempPath(), "lanshare-resume-" + Guid.NewGuid().ToString("N")[..8]);

    public ResumeStateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    Offer MakeOffer() => new(Guid.NewGuid(), "t", 2, Chunk, [new FileEntry("a.bin", Size, Key)]);

    string PartPath => Path.Combine(_root, "a.bin.lfspart");
    string StatePath => Path.Combine(_root, "a.bin.lfsstate");

    void WritePart(long length) => File.WriteAllBytes(PartPath, new byte[length]);

    void WriteState(string key, byte[] bitmap)
    {
        using var fs = File.Create(StatePath);
        using var w = new BinaryWriter(fs);
        w.Write(key);
        w.Write(bitmap.Length);
        w.Write(bitmap);
    }

    static void AssertFresh(ReceiverSession session, TransferState state)
    {
        Assert.All(session.SnapshotDone()[0], b => Assert.Equal((byte)0, b));
        Assert.Equal(0, state.DoneBytes);
    }

    [Fact]
    public void ValidState_IsResumed()
    {
        byte[] map = [1, 0, 1, 1, 0, 1];
        WritePart(Size);
        WriteState(Key, map);

        var state = new TransferState(Size, 1);
        using var session = ReceiverSession.Prepare(MakeOffer(), _root, state);

        Assert.Equal(map, session.SnapshotDone()[0]);
        long expected = 0;
        for (int i = 0; i < map.Length; i++)
            if (map[i] != 0) expected += ChunkMath.Length(Size, Chunk, i);
        Assert.Equal(expected, state.DoneBytes);
    }

    [Fact]
    public void FileChangedOnTheSender_KeyMismatch_StartsFresh()
    {
        WritePart(Size);
        WriteState("other|1|1", [1, 1, 1, 1, 1, 1]);
        var state = new TransferState(Size, 1);
        using var session = ReceiverSession.Prepare(MakeOffer(), _root, state);
        AssertFresh(session, state);
    }

    [Fact]
    public void PartFileHasWrongLength_StartsFresh()
    {
        WritePart(Size - 10);
        WriteState(Key, [1, 1, 1, 1, 1, 1]);
        var state = new TransferState(Size, 1);
        using var session = ReceiverSession.Prepare(MakeOffer(), _root, state);
        AssertFresh(session, state);
    }

    [Fact]
    public void CorruptStateFile_StartsFresh()
    {
        WritePart(Size);
        File.WriteAllBytes(StatePath, Enumerable.Repeat((byte)0xFF, 16).ToArray()); // invalid 7-bit length prefix
        var state = new TransferState(Size, 1);
        using var session = ReceiverSession.Prepare(MakeOffer(), _root, state);
        AssertFresh(session, state);
    }

    [Fact]
    public void TruncatedStateFile_StartsFresh()
    {
        WritePart(Size);
        File.WriteAllBytes(StatePath, [3]);
        var state = new TransferState(Size, 1);
        using var session = ReceiverSession.Prepare(MakeOffer(), _root, state);
        AssertFresh(session, state);
    }
}