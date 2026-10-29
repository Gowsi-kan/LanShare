using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LanShare.Core;

public sealed class IncomingJob(Offer offer, string peer, TransferState state) : IPausable
{
    public Offer Offer { get; } = offer;
    public string Peer { get; } = peer;
    public TransferState State { get; } = state;

    internal Func<byte, Task>? ControlSender { get; set; }
    internal SemaphoreSlim ControlWriteGate { get; } = new(1, 1);

    internal TaskCompletionSource<string?> Decision { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource Cts { get; } = new();

    public void Accept(string saveDir) => Decision.TrySetResult(saveDir);

    public void Decline() => Decision.TrySetResult(null);

    public void Pause()
    {
        State.PausedLocal = true;
        State.ApplyPause();
        _ = ControlSender?.Invoke(Protocol.ControlPause);
    }

    public void Resume()
    {
        State.PausedLocal = false;
        State.ApplyPause();
        _ = ControlSender?.Invoke(Protocol.ControlResume);
    }

    public void Cancel()
    {
        Decision.TrySetResult(null);
        Cts.Cancel();
    }
}

/// <summary>TCP listener (control + data share one port) that dispatches incoming sessions.</summary>
public sealed class TransferService : IAsyncDisposable
{
    readonly ConcurrentDictionary<Guid, ReceiverSession> _sessions = new();
    readonly CancellationTokenSource _cts = new();
    Socket? _listener;
    Task? _accept;

    public Guid DeviceId { get; } = Guid.NewGuid();
    public string DeviceName { get; }
    public int Port { get; private set; }

    public event Action<IncomingJob>? IncomingOffer;

    public TransferService(string deviceName) => DeviceName = deviceName;

    public void Start()
    {
        _listener =
            Bind(Protocol.DefaultTcpPort)
            ?? Bind(0)
            ?? throw new IOException("Cannot open a listening socket.");
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _accept = Task.Run(AcceptLoopAsync);
    }

