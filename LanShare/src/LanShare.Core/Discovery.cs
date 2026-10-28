using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanShare.Core;

public sealed record Peer(Guid Id, string Name, IPAddress Address, int Port);

/// <summary>UDP broadcast beacon (every 2 s, per-NIC directed broadcast) + listener. Peers expire after 8 s.</summary>
public sealed class DiscoveryService(Guid selfId, string selfName, int tcpPort) : IDisposable
{
    readonly ConcurrentDictionary<Guid, (Peer Peer, long Seen)> _peers = new();
    readonly CancellationTokenSource _cts = new();
    const long ExpiryMs = 8000;

    public event Action? PeersChanged;

    public IReadOnlyList<Peer> Snapshot =>
        _peers
            .Values.Select(v => v.Peer)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Start()
    {
        var ct = _cts.Token;
        _ = Task.Run(() => Guard(() => BeaconLoopAsync(ct)));
        _ = Task.Run(() => Guard(() => ListenLoopAsync(ct)));
        _ = Task.Run(() => Guard(() => ExpiryLoopAsync(ct)));
    }

    public void AddManual(Peer peer)
    {
        _peers[peer.Id] = (peer, long.MaxValue);
        PeersChanged?.Invoke();
    }

    static async Task Guard(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    async Task BeaconLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var payload = Encoding.UTF8.GetBytes(
            $"LFS1|{selfId:N}|{selfName.Replace('|', ' ')}|{tcpPort}"
        );
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            foreach (var b in NetInfo.BroadcastAddresses().Append(IPAddress.Broadcast).Distinct())
            {
                try
                {
                    await udp.SendAsync(payload, new IPEndPoint(b, Protocol.DiscoveryPort), ct);
                }
                catch (SocketException) { }
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    async Task ListenLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct);
            }
            catch (SocketException)
            {
                await Task.Delay(200, ct);
                continue;
            }

            var parts = Encoding.UTF8.GetString(r.Buffer).Split('|');
            if (parts.Length != 4 || parts[0] != "LFS1")
                continue;
            if (!Guid.TryParseExact(parts[1], "N", out var id) || id == selfId)
                continue;
            if (!int.TryParse(parts[3], out var port) || port is < 1 or > 65535)
                continue;

            var peer = new Peer(id, parts[2], r.RemoteEndPoint.Address, port);
            bool changed = !_peers.TryGetValue(id, out var old) || old.Peer != peer;
            _peers[id] = (peer, Environment.TickCount64);
            if (changed)
                PeersChanged?.Invoke();
        }
    }

    async Task ExpiryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            bool removed = false;
            long now = Environment.TickCount64;
            foreach (var (id, v) in _peers)
                if (
                    v.Seen != long.MaxValue
                    && now - v.Seen > ExpiryMs
                    && _peers.TryRemove(id, out _)
                )
                    removed = true;
            if (removed)
                PeersChanged?.Invoke();
        }
    }

    public void Dispose() => _cts.Cancel();
}
