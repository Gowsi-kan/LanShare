using System.Diagnostics;
using LanShare.Core;
using LanShare.Web;

namespace LanShare.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task Unlimited_NeverWaits()
    {
        var limiter = new RateLimiter();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) await limiter.WaitAsync(1 << 20, default);
        Assert.True(sw.ElapsedMilliseconds < 500);
    }

    [Fact]
    public async Task Limit_IsRespected()
    {
        var limiter = new RateLimiter { BytesPerSecond = 4 << 20 };
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 12; i++) await limiter.WaitAsync(1 << 20, default); // 12 MiB at 4 MiB/s, the first 4 MiB are free
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.5, 8);
    }

    [Fact]
    public async Task RemovingTheLimit_ReleasesWaitersQuickly()
    {
        var limiter = new RateLimiter { BytesPerSecond = 1024 };
        await limiter.WaitAsync(1024, default);                    // drains the initial bucket
        var waiter = limiter.WaitAsync(8 << 20, default).AsTask(); // would take hours at 1 KiB/s
        await Task.Delay(300);
        Assert.False(waiter.IsCompleted);

        limiter.BytesPerSecond = 0;
        await waiter.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

public class PauseGateTests
{
    [Fact]
    public async Task Gate_BlocksUntilResumed()
    {
        var gate = new PauseGate();
        gate.Set(true);
        var waiting = gate.WaitAsync(default);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        gate.Set(false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Gate_HonoursCancellation()
    {
        var gate = new PauseGate();
        gate.Set(true);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(cts.Token));
    }
}

public class TransferStateTests
{
    [Fact]
    public void ApplyPause_NeverOverwritesAFinishedTransfer()
    {
        var s = new TransferState(10, 1) { Phase = Phase.Completed };
        s.PausedLocal = true;
        s.ApplyPause();
        Assert.Equal(Phase.Completed, s.Phase);
    }

    [Fact]
    public void ApplyPause_TogglesWhileTransferring()
    {
        var s = new TransferState(10, 1) { Phase = Phase.Transferring };
        s.PausedByPeer = true;
        s.ApplyPause();
        Assert.Equal(Phase.Paused, s.Phase);

        s.PausedByPeer = false;
        s.ApplyPause();
        Assert.Equal(Phase.Transferring, s.Phase);
    }
}

public class PhoneFileNameTests
{
    [Theory]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("..\\..\\x.txt", "x.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("report. ", "report")]
    public void SafeName_Sanitizes(string input, string expected) =>
        Assert.Equal(expected, WebShareServer.SafeName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public void SafeName_RejectsEmpty(string? input) => Assert.Null(WebShareServer.SafeName(input));

    [Fact]
    public void SafeName_RejectsVeryLongNames() => Assert.Null(WebShareServer.SafeName(new string('x', 201)));
}