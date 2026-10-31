using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace LanShare.Core;

public static class SystemInfo
{
    static string? _cache;
    static string _cacheDir = "";
    static DateTime _cacheTime;

    /// <summary>OS / CPU / adapters / receive-disk description. Cached for 2 minutes (the disk probe can take a few seconds).</summary>
    public static async Task<string> DescribeAsync(string downloadDir)
    {
        if (
            _cache is not null
            && _cacheDir == downloadDir
            && DateTime.UtcNow - _cacheTime < TimeSpan.FromMinutes(2)
        )
            return _cache;

        var sb = new StringBuilder();
        sb.AppendLine("=== LanShare diagnostics ===");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine(
            $"App: LanShare {typeof(SystemInfo).Assembly.GetName().Version} · {RuntimeInformation.FrameworkDescription} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}"
        );
        sb.AppendLine(
            $"OS: {RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture} · {Environment.ProcessorCount} logical cores"
        );
        sb.AppendLine(
            $"AES hardware acceleration (encryption speed): {(AesAccelerated() ? "yes" : "NO")}"
        );
        sb.AppendLine("Network adapters that are up:");
        foreach (var l in Adapters())
            sb.AppendLine("  - " + l);
        sb.AppendLine("Receive folder disk: " + await DescribeDiskAsync(downloadDir));

        _cache = sb.ToString().TrimEnd();
        _cacheDir = downloadDir;
        _cacheTime = DateTime.UtcNow;
        return _cache;
    }

    /// <summary>"C:\ · NTFS · Fixed · 120 GB free of 476 GB · media: SSD" (no folder or file names).</summary>
    public static async Task<string> DescribeDiskAsync(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return "unknown";
        }

        var sb = new StringBuilder();
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(full)!);
            sb.Append(
                $"{d.Name} · {d.DriveFormat} · {d.DriveType} · {Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)}"
            );
        }
        catch
        {
            sb.Append("(drive info unavailable)");
        }

        var kind = await DiskKindAsync(full);
        if (kind.Length > 0)
            sb.Append(" · ").Append(kind);
        return sb.ToString();
    }

    static bool AesAccelerated() =>
        System.Runtime.Intrinsics.X86.Aes.IsSupported
        || System.Runtime.Intrinsics.Arm.Aes.IsSupported;

    static IEnumerable<string> Adapters()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (
                    nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType
                        is NetworkInterfaceType.Loopback
                            or NetworkInterfaceType.Tunnel
                )
                    continue;
                var ips = nic.GetIPProperties()
                    .UnicastAddresses.Where(a =>
                        a.Address.AddressFamily == AddressFamily.InterNetwork
                    )
                    .Select(a => a.Address.ToString());
                list.Add(
                    $"{nic.Name} ({nic.Description}) · {NetInfo.Kind(nic.NetworkInterfaceType)} · "
                        + $"{(nic.Speed > 0 ? Format.LinkRate(nic.Speed) : "speed unknown")} · {string.Join(", ", ips)}"
                );
            }
        }
        catch (NetworkInformationException) { }
        return list.Count > 0 ? list : ["(none)"];
    }

    static async Task<string> DiskKindAsync(string full)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(full);
                if (root is { Length: >= 2 } && char.IsAsciiLetter(root[0]) && root[1] == ':')
                {
                    var o = (
                        await RunAsync(
                            "powershell",
                            8,
                            "-NoProfile",
                            "-NonInteractive",
                            "-Command",
                            $"(Get-Partition -DriveLetter {root[0]} | Get-Disk | Get-PhysicalDisk).MediaType"
                        )
                    ).Trim();
                    return o.Length > 0
                        ? "media: " + o.Replace('\r', ' ').Replace('\n', ' ')
                        : "media: unknown";
                }
                return "";
            }

            if (OperatingSystem.IsLinux())
            {
                var src = (await RunAsync("findmnt", 5, "-no", "SOURCE", "--target", full)).Trim();
                if (!src.StartsWith("/dev/"))
                    return src.Length > 0 ? $"source: {src}" : "";
                var parent = (await RunAsync("lsblk", 5, "-no", "PKNAME", src))
                    .Trim()
                    .Split('\n')[0]
                    .Trim();
                var name = parent.Length > 0 ? parent : Path.GetFileName(src);
                var rotFile = $"/sys/block/{name}/queue/rotational";
                var rot = File.Exists(rotFile) ? File.ReadAllText(rotFile).Trim() : "";
                var kind =
                    rot == "1" ? "HDD (rotational)"
                    : rot == "0" ? (name.StartsWith("nvme") ? "NVMe SSD" : "SSD")
                    : "type unknown";
                return $"{kind} ({name})";
            }
        }
        catch { }
        return "";
    }

    static async Task<string> RunAsync(string file, int timeoutSec, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null)
                return "";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var output = p.StandardOutput.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(true);
                }
                catch { }
                return "";
            }
            return await output;
        }
        catch
        {
            return "";
        }
    }
}
