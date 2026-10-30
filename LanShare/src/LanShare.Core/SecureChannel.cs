using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LanShare.Core;

internal static class Tls
{
    const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    public static async Task<SslStream> AsServerAsync(
        Stream inner,
        DeviceIdentity id,
        CancellationToken ct
    )
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = id.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = Protocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    // self-signed on purpose: the peer is identified by fingerprint (trust store / pairing) after the handshake
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                ct
            );
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }

    public static async Task<SslStream> AsClientAsync(
        Stream inner,
        DeviceIdentity id,
        CancellationToken ct
    )
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "lanshare",
                    ClientCertificates = new X509CertificateCollection { id.Certificate },
                    EnabledSslProtocols = Protocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                ct
            );
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }

    public static string PeerFingerprint(SslStream s) =>
        s.RemoteCertificate is { } c
            ? SecurityContext.Fingerprint(c)
            : throw new AuthenticationException("The peer sent no certificate.");
}

/// <summary>Runs right after TLS on the control connection: trust check, and pairing on first contact.</summary>
internal static class SecurityHandshake
{
    /// <returns>The peer's device name. Throws <see cref="PairingRejectedException"/> if pairing fails.</returns>
    public static async Task<string> RunAsync(
        Stream s,
        SecurityContext sec,
        string myName,
        string myFp,
        string peerFp,
        string peerAddress,
        Action<bool>? pairing,
        CancellationToken ct
    )
    {
        bool trustsPeer = sec.Trust.IsTrusted(peerFp);

        // 1) both sides send "do I trust you" + their name (small frames, so both writing first cannot deadlock)
        bool peerTrustsMe;
        string peerName;
        using (var tc = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            tc.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await Wire.WriteFrameAsync(
                    s,
                    w =>
                    {
                        w.Write(trustsPeer);
                        w.Write(myName);
                    },
                    tc.Token
                );
                using var r = await Wire.ReadFrameAsync(s, tc.Token);
                peerTrustsMe = r.ReadBoolean();
                peerName = Clean(r.ReadString());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("The other device did not answer the security handshake.");
            }
        }
        if (trustsPeer && peerTrustsMe)
            return peerName;

        // 2) first contact (or one side forgot the other): both users compare the same code
        if (sec.PairingUi is null)
            throw new PairingRejectedException(
                "Pairing is required but no pairing UI is available."
            );
        if (!sec.TryBeginPairing())
            throw new PairingRejectedException("Too many pairing requests.");
        try
        {
            pairing?.Invoke(true);
            var code = SecurityContext.PairingCode(myFp, peerFp);

            bool ok;
            using (var uc = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                uc.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    ok = await sec.PairingUi.ConfirmAsync(
                        new PairingRequest(peerName, peerAddress, code),
                        uc.Token
                    );
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    ok = false;
                }
            }

            await Wire.WriteFrameAsync(s, w => w.Write(ok), ct);

            bool peerOk;
            using (var rc = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                rc.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    using var r = await Wire.ReadFrameAsync(s, rc.Token);
                    peerOk = r.ReadBoolean();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new PairingRejectedException("The other device did not confirm in time.");
                }
            }

            if (!ok)
                throw new PairingRejectedException("Pairing cancelled.");
            if (!peerOk)
                throw new PairingRejectedException("The other device cancelled pairing.");

            sec.Trust.Add(peerFp, peerName, peerAddress);
            return peerName;
        }
        finally
        {
            pairing?.Invoke(false);
            sec.EndPairing();
        }
    }

    static string Clean(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? "Unknown device"
            : name.Length > 40 ? name[..40]
            : name;
    }
}