    static Socket? Bind(int port)
    {
        Socket? s = null;
        try
        {
            bool v6 = Socket.OSSupportsIPv6;
            s = new Socket(
                v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp
            );
            if (v6)
                s.DualMode = true;
            SocketTuning.Apply(s);
            s.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, port));
            s.Listen(128);
            return s;
        }
        catch (SocketException)
        {
            s?.Dispose();
            return null;
        }
    }

    async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(100, CancellationToken.None);
                continue;
            }
            _ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);
        }
    }

    async Task HandleConnectionAsync(Socket sock, CancellationToken ct)
    {
        try
        {
            SocketTuning.Apply(sock);
            using var ns = new NetworkStream(sock, ownsSocket: true);

            byte kind;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hs.CancelAfter(TimeSpan.FromSeconds(5));
                kind = await Wire.ReadHandshakeAsync(ns, hs.Token);
            }

            if (kind == Protocol.KindSession)
                await HandleControlAsync(sock, ns, ct);
            else if (kind == Protocol.KindData)
                await HandleDataAsync(ns, ct);
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Connection error: {e.Message}");
        }
    }

    async Task HandleDataAsync(NetworkStream ns, CancellationToken ct)
    {
        var idBytes = new byte[16];
        await ns.ReadExactlyAsync(idBytes, ct);
        if (!_sessions.TryGetValue(new Guid(idBytes), out var session))
            return;
        await session.ReceiveLoopAsync(ns, ct);
    }

    async Task HandleControlAsync(Socket sock, NetworkStream ns, CancellationToken serviceCt)
    {
        Offer offer;
        using (var r = await Wire.ReadFrameAsync(ns, serviceCt))
            offer = Wire.ReadOffer(r);

        var remote = sock.RemoteEndPoint is IPEndPoint rep
            ? NetInfo.Normalize(rep.Address).ToString()
            : "?";
        var state = new TransferState(offer.TotalBytes, offer.Files.Count)
        {
            Phase = Phase.AwaitingYou,
            LinkBps = NetInfo.LinkBitsPerSecond(sock.LocalEndPoint),
        };
        var job = new IncomingJob(offer, $"{offer.SenderName} ({remote})", state);

        job.ControlSender = b => SendControlAsync(ns, job, b);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt, job.Cts.Token);
        var token = cts.Token;
        var peerGone = ControlLoopAsync(ns, state, token); // completes only when the sender disconnects
        ReceiverSession? session = null;

        try
        {
            var handler = IncomingOffer;
            if (handler is null)
                job.Decline();
            else
                handler(job);

            var timeout = Task.Delay(TimeSpan.FromMinutes(2), token);
            var first = await Task.WhenAny(job.Decision.Task, peerGone, timeout);
            if (first == peerGone)
            {
                state.Error = "Sender disconnected.";
                state.Phase = Phase.Cancelled;
                return;
            }

            var dir = first == job.Decision.Task ? await job.Decision.Task : null;
            if (dir is null)
            {
                await Wire.WriteFrameAsync(ns, w => w.Write((byte)Decision.Decline), token);
                state.Phase = Phase.Declined;
                return;
            }

            try
            {
                session = await Task.Run(() => ReceiverSession.Prepare(offer, dir, state), token);
            }
            catch (InsufficientSpaceException e)
            {
                state.Error = e.Message;
                state.Phase = Phase.Failed;
                await Wire.WriteFrameAsync(ns, w => w.Write((byte)Decision.NoSpace), token);
                return;
            }

            _sessions[offer.SessionId] = session;
            var maps = session.SnapshotDone();
            await Wire.WriteFrameAsync(
                ns,
                w =>
                {
                    w.Write((byte)Decision.Accept);
                    foreach (var m in maps)
                        w.Write(m);
                },
                token
            );
            state.Phase = Phase.Transferring;
            state.ApplyPause();
            long lastBytes = state.DoneBytes;
            long lastChange = Stopwatch.GetTimestamp();
            long lastStreams = lastChange;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (true)
            {
                var tick = timer.WaitForNextTickAsync(token).AsTask();
                var winner = await Task.WhenAny(session.AllDone, peerGone, tick);
                if (winner == session.AllDone)
                    break;
                if (winner == peerGone)
                    throw new IOException("Sender disconnected.");
                await Task.Run(
                    () => session.Persist(TimeSpan.FromSeconds(1.5)),
                    CancellationToken.None
                );

                // watchdogs: a sender that is alive on the control connection but never delivers must not hang us forever
                long nowTs = Stopwatch.GetTimestamp();
                if (state.DoneBytes != lastBytes)
                {
                    lastBytes = state.DoneBytes;
                    lastChange = nowTs;
                }
                if (state.ActiveStreams > 0)
                    lastStreams = nowTs;
                if (state.IsPaused)
                {
                    lastChange = nowTs;
                    lastStreams = nowTs;
                } // a pause must not trip the idle watchdogs
                if (Stopwatch.GetElapsedTime(lastStreams, nowTs).TotalSeconds > 90)
                    throw new IOException("Sender opened no data streams for 90 s.");
                if (
                    state.DoneBytes < state.TotalBytes
                    && Stopwatch.GetElapsedTime(lastChange, nowTs).TotalMinutes > 5
                )
                    throw new IOException("No data received for 5 minutes.");
            }

            await session.AllDone;
            session.FinishCleanup();
            await job.ControlWriteGate.WaitAsync(token);
            try
            {
                await ns.WriteAsync(new byte[] { Protocol.Done }, token);
            }
            finally
            {
                job.ControlWriteGate.Release();
            }
            state.Phase = Phase.Completed;
        }
        catch (OperationCanceledException)
        {
            state.Phase = Phase.Cancelled;
        }
        catch (Exception e)
        {
            state.Error = e.Message;
            state.Phase = Phase.Failed;
        }
        finally
        {
            _sessions.TryRemove(offer.SessionId, out _);
            session?.Dispose();
            cts.Cancel();
        }
    }

    static async Task ControlLoopAsync(NetworkStream ns, TransferState state, CancellationToken ct)
    {
        var one = new byte[1];
        while (true)
        {
            if (await ns.ReadAsync(one, ct) == 0)
                return; // sender closed the control connection
            if (one[0] == Protocol.ControlPause)
            {
                state.PausedByPeer = true;
                state.ApplyPause();
            }
            else if (one[0] == Protocol.ControlResume)
            {
                state.PausedByPeer = false;
                state.ApplyPause();
            }
        }
    }

    static async Task SendControlAsync(NetworkStream ns, IncomingJob job, byte b)
    {
        try
        {
            await job.ControlWriteGate.WaitAsync();
            try
            {
                await ns.WriteAsync(new[] { b });
            }
            finally
            {
                job.ControlWriteGate.Release();
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Dispose();
        if (_accept is not null)
        {
            try
            {
                await _accept;
            }
            catch { }
        }
        foreach (var s in _sessions.Values)
            s.Dispose();
    }
}
