using System.ComponentModel;
using System.Diagnostics;

namespace LanShare.Core;

public enum FirewallState
{
    Open,
    Closed,
    Unknown,
    NotNeeded,
}

public sealed record FirewallStatus(FirewallState State, string Message);

/// <summary>
/// Checks / opens the inbound ports LanShare needs (TCP transfer port + UDP discovery port).
/// Windows: netsh advfirewall (UAC elevation, Private+Domain profiles).
/// Linux: firewalld or ufw via pkexec (polkit prompt). Other firewalls are left untouched.
/// </summary>
public static class FirewallHelper
{
    const string TcpRule = "LanShare-TCP";
    const string UdpRule = "LanShare-UDP";

    public static async Task<FirewallStatus> CheckAsync(int tcpPort)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return await CheckWindowsAsync(tcpPort);
            if (OperatingSystem.IsLinux())
                return await CheckLinuxAsync(tcpPort);
        }
        catch (Exception e)
        {
            return new(FirewallState.Unknown, $"Could not check the firewall: {e.Message}");
        }
        return new(
            FirewallState.NotNeeded,
            "No firewall setup needed on this OS (allow the prompt if asked)."
        );
    }

    public static async Task<FirewallStatus> OpenAsync(int tcpPort)
    {
        int udp = Protocol.DiscoveryPort;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var args =
                    "/c \""
                    + $"netsh advfirewall firewall delete rule name={TcpRule} >nul 2>&1 & "
                    + $"netsh advfirewall firewall delete rule name={UdpRule} >nul 2>&1 & "
                    + $"netsh advfirewall firewall add rule name={TcpRule} dir=in action=allow protocol=TCP localport={tcpPort} profile=private,domain && "
                    + $"netsh advfirewall firewall add rule name={UdpRule} dir=in action=allow protocol=UDP localport={udp} profile=private,domain\"";

                var psi = new ProcessStartInfo("cmd.exe", args)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using var p = Process.Start(psi);
                if (p is null)
                    return new(FirewallState.Closed, "Could not start the elevated process.");
                using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await p.WaitForExitAsync(wait.Token);
                }
                catch (OperationCanceledException)
                {
                    return new(
                        FirewallState.Closed,
                        "Timed out waiting for the administrator prompt."
                    );
                }
                if (p.ExitCode != 0)
                    return new(
                        FirewallState.Closed,
                        "Adding the firewall rules failed. Run LanShare as administrator and retry."
                    );
                return await CheckWindowsAsync(tcpPort);
            }

            if (OperatingSystem.IsLinux())
            {
                string script;
                if (await IsActiveAsync("firewalld"))
                    script =
                        $"firewall-cmd --permanent --add-port={tcpPort}/tcp --add-port={udp}/udp && firewall-cmd --reload";
                else if (await IsActiveAsync("ufw"))
                    script = $"ufw allow {tcpPort}/tcp && ufw allow {udp}/udp";
                else
                    return new(
                        FirewallState.NotNeeded,
                        "No active ufw/firewalld detected; nothing to open."
                    );

                var r = await RunAsync("pkexec", $"sh -c \"{script}\"", 120);
                if (r.ExitCode != 0)
                    return new(
                        FirewallState.Closed,
                        $"Could not elevate (pkexec missing or denied). Run manually:\nsudo sh -c \"{script}\""
                    );

                var after = await CheckLinuxAsync(tcpPort);
                return after.State == FirewallState.Unknown
                    ? new(
                        FirewallState.Open,
                        "ufw rules added for the transfer and discovery ports."
                    )
                    : after;
            }
        }
        catch (Win32Exception)
        {
            return new(FirewallState.Closed, "Administrator permission was denied.");
        }
        catch (Exception e)
        {
            return new(FirewallState.Closed, $"Firewall change failed: {e.Message}");
        }
        return new(FirewallState.NotNeeded, "No firewall setup needed on this OS.");
    }

    /// <summary>Opens the browser-access (HTTP) port. Same elevation approach as OpenAsync.</summary>
    public static async Task<FirewallStatus> OpenWebPortAsync(int port)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var args = "/c \"" +
                    "netsh advfirewall firewall delete rule name=LanShare-WEB >nul 2>&1 & " +
                    $"netsh advfirewall firewall add rule name=LanShare-WEB dir=in action=allow protocol=TCP localport={port} profile=private,domain\"";
                using var p = Process.Start(new ProcessStartInfo("cmd.exe", args)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p is null) return new(FirewallState.Closed, "Could not start the elevated process.");
                using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await p.WaitForExitAsync(wait.Token);
                return p.ExitCode == 0
                    ? new(FirewallState.Open, $"Windows Firewall: TCP {port} allowed (Private/Domain networks).")
                    : new(FirewallState.Closed, "Adding the firewall rule failed.");
            }

            if (OperatingSystem.IsLinux())
            {
                string script;
                if (await IsActiveAsync("firewalld")) script = $"firewall-cmd --permanent --add-port={port}/tcp && firewall-cmd --reload";
                else if (await IsActiveAsync("ufw")) script = $"ufw allow {port}/tcp";
                else return new(FirewallState.NotNeeded, "No active ufw/firewalld detected; nothing to open.");

                var r = await RunAsync("pkexec", $"sh -c \"{script}\"", 120);
                return r.ExitCode == 0
                    ? new(FirewallState.Open, $"TCP {port} opened.")
                    : new(FirewallState.Closed, $"Could not elevate. Run manually:\nsudo sh -c \"{script}\"");
            }
        }
        catch (Win32Exception) { return new(FirewallState.Closed, "Administrator permission was denied."); }
        catch (OperationCanceledException) { return new(FirewallState.Closed, "Timed out waiting for the administrator prompt."); }
        catch (Exception e) { return new(FirewallState.Closed, $"Firewall change failed: {e.Message}"); }
        return new(FirewallState.NotNeeded, "No firewall setup needed on this OS.");
    }

    static async Task<FirewallStatus> CheckWindowsAsync(int tcpPort)
    {
        var tcp = await RunAsync("netsh", $"advfirewall firewall show rule name={TcpRule}");
        var udp = await RunAsync("netsh", $"advfirewall firewall show rule name={UdpRule}");
        bool open =
            tcp.ExitCode == 0
            && tcp.Output.Contains(tcpPort.ToString())
            && udp.ExitCode == 0
            && udp.Output.Contains(Protocol.DiscoveryPort.ToString());
        return open
            ? new(
                FirewallState.Open,
                $"Windows Firewall: TCP {tcpPort} and UDP {Protocol.DiscoveryPort} allowed (Private/Domain networks)."
            )
            : new(
                FirewallState.Closed,
                $"Windows Firewall is blocking TCP {tcpPort} / UDP {Protocol.DiscoveryPort}. Other laptops may not find or reach this one."
            );
    }

    static async Task<FirewallStatus> CheckLinuxAsync(int tcpPort)
    {
        if (await IsActiveAsync("firewalld"))
        {
            var t = await RunAsync("firewall-cmd", $"--query-port={tcpPort}/tcp");
            var u = await RunAsync("firewall-cmd", $"--query-port={Protocol.DiscoveryPort}/udp");
            bool open =
                t.ExitCode == 0
                && t.Output.Trim().StartsWith("yes", StringComparison.Ordinal)
                && u.ExitCode == 0
                && u.Output.Trim().StartsWith("yes", StringComparison.Ordinal);
            return open
                ? new(
                    FirewallState.Open,
                    $"firewalld: TCP {tcpPort} and UDP {Protocol.DiscoveryPort} are open."
                )
                : new(
                    FirewallState.Closed,
                    $"firewalld is active and TCP {tcpPort} / UDP {Protocol.DiscoveryPort} are closed."
                );
        }
        if (await IsActiveAsync("ufw"))
            return new(
                FirewallState.Unknown,
                "ufw is active (its rules can't be read without root). Click to apply the rules."
            );

        return new(
            FirewallState.NotNeeded,
            "No active ufw/firewalld detected; ports should be reachable."
        );
    }

    static async Task<bool> IsActiveAsync(string unit) =>
        (await RunAsync("systemctl", $"is-active --quiet {unit}")).ExitCode == 0;

    static async Task<(int ExitCode, string Output)> RunAsync(
        string file,
        string args,
        int timeoutSec = 15
    )
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p = Process.Start(psi);
            if (p is null)
                return (-1, "");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await outTask + await errTask);
        }
        catch (Exception e)
            when (e is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            try
            {
                if (p is { HasExited: false })
                    p.Kill(true);
            }
            catch { }
            return (-1, e.Message);
        }
        finally
        {
            p?.Dispose();
        }
    }
}
