using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanShare.Web;

public static class LanAddress
{
    static readonly string[] VirtualHints =
    [
        "vethernet",
        "vmware",
        "virtualbox",
        "docker",
        "veth",
        "virbr",
        "br-",
        "hyper-v",
        "wsl",
    ];

    /// <summary>Local IPv4 addresses a phone could reach, best first (has a gateway, not virtual).</summary>
    public static IReadOnlyList<string> Candidates()
    {
        var found = new List<(string Ip, bool Gateway, bool Virtual)>();
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

                var props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any)
                );
                var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
                bool isVirtual = VirtualHints.Any(label.Contains);

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("127.") || s.StartsWith("169.254."))
                        continue;
                    found.Add((s, gateway, isVirtual));
                }
            }
        }
        catch (NetworkInformationException) { }

        return found
            .OrderByDescending(x => x.Gateway)
            .ThenBy(x => x.Virtual)
            .Select(x => x.Ip)
            .Distinct()
            .ToList();
    }
}
