using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LanShare.Core;

public sealed record PairingRequest(string PeerName, string PeerAddress, string Code);

public interface IPairingUi
{
    /// <summary>Shows the code. true = the user confirms it is identical on both devices. Must honour ct (close the dialog).</summary>
    Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct);
}

sealed class AutoPairingUi : IPairingUi
{
    public Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct) =>
        Task.FromResult(true);
}

public sealed class PairingRejectedException(string message) : Exception(message);

public sealed record TrustedDevice(
    string Fingerprint,
    string Name,
    DateTime AddedUtc,
    string Address
);

/// <summary>This device's permanent identity: a self-signed ECDSA P-256 certificate.</summary>
public sealed class DeviceIdentity
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    DeviceIdentity(X509Certificate2 cert)
    {
        Certificate = cert;
        Fingerprint = SecurityContext.Fingerprint(cert);
    }

    public static DeviceIdentity LoadOrCreate(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "identity.pfx");
        try
        {
            if (File.Exists(path))
                return new DeviceIdentity(X509CertificateLoader.LoadPkcs12FromFile(path, null));
        }
        catch (Exception e) when (e is CryptographicException or IOException)
        {
            // unreadable: create a new identity (previously paired devices must pair again)
        }

        var pfx = CreatePfx();
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, pfx);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
        return new DeviceIdentity(X509CertificateLoader.LoadPkcs12(pfx, null));
    }

    public static DeviceIdentity CreateEphemeral() =>
        new(X509CertificateLoader.LoadPkcs12(CreatePfx(), null));

    static byte[] CreatePfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            $"CN=LanShare {Environment.MachineName}",
            key,
            HashAlgorithmName.SHA256
        );
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false)
        );
        var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }; // server + client auth
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(20)
        );
        return cert.Export(X509ContentType.Pfx); // exported and re-imported: ephemeral keys do not work with Windows SChannel
    }
}

/// <summary>Fingerprints of devices the user has paired with.</summary>
public sealed class TrustStore
{
    readonly object _gate = new();
    readonly string? _path;
    readonly List<TrustedDevice> _items = [];

    public event Action? Changed;

    TrustStore(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path))
            return;
        try
        {
            _items.AddRange(
                JsonSerializer.Deserialize<List<TrustedDevice>>(File.ReadAllText(path)) ?? []
            );
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    public static TrustStore Load(string path) => new(path);

    public static TrustStore InMemory() => new(null);

    public IReadOnlyList<TrustedDevice> Items
    {
        get
        {
            lock (_gate)
                return _items.ToList();
        }
    }

    public bool IsTrusted(string fingerprint)
    {
        lock (_gate)
            return _items.Any(x => x.Fingerprint == fingerprint);
    }

    public void Add(string fingerprint, string name, string address)
    {
        lock (_gate)
        {
            _items.RemoveAll(x => x.Fingerprint == fingerprint);
            _items.Add(new TrustedDevice(fingerprint, name, DateTime.UtcNow, address));
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(string fingerprint)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(x => x.Fingerprint == fingerprint) > 0;
            if (removed)
                Save();
        }
        if (removed)
            Changed?.Invoke();
    }

    void Save()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true })
            );
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
    }
}

/// <summary>Identity + trust store + pairing UI, shared by the sender and the receiver.</summary>
public sealed class SecurityContext(
    DeviceIdentity identity,
    TrustStore trust,
    IPairingUi? pairingUi = null
)
{
    int _pairingBusy;

    public DeviceIdentity Identity { get; } = identity;
    public TrustStore Trust { get; } = trust;
    public IPairingUi? PairingUi { get; set; } = pairingUi;

    public static string Fingerprint(
        System.Security.Cryptography.X509Certificates.X509Certificate cert
    ) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();

    /// <summary>Throw-away identity that trusts everyone (benchmarks / tests only).</summary>
    public static SecurityContext CreateEphemeral() =>
        new(DeviceIdentity.CreateEphemeral(), TrustStore.InMemory(), new AutoPairingUi());

    /// <summary>8-digit code derived from both fingerprints (same on both sides, "1234 5678").</summary>
    public static string PairingCode(string fpA, string fpB)
    {
        var (x, y) = string.CompareOrdinal(fpA, fpB) <= 0 ? (fpA, fpB) : (fpB, fpA);
        var h = SHA256.HashData(Encoding.ASCII.GetBytes($"lanshare-pairing-v1|{x}|{y}"));
        var s = (BinaryPrimitives.ReadUInt64BigEndian(h) % 100_000_000UL).ToString("D8");
        return $"{s[..4]} {s[4..]}";
    }

    // at most 3 pairing prompts at once: a hostile device on the LAN cannot flood the screen
    internal bool TryBeginPairing()
    {
        if (Interlocked.Increment(ref _pairingBusy) <= 3)
            return true;
        Interlocked.Decrement(ref _pairingBusy);
        return false;
    }

    internal void EndPairing() => Interlocked.Decrement(ref _pairingBusy);
}
