using System.Net;
using LanShare.Core;

namespace LanShare.Tests;

sealed class FixedPairingUi(bool answer) : IPairingUi
{
    public int Calls;

    public Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(answer);
    }
}

/// <summary>A receiver (real TransferService on loopback) plus helpers to create files and run senders against it.</summary>
sealed class Rig : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "lanshare-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public string Src => Path.Combine(Root, "src");
    public string Dst => Path.Combine(Root, "dst");
    public SecurityContext SenderSecurity { get; } = SecurityContext.CreateEphemeral();
    public SecurityContext ReceiverSecurity { get; } = SecurityContext.CreateEphemeral();
    public TransferService Receiver { get; }
    public bool Accept { get; set; } = true;

    public Rig(IPairingUi? senderUi = null, IPairingUi? receiverUi = null)
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
        if (senderUi is not null) SenderSecurity.PairingUi = senderUi;
        if (receiverUi is not null) ReceiverSecurity.PairingUi = receiverUi;

        Receiver = new TransferService("rig-receiver", ReceiverSecurity);
        Receiver.IncomingOffer += job =>
        {
            if (Accept) job.Accept(Dst);
            else job.Decline();
        };
        Receiver.Start();
    }

    public (string Full, string Rel) AddFile(string rel, long size, int seed, bool compressible = false)
    {
        var full = Path.Combine(Src, rel.Replace('/', Path.DirectorySeparatorChar));
        TestData.WriteFile(full, size, seed, compressible);
        return (full, rel);
    }

    public SendOptions Options(int streams = 2, bool compress = false, RateLimiter? limiter = null) =>
        new(streams, compress, limiter, SenderSecurity);

    public SenderSession NewSession(IEnumerable<(string Full, string Rel)> files, SendOptions options, int? port = null) =>
        new(new IPEndPoint(IPAddress.Loopback, port ?? Receiver.Port), "rig-sender", files, options);

    public async Task RunAsync(SenderSession session, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await session.RunAsync(cts.Token);
    }

    /// <summary>Every file exists, has the same length and SHA-256, and no temporary/resume files are left behind.</summary>
    public void AssertIdentical(IEnumerable<(string Full, string Rel)> files)
    {
        foreach (var (full, rel) in files)
        {
            var dst = Path.Combine(Dst, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(dst), $"missing on the receiver: {rel}");
            Assert.Equal(new FileInfo(full).Length, new FileInfo(dst).Length);
            Assert.Equal(TestData.Sha256(full), TestData.Sha256(dst));
        }

        var junk = Directory.EnumerateFiles(Dst, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".lfspart") || f.EndsWith(".lfsstate") || f.EndsWith(".tmp") ||
                        Path.GetFileName(f).StartsWith(".lfs-"))
            .ToList();
        Assert.Empty(junk);
    }

    public async ValueTask DisposeAsync()
    {
        await Receiver.DisposeAsync();
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}