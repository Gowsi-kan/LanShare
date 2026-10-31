using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanShare.Core;

internal static class NetInfo
{
    public static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    /// <summary>Negotiated speed of the NIC owning the local endpoint (bits/s), 0 if unknown.</summary>
    public static long LinkBitsPerSecond(EndPoint? local)
    {
        if (local is not IPEndPoint ep)
            return 0;
        var addr = Normalize(ep.Address);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.Equals(addr))
                        return Math.Max(0, nic.Speed);
            }
        }
        catch (NetworkInformationException) { }
        return 0;
    }

    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (
                    nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                )
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (
                        ua.Address.AddressFamily != AddressFamily.InterNetwork
                        || ua.IPv4Mask is null
                    )
                        continue;
                    var a = ua.Address.GetAddressBytes();
                    var m = ua.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++)
                        a[i] |= (byte)~m[i];
                    result.Add(new IPAddress(a));
                }
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    internal static string Kind(NetworkInterfaceType t) =>
        t switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx => "Ethernet (wired)",
            _ => t.ToString(),
        };

    /// <summary>"Ethernet (Realtek…) · Ethernet (wired) · 1 Gbps · 192.168.1.10" for the adapter that carries this connection.</summary>
    public static string DescribeLink(EndPoint? local)
    {
        if (local is not IPEndPoint ep)
            return "unknown";
        var addr = Normalize(ep.Address);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.Equals(addr))
                    return $"{nic.Name} ({nic.Description}) · {Kind(nic.NetworkInterfaceType)} · "
                        + $"{(nic.Speed > 0 ? Format.LinkRate(nic.Speed) : "speed unknown")} · {addr}";
        }
        catch (NetworkInformationException) { }
        return addr.ToString();
    }
}

internal static class SocketTuning
{
    public static void Apply(Socket s)
    {
        try
        {
            s.NoDelay = true;
            if (OperatingSystem.IsWindows())
            {
                s.SendBufferSize = 4 * 1024 * 1024;
                s.ReceiveBufferSize = 4 * 1024 * 1024;
            }
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException) { }
    }
}
