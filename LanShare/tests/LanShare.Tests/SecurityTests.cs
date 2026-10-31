using LanShare.Core;

namespace LanShare.Tests;

public class SecurityTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lanshare-sec-" + Guid.NewGuid().ToString("N")[..8]);

    public SecurityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void PairingCode_IsSymmetric_EightDigits_AndDependsOnBothSides()
    {
        string a = new('a', 64), b = new('b', 64), c = new('c', 64);
        var code = SecurityContext.PairingCode(a, b);
        Assert.Equal(code, SecurityContext.PairingCode(b, a));
        Assert.Matches(@"^\d{4} \d{4}$", code);
        Assert.NotEqual(code, SecurityContext.PairingCode(a, c));
    }

    [Fact]
    public void TrustStore_PersistsAndForgets()
    {
        var path = Path.Combine(_dir, "trusted.json");
        var store = TrustStore.Load(path);
        store.Add("fp1", "Laptop", "10.0.0.2");
        Assert.True(TrustStore.Load(path).IsTrusted("fp1"));

        store.Remove("fp1");
        Assert.False(TrustStore.Load(path).IsTrusted("fp1"));
    }

    [Fact]
    public void DeviceIdentity_IsStableAcrossLoads()
    {
        var first = DeviceIdentity.LoadOrCreate(_dir);
        var second = DeviceIdentity.LoadOrCreate(_dir);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length); // SHA-256 as hex
    }
}