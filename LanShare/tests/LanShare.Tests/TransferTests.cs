using LanShare.Core;

namespace LanShare.Tests;

public class TransferTests
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task LargeFile_IsBitIdentical()
    {
        await using var rig = new Rig();
        var files = new[] { rig.AddFile("big.bin", 12L << 20, seed: 1) };
        var s = rig.NewSession(files, rig.Options(streams: 2));
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Completed, s.State.Phase);
        rig.AssertIdentical(files);
    }

    [Fact]
    public async Task ManySmallFiles_AreBundled_AndIdentical()
    {
        await using var rig = new Rig();
        var rnd = new Random(42);
        var files = new List<(string Full, string Rel)>();
        for (int i = 0; i < 400; i++)
        {
            long size = i == 0 ? 0 : i == 1 ? 1 : rnd.Next(2, 120_000); // includes an empty and a 1-byte file
            files.Add(rig.AddFile($"d{i % 7}/sub{i % 3}/f{i:000}.dat", size, seed: i, compressible: i % 2 == 0));
        }

        var s = rig.NewSession(files, rig.Options(streams: 3, compress: true));
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Completed, s.State.Phase);
        Assert.Equal(400, s.State.FilesDone);
        rig.AssertIdentical(files);
    }

    [Fact]
    public async Task MixOfBigSmallAndEmptyFiles_IsBitIdentical()
    {
        await using var rig = new Rig();
        var files = new List<(string Full, string Rel)> { rig.AddFile("mix/big.bin", 6L << 20, seed: 5), rig.AddFile("mix/empty.txt", 0, seed: 6) };
        var rnd = new Random(7);
        for (int i = 0; i < 50; i++)
            files.Add(rig.AddFile($"mix/s/f{i}.bin", rnd.Next(1, 300_000), seed: 100 + i)); // some above the 256 KiB bundle limit

        var s = rig.NewSession(files, rig.Options(streams: 2));
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Completed, s.State.Phase);
        rig.AssertIdentical(files);
    }

    [Fact]
    public async Task Compression_ShrinksText_AndSkipsRandomData()
    {
        await using var rig = new Rig();
        var text = new[] { rig.AddFile("text.log", 4L << 20, seed: 11, compressible: true) };
        var limiter = new RateLimiter { BytesPerSecond = 10L << 20 };
        var st = rig.NewSession(text, rig.Options(compress: true, limiter: limiter));
        await rig.RunAsync(st, T);
        Assert.Equal(Phase.Completed, st.State.Phase);
        Assert.True(st.State.SentWireBytes < st.State.SentRawBytes * 0.8, "text should compress");
        rig.AssertIdentical(text);

        var noise = new[] { rig.AddFile("noise.bin", 4L << 20, seed: 12) };
        var sn = rig.NewSession(noise, rig.Options(compress: true));
        await rig.RunAsync(sn, T);
        Assert.Equal(Phase.Completed, sn.State.Phase);
        Assert.Equal(sn.State.SentRawBytes, sn.State.SentWireBytes); // random data is sent as is
        rig.AssertIdentical(noise);
    }

    [Fact]
    public async Task Resume_AfterCancel_SkipsChunksAlreadyReceived()
    {
        await using var rig = new Rig();
        const long total = 40L << 20;
        var files = new[] { rig.AddFile("resume.bin", total, seed: 7) };
        var limiter = new RateLimiter { BytesPerSecond = 10L << 20 }; // slow enough to cancel in the middle

        var first = rig.NewSession(files, rig.Options(streams: 2, limiter: limiter));
        using var cts = new CancellationTokenSource(T);
        var run = first.RunAsync(cts.Token);
        await Wait.UntilAsync(() => first.State.DoneBytes > 12L << 20, TimeSpan.FromSeconds(30), "12 MiB acknowledged");
        cts.Cancel();
        await run;
        Assert.Equal(Phase.Cancelled, first.State.Phase);

        // the receiver saves its bitmap when the connection ends
        await Wait.UntilAsync(() => Directory.EnumerateFiles(rig.Dst, "*.lfsstate").Any(), TimeSpan.FromSeconds(15), "resume state");
        Assert.False(File.Exists(Path.Combine(rig.Dst, "resume.bin")));

        var second = rig.NewSession(files, rig.Options(streams: 2));
        await rig.RunAsync(second, T);
        Assert.Equal(Phase.Completed, second.State.Phase);
        Assert.True(second.State.SentRawBytes < total, "already received chunks must not be sent again");
        rig.AssertIdentical(files);
    }

    [Fact]
    public async Task PauseStopsTheFlow_ResumeFinishes()
    {
        await using var rig = new Rig();
        var files = new[] { rig.AddFile("pause.bin", 40L << 20, seed: 9) };
        var limiter = new RateLimiter { BytesPerSecond = 12L << 20 };

        var s = rig.NewSession(files, rig.Options(streams: 2, limiter: limiter));
        using var cts = new CancellationTokenSource(T);
        var run = s.RunAsync(cts.Token);
        await Wait.UntilAsync(() => s.State.DoneBytes > 6L << 20, TimeSpan.FromSeconds(30), "6 MiB acknowledged");

        s.Pause();
        await Task.Delay(1500); // frames already on the wire drain
        long frozen = s.State.DoneBytes;
        await Task.Delay(1500);
        Assert.Equal(Phase.Paused, s.State.Phase);
        Assert.True(s.State.DoneBytes - frozen <= 2L << 20, "data kept flowing while paused");

        s.Resume();
        await run;
        Assert.Equal(Phase.Completed, s.State.Phase);
        rig.AssertIdentical(files);
    }

    [Fact]
    public async Task DeclinedTransfer_WritesNothing()
    {
        await using var rig = new Rig { Accept = false };
        var files = new[] { rig.AddFile("no.bin", 100_000, seed: 3) };
        var s = rig.NewSession(files, rig.Options());
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Declined, s.State.Phase);
        Assert.Empty(Directory.GetFileSystemEntries(rig.Dst));
    }

    [Fact]
    public async Task ExistingFileConflict_RenameKeepsBothFiles()
    {
        await using var rig = new Rig();
        var source = rig.AddFile("same.bin", 160_000, seed: 41);
        var existing = Path.Combine(rig.Dst, "same.bin");
        TestData.WriteFile(existing, 160_000, seed: 42);

        var session = rig.NewSession([source], rig.Options());
        await rig.RunAsync(session, T);

        Assert.Equal(Phase.Completed, session.State.Phase);
        Assert.Equal(TestData.Sha256(existing), TestData.Sha256(Path.Combine(rig.Dst, "same.bin")));
        Assert.Equal(
            TestData.Sha256(source.Full),
            TestData.Sha256(Path.Combine(rig.Dst, "same (1).bin"))
        );
    }

    [Fact]
    public async Task ExistingFileConflict_KeepExistingSkipsIncomingBytes()
    {
        await using var rig = new Rig { ConflictHandling = ConflictHandling.KeepExisting };
        var source = rig.AddFile("same.bin", 160_000, seed: 43);
        var existing = Path.Combine(rig.Dst, "same.bin");
        TestData.WriteFile(existing, 160_000, seed: 44);
        var originalHash = TestData.Sha256(existing);

        var session = rig.NewSession([source], rig.Options());
        await rig.RunAsync(session, T);

        Assert.Equal(Phase.Completed, session.State.Phase);
        Assert.Equal(new FileInfo(source.Full).Length, session.State.DoneBytes);
        Assert.Equal(originalHash, TestData.Sha256(existing));
    }

    [Fact]
    public async Task ExistingFileConflict_ReplaceReplacesOnlyAfterTransfer()
    {
        await using var rig = new Rig { ConflictHandling = ConflictHandling.Replace };
        var source = rig.AddFile("same.bin", 160_000, seed: 45);
        var existing = Path.Combine(rig.Dst, "same.bin");
        TestData.WriteFile(existing, 160_000, seed: 46);

        var session = rig.NewSession([source], rig.Options());
        await rig.RunAsync(session, T);

        Assert.Equal(Phase.Completed, session.State.Phase);
        Assert.Equal(TestData.Sha256(source.Full), TestData.Sha256(existing));
        Assert.Single(Directory.GetFiles(rig.Dst));
    }

    [Fact]
    public async Task Pairing_IsAskedOnce_ThenRemembered()
    {
        var senderUi = new FixedPairingUi(true);
        var receiverUi = new FixedPairingUi(true);
        await using var rig = new Rig(senderUi, receiverUi);

        var f1 = new[] { rig.AddFile("one.bin", 100_000, seed: 1) };
        var s1 = rig.NewSession(f1, rig.Options());
        await rig.RunAsync(s1, T);
        Assert.Equal(Phase.Completed, s1.State.Phase);

        var f2 = new[] { rig.AddFile("two.bin", 100_000, seed: 2) };
        var s2 = rig.NewSession(f2, rig.Options());
        await rig.RunAsync(s2, T);
        Assert.Equal(Phase.Completed, s2.State.Phase);

        Assert.Equal(1, senderUi.Calls);
        Assert.Equal(1, receiverUi.Calls);
        Assert.Single(rig.SenderSecurity.Trust.Items);
        Assert.Single(rig.ReceiverSecurity.Trust.Items);
    }

    [Fact]
    public async Task PairingDeclinedByTheReceiver_BlocksTheTransfer()
    {
        await using var rig = new Rig(new FixedPairingUi(true), new FixedPairingUi(false));
        var files = new[] { rig.AddFile("blocked.bin", 100_000, seed: 4) };
        var s = rig.NewSession(files, rig.Options());
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Declined, s.State.Phase);
        Assert.Empty(Directory.GetFileSystemEntries(rig.Dst));
        Assert.Empty(rig.SenderSecurity.Trust.Items);
        Assert.Empty(rig.ReceiverSecurity.Trust.Items);
    }
}