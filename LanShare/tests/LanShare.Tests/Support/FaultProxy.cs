using System.Net;
using System.Net.Sockets;

namespace LanShare.Tests;

/// <summary>
/// TCP man-in-the-middle for tests. Drops connections, flips a byte (TLS then kills the connection) and adds latency.
/// Connection #0 (the control connection) is never touched; faults are limited by <see cref="MaxFaults"/> so a retrying sender can finish.
/// </summary>
sealed class FaultProxy : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly IPEndPoint _upstream;
    readonly CancellationTokenSource _cts = new();
    readonly Task _accept;
    readonly List<TcpClient> _open = [];
    int _connections, _faults;

    public long DropAfterBytes { get; init; }   // drop a data connection after this many bytes (0 = off)
    public long FlipAtByte { get; init; }       // flip one byte at this offset of a data connection (0 = off)
    public int MaxFaults { get; init; } = 3;
    public int LatencyMs { get; init; }         // delay for every forwarded buffer, both directions
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int FaultsInjected => Volatile.Read(ref _faults);

    public FaultProxy(IPEndPoint upstream)
    {
        _upstream = upstream;
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            int index = Interlocked.Increment(ref _connections) - 1;
            _ = Task.Run(() => HandleAsync(client, index));
        }
    }

    async Task HandleAsync(TcpClient client, int index)
    {
        var server = new TcpClient();
        try
        {
            await server.ConnectAsync(_upstream, _cts.Token);
            lock (_open)
            {
                _open.Add(client);
                _open.Add(server);
            }
            await Task.WhenAll(
                PumpAsync(client, server, injectFaults: index >= 1), // sender -> receiver: where the data flows
                PumpAsync(server, client, injectFaults: false));
        }
        catch { }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    async Task PumpAsync(TcpClient from, TcpClient to, bool injectFaults)
    {
        var buf = new byte[16 * 1024];
        long forwarded = 0;
        bool flipped = false;
        try
        {
            var src = from.GetStream();
            var dst = to.GetStream();
            while (true)
            {
                int n = await src.ReadAsync(buf, _cts.Token);
                if (n == 0) break;
                if (LatencyMs > 0) await Task.Delay(LatencyMs, _cts.Token);

                if (injectFaults)
                {
                    if (DropAfterBytes > 0 && forwarded >= DropAfterBytes && TryTakeFault())
                    {
                        Kill(from, to);
                        return;
                    }
                    if (FlipAtByte > 0 && !flipped && forwarded + n > FlipAtByte && TryTakeFault())
                    {
                        buf[(int)Math.Max(0, FlipAtByte - forwarded)] ^= 0xFF;
                        flipped = true;
                    }
                }

                await dst.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                forwarded += n;
            }
            try { to.Client.Shutdown(SocketShutdown.Send); } catch { } // clean EOF: pass the half-close on
        }
        catch
        {
            Kill(from, to);
        }
    }

    static void Kill(TcpClient a, TcpClient b)
    {
        try { a.Dispose(); } catch { }
        try { b.Dispose(); } catch { }
    }

    bool TryTakeFault()
    {
        if (Interlocked.Increment(ref _faults) <= MaxFaults) return true;
        Interlocked.Decrement(ref _faults);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_open)
        {
            foreach (var c in _open) { try { c.Dispose(); } catch { } }
        }
        try { await _accept; } catch { }
    }
}