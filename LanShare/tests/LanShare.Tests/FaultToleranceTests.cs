using System.Net;
using LanShare.Core;

namespace LanShare.Tests;

public class FaultToleranceTests
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task DroppedConnections_AreRetried_FileStaysIdentical()
    {
        await using var rig = new Rig();
        await using var proxy = new FaultProxy(new IPEndPoint(IPAddress.Loopback, rig.Receiver.Port))
        {
            DropAfterBytes = 3L << 20,
            MaxFaults = 3,
        };
        var files = new[] { rig.AddFile("big.bin", 24L << 20, seed: 3) };

        var s = rig.NewSession(files, rig.Options(streams: 3), proxy.Port);
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Completed, s.State.Phase);
        Assert.True(proxy.FaultsInjected >= 1, "the proxy never dropped a connection");
        Assert.True(s.State.Retries >= 1, "the sender should have retried");
        rig.AssertIdentical(files);
    }

    [Fact]
    public async Task FlippedByte_IsDetected_AndTheTransferRecovers()
    {
        await using var rig = new Rig();
        await using var proxy = new FaultProxy(new IPEndPoint(IPAddress.Loopback, rig.Receiver.Port))
        {
            FlipAtByte = 2L << 20,
            MaxFaults = 2,
        };
        var files = new[] { rig.AddFile("big.bin", 16L << 20, seed: 4) };

        var s = rig.NewSession(files, rig.Options(streams: 2), proxy.Port);
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Completed, s.State.Phase);
        Assert.True(proxy.FaultsInjected >= 1, "the proxy never corrupted a byte");
        rig.AssertIdentical(files); // TLS rejects the corrupted record, the sender reconnects and resends
    }

    [Fact]
    public async Task HighLatency_StillCorrect()
    {
        await using var rig = new Rig();
        await using var proxy = new FaultProxy(new IPEndPoint(IPAddress.Loopback, rig.Receiver.Port)) { LatencyMs = 3 };
        var files = new[] { rig.AddFile("lat.bin", 6L << 20, seed: 5), rig.AddFile("lat2.txt", 300_000, seed: 6, compressible: true) };

        var s = rig.NewSession(files, rig.Options(streams: 2, compress: true), proxy.Port);
        await rig.RunAsync(s, T);

        Assert.Equal(Phase.Completed, s.State.Phase);
        rig.AssertIdentical(files);
    }
}