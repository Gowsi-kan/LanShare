using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanShare.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanShare.Web;

public sealed record SharedFile(string Id, string Path, string Name, long Size);

public sealed record UploadFile(string Name, long Size);

public sealed record UploadRequest(string Peer, string Device, IReadOnlyList<UploadFile> Files);

/// <summary>One browser transfer (upload from phone or download to phone). State is shown in the desktop UI.</summary>
public sealed class WebUpload
{
    public required string FileName { get; init; }
    public required string Peer { get; init; }
    public required TransferState State { get; init; }
    public bool IsDownload { get; init; }
    internal CancellationTokenSource Cts { get; } = new();
    internal Action? OnCancel { get; set; }

    public void Cancel()
    {
        Cts.Cancel();
        OnCancel?.Invoke();
    }
}

/// <summary>
/// Browser access: token-protected, private-network-only HTTP server on its own port.
/// Completely separate from the PC-to-PC protocol (different port, listener, threads).
/// </summary>
public sealed class WebShareServer(string deviceName, Func<string> downloadDir) : IAsyncDisposable
{
    public const int DefaultPort = 45480;
    const int MaxConcurrentUploads = 4;
    const int SliceBytes = 4 * 1024 * 1024; // download slice: progress + cancel granularity, still sendfile-style

    sealed class UploadSlot(WebUpload upload, string part)
    {
        public WebUpload Upload { get; } = upload;
        public string Part { get; } = part;
        public SemaphoreSlim Busy { get; } = new(1, 1);
    }

    sealed class DownloadSlot(WebUpload upload)
    {
        public WebUpload Upload { get; } = upload;
        public int Active; // concurrent requests for this download
    }

    readonly record struct UploadQuery(string Cid, string Name, long Size, long Offset, string Key);

    readonly ConcurrentDictionary<string, SharedFile> _shared = new();
    readonly Dictionary<string, UploadSlot> _slots = new();
    readonly Dictionary<string, DownloadSlot> _downloads = new();
    readonly object _slotsGate = new();
    readonly SemaphoreSlim _uploadGate = new(MaxConcurrentUploads);
    WebApplication? _app;
    string _indexHtml = "";

    public int Port { get; private set; }
    public string Token { get; private set; } = NewToken();
    public bool IsRunning => _app is not null;

    /// <summary>Raised for every new browser transfer, uploads and downloads (see <see cref="WebUpload.IsDownload"/>).</summary>
    public event Action<WebUpload>? UploadStarted;

    public IReadOnlyList<SharedFile> Shared =>
        [.. _shared.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];

    sealed record SessionInfo(string Ip, DateTime Created);

    sealed class Approval(string ip, UploadRequest request)
    {
        public string Ip { get; } = ip;
        public UploadRequest Request { get; } = request;
        public DateTime Created { get; } = DateTime.UtcNow;
        public volatile int State; // 0 pending, 1 approved, 2 declined

        public bool Allows(string name, long size) =>
            Request.Files.Any(f => f.Name == name && f.Size == size);
    }

    readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    readonly ConcurrentDictionary<string, Approval> _approvals = new();
    readonly ConcurrentDictionary<string, (int Fails, DateTime LockedUntil)> _failures = new();
    int _totalFailures;

    public string Pin { get; private set; } = NewPin();
    public bool UseHttps { get; private set; }
    public event Action<string>? PinChanged;

    /// <summary>Asked once per upload batch. null = approve automatically.</summary>
    public Func<UploadRequest, CancellationToken, Task<bool>>? ApproveUploads { get; set; }

    // ------------------------------------------------------------ shared files (PC -> phone)

    public SharedFile? AddShared(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists)
            return null;
        var f = new SharedFile(Guid.NewGuid().ToString("N")[..12], fi.FullName, fi.Name, fi.Length);
        _shared[f.Id] = f;
        return f;
    }

    public void RemoveShared(string id) => _shared.TryRemove(id, out _);

    // ------------------------------------------------------------ lifecycle

    public async Task StartAsync(int port = DefaultPort, X509Certificate2? httpsCert = null)
    {
        if (_app is not null)
            return;
        Token = NewToken();
        Pin = NewPin();
        UseHttps = httpsCert is not null;
        _sessions.Clear();
        _approvals.Clear();
        _failures.Clear();
        Interlocked.Exchange(ref _totalFailures, 0);
        _indexHtml = LoadIndex();
        try
        {
            _app = await BuildAndStartAsync(port, httpsCert);
            Port = port;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or SocketException)
        {
            int free = FreePort(); // default port busy
            _app = await BuildAndStartAsync(free, httpsCert);
            Port = free;
        }

        DiagLog.Info($"Phone access started: port {Port}, https={UseHttps}");
    }

    async Task<WebApplication> BuildAndStartAsync(int port, X509Certificate2? cert)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = null;
            o.Limits.MinRequestBodyDataRate = null;
            o.Limits.MinResponseDataRate = null;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            o.ListenAnyIP(
                port,
                lo =>
                {
                    if (cert is not null)
                        lo.UseHttps(cert);
                }
            );
        });

        var app = builder.Build();
        Map(app);
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        DiagLog.Info("Phone access stopped");

        _sessions.Clear();
        _approvals.Clear();

        List<WebUpload> all;
        lock (_slotsGate)
        {
            all = _slots
                .Values.Select(s => s.Upload)
                .Concat(_downloads.Values.Select(d => d.Upload))
                .ToList();
            _slots.Clear();
            _downloads.Clear();
        }
        foreach (var up in all)
        {
            up.Cts.Cancel();
            up.State.Error = "Phone access was stopped.";
            up.State.Phase = Phase.Cancelled; // partial uploads are kept; re-sending the same file resumes it
        }

        if (app is not null)
        {
            try
            {
                await app.StopAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    async Task<WebApplication> BuildAndStartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = null; // multi-GB uploads
            o.Limits.MinRequestBodyDataRate = null; // slow phones must not be dropped
            o.Limits.MinResponseDataRate = null;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            o.ListenAnyIP(port);
        });

        var app = builder.Build();
        Map(app);
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    // ------------------------------------------------------------ routes

    void Map(WebApplication app)
    {
        app.Use(
            async (ctx, next) =>
            {
                var ip = ctx.Connection.RemoteIpAddress;
                if (ip is null || !IsLocalNetwork(ip))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    bool ok;
                    if (ctx.Request.Path.Equals("/api/login"))
                        ok = TokenOk(ctx.Request.Headers["X-Token"].FirstOrDefault() ?? ""); // the QR link only opens the login
                    else
                    {
                        var s =
                            ctx.Request.Headers["X-Session"].FirstOrDefault()
                            ?? ctx.Request.Query["s"].FirstOrDefault()
                            ?? "";
                        ok = SessionOk(s, ClientIp(ctx));
                    }
                    if (!ok)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }
                await next();
            }
        );

        app.MapGet("/", () => Results.Content(_indexHtml, "text/html; charset=utf-8"));

        app.MapGet(
            "/api/info",
            (HttpContext ctx) =>
            {
                var cid = ctx.Request.Query["cid"].ToString();
                return Results.Json(
                    new
                    {
                        device = deviceName,
                        free = FreeBytes(downloadDir()),
                        files = Shared.Select(f => new
                        {
                            id = f.Id,
                            name = f.Name,
                            size = f.Size,
                            dl = DownloadInfo(cid, f.Id),
                        }),
                    }
                );
            }
        );

        app.MapPost(
            "/api/login",
            async (HttpContext ctx) =>
            {
                var ip = ClientIp(ctx);
                if (IsLocked(ip, out var wait))
                    return Results.Json(
                        new { error = $"Too many attempts. Try again in {wait} s." },
                        statusCode: StatusCodes.Status429TooManyRequests
                    );
                if (ctx.Request.ContentLength is null or > 1024)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                string pin;
                try
                {
                    using var doc = await JsonDocument.ParseAsync(
                        ctx.Request.Body,
                        cancellationToken: ctx.RequestAborted
                    );
                    pin = doc.RootElement.GetProperty("pin").GetString() ?? "";
                }
                catch
                {
                    return Results.BadRequest(new { error = "bad request" });
                }

                if (!PinOk(pin))
                {
                    DiagLog.Warn($"Phone login: wrong PIN from {ip}");
                    RegisterFailure(ip);
                    return Results.Json(
                        new { error = "Wrong PIN" },
                        statusCode: StatusCodes.Status401Unauthorized
                    );
                }
                _failures.TryRemove(ip, out _);
                DiagLog.Info($"Phone login OK from {ip}");
                var session = NewToken();
                _sessions[session] = new SessionInfo(ip, DateTime.UtcNow);
                return Results.Json(new { session });
            }
        );

        app.MapPost(
            "/api/upload/request",
            async (HttpContext ctx) =>
            {
                var ip = ClientIp(ctx);
                if (ctx.Request.ContentLength is null or > 262_144)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                var files = new List<UploadFile>();
                try
                {
                    using var doc = await JsonDocument.ParseAsync(
                        ctx.Request.Body,
                        cancellationToken: ctx.RequestAborted
                    );
                    foreach (
                        var f in doc.RootElement.GetProperty("files").EnumerateArray().Take(500)
                    )
                    {
                        var name = SafeName(f.GetProperty("name").GetString());
                        long size = f.GetProperty("size").GetInt64();
                        if (name is null || size < 0)
                            return Results.BadRequest(new { error = "bad file entry" });
                        files.Add(new UploadFile(name, size));
                    }
                }
                catch
                {
                    return Results.BadRequest(new { error = "bad request" });
                }
                if (files.Count == 0)
                    return Results.BadRequest(new { error = "no files" });

                var ap = new Approval(
                    ip,
                    new UploadRequest(
                        ip,
                        DeviceHint(ctx.Request.Headers.UserAgent.ToString()),
                        files
                    )
                );
                var id = NewToken();
                _approvals[id] = ap;
                PruneApprovals();
                _ = DecideAsync(ap);
                return Results.Json(new { id, state = StateName(ap.State) });
            }
        );

        app.MapGet(
            "/api/upload/request/{id}",
            (HttpContext ctx, string id) =>
                _approvals.TryGetValue(id, out var ap) && ap.Ip == ClientIp(ctx)
                    ? Results.Json(new { state = StateName(ap.State) })
                    : Results.NotFound()
        );

        app.MapMethods(
            "/api/files/{id}",
            new[] { "GET", "HEAD" },
            (HttpContext ctx, string id) => DownloadAsync(ctx, id)
        );

        app.MapGet(
            "/api/upload/status",
            (HttpContext ctx) =>
            {
                var q = ParseQuery(ctx.Request.Query);
                if (q is null)
                    return Results.BadRequest(new { error = "bad parameters" });
                var part = PartPath(downloadDir(), q.Value);
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                return Results.Json(new { received = have });
            }
        );

        app.MapPut("/api/upload", (HttpContext ctx) => UploadAsync(ctx));

        app.MapDelete(
            "/api/upload",
            (HttpContext ctx) =>
            {
                var q = ParseQuery(ctx.Request.Query);
                if (q is null)
                    return Results.BadRequest();
                UploadSlot? slot;
                lock (_slotsGate)
                    _slots.TryGetValue(q.Value.Key, out slot);
                if (slot is not null)
                {
                    slot.Upload.Cts.Cancel();
                    if (slot.Busy.CurrentCount == 1)
                        Discard(q.Value.Key, slot, Phase.Cancelled);
                }
                return Results.Ok();
            }
        );
    }

    // ------------------------------------------------------------ download (PC -> phone)

    object? DownloadInfo(string cid, string fileId)
    {
        if (cid.Length is 0 or > 64)
            return null;
        DownloadSlot? slot;
        lock (_slotsGate)
            _downloads.TryGetValue($"{cid}|{fileId}", out slot);
        if (slot is null)
            return null;
        var s = slot.Upload.State;
        return new
        {
            done = s.DoneBytes,
            total = s.TotalBytes,
            phase = s.Phase.ToString().ToLowerInvariant(),
        };
    }

    async Task DownloadAsync(HttpContext ctx, string id)
    {
        if (!_shared.TryGetValue(id, out var f) || !File.Exists(f.Path))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        long size;
        try
        {
            size = new FileInfo(f.Path).Length;
        }
        catch (IOException)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        long start = 0,
            end = size - 1;
        var rangeHeader = ctx.Request.Headers["Range"].ToString();
        bool partial = rangeHeader.Length > 0;
        if (partial && (size == 0 || !TryParseRange(rangeHeader, size, out start, out end)))
        {
            ctx.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            ctx.Response.Headers["Content-Range"] = $"bytes */{size}";
            return;
        }
        long length = size == 0 ? 0 : end - start + 1;

        ctx.Response.StatusCode = partial
            ? StatusCodes.Status206PartialContent
            : StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = length;
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.Headers["Content-Disposition"] = AttachmentHeader(f.Name);
        if (partial)
            ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{size}";
        if (HttpMethods.IsHead(ctx.Request.Method) || length == 0)
            return;

        // tiny range probes (browsers do these) are not shown as transfers
        DownloadSlot? slot = null;
        if (length >= (1 << 20) || length == size)
        {
            var peer = ctx.Connection.RemoteIpAddress is { } ip ? ip.MapToIPv4OrSelf() : "?";
            var cid = ctx.Request.Query["cid"].ToString();
            slot = GetDownloadSlot(cid.Length is 0 or > 64 ? peer : cid, f, size, peer);
            if (Interlocked.Increment(ref slot.Active) == 1)
                slot.Upload.State.SetDone(start);
            slot.Upload.State.Error = null;
            slot.Upload.State.Phase = Phase.Transferring;
        }
        var state = slot?.Upload.State;

        using var lcts = CancellationTokenSource.CreateLinkedTokenSource(
            ctx.RequestAborted,
            slot?.Upload.Cts.Token ?? CancellationToken.None
        );
        var ct = lcts.Token;

        try
        {
            long pos = start,
                left = length;
            while (left > 0)
            {
                int n = (int)Math.Min(left, SliceBytes);
                await ctx.Response.SendFileAsync(f.Path, pos, n, ct);
                pos += n;
                left -= n;
                if (state is not null)
                {
                    state.AddBytes(n);
                    state.CurrentFile = f.Name;
                }
            }
            if (state is not null && pos >= size)
            {
                state.Phase = Phase.Completed;
                DiagLog.Info(
                    $"Phone download finished: {Format.Bytes(size)} to {ctx.Connection.RemoteIpAddress}"
                );
            }
        }
        catch (Exception e)
            when (e
                    is OperationCanceledException
                        or IOException
                        or BadHttpRequestException
                        or ConnectionResetException
            )
        {
            // phone stopped / connection lost / cancelled from the PC: the finally block marks it paused
        }
        finally
        {
            if (
                slot is not null
                && Interlocked.Decrement(ref slot.Active) == 0
                && slot.Upload.State.Phase == Phase.Transferring
            )
                slot.Upload.State.Phase = Phase.Paused;
        }
    }

    DownloadSlot GetDownloadSlot(string client, SharedFile f, long size, string peer)
    {
        var key = $"{client}|{f.Id}";
        WebUpload? started = null;
        DownloadSlot slot;
        lock (_slotsGate)
        {
            if (!_downloads.TryGetValue(key, out slot!) || IsTerminal(slot.Upload.State.Phase))
            {
                var state = new TransferState(size, 1) { Phase = Phase.Paused };
                var up = new WebUpload
                {
                    FileName = f.Name,
                    Peer = peer,
                    State = state,
                    IsDownload = true,
                };
                slot = new DownloadSlot(up);
                var captured = slot;
                up.OnCancel = () =>
                {
                    captured.Upload.State.Phase = Phase.Cancelled;
                    lock (_slotsGate)
                    {
                        if (
                            _downloads.TryGetValue(key, out var cur)
                            && ReferenceEquals(cur, captured)
                        )
                            _downloads.Remove(key);
                    }
                };
                _downloads[key] = slot;
                started = up;
            }
        }
        if (started is not null)
            UploadStarted?.Invoke(started);
        return slot;
    }

    static bool IsTerminal(Phase p) =>
        p is Phase.Completed or Phase.Failed or Phase.Cancelled or Phase.Declined;

    static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0;
        end = size - 1;
        if (
            !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',')
        )
            return false;
        var spec = header[6..].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0)
            return false;
        var a = spec[..dash];
        var b = spec[(dash + 1)..];
        if (a.Length == 0)
        {
            if (!long.TryParse(b, out var n) || n <= 0)
                return false;
            start = Math.Max(0, size - n);
        }
        else
        {
            if (!long.TryParse(a, out start))
                return false;
            if (b.Length > 0)
            {
                if (!long.TryParse(b, out end))
                    return false;
                end = Math.Min(end, size - 1);
            }
        }
        return start >= 0 && start <= end && start < size;
    }

    static string AttachmentHeader(string name)
    {
        var ascii = new string(
            name.Select(c => c is >= ' ' and <= '~' && c != '"' && c != '\\' && c != ';' ? c : '_')
                .ToArray()
        );
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    // ------------------------------------------------------------ upload (phone -> PC)

    async Task<IResult> UploadAsync(HttpContext ctx)
    {
        var parsed = ParseQuery(ctx.Request.Query);
        if (parsed is null)
            return Results.BadRequest(new { error = "bad parameters" });
        var q = parsed.Value;

        var rid = ctx.Request.Query["rid"].ToString();
        if (
            !_approvals.TryGetValue(rid, out var approval)
            || approval.Ip != ClientIp(ctx)
            || approval.State != 1
            || !approval.Allows(q.Name, q.Size)
        )
            return Results.Json(
                new { error = "This upload was not approved on the PC." },
                statusCode: StatusCodes.Status403Forbidden
            );

        var dir = downloadDir();
        Directory.CreateDirectory(dir);
        var peer = ctx.Connection.RemoteIpAddress is { } ip ? ip.MapToIPv4OrSelf() : "?";
        var slot = GetSlot(q, dir, peer);

        if (!await _uploadGate.WaitAsync(0))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try
        {
            if (!await slot.Busy.WaitAsync(0))
                return Results.Conflict(new { error = "busy" });
            try
            {
                return await ReceiveAsync(ctx, slot, dir, q);
            }
            finally
            {
                slot.Busy.Release();
            }
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    async Task<IResult> ReceiveAsync(HttpContext ctx, UploadSlot slot, string dir, UploadQuery q)
    {
        var state = slot.Upload.State;
        long have = File.Exists(slot.Part) ? new FileInfo(slot.Part).Length : 0;
        if (q.Offset != have)
            return Results.Json(new { received = have }, statusCode: StatusCodes.Status409Conflict);

        if (have == 0 && FreeBytes(dir) < q.Size + (64L << 20))
        {
            Fail(q.Key, slot, "Not enough free disk space on the PC.");
            return Results.Json(
                new { error = state.Error },
                statusCode: StatusCodes.Status507InsufficientStorage
            );
        }

        state.Error = null;
        state.Phase = Phase.Transferring;
        using var lcts = CancellationTokenSource.CreateLinkedTokenSource(
            ctx.RequestAborted,
            slot.Upload.Cts.Token
        );
        var ct = lcts.Token;
        long received = q.Offset;

        try
        {
            await using (
                var fs = new FileStream(
                    slot.Part,
                    q.Offset == 0 ? FileMode.Create : FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    1 << 20,
                    FileOptions.Asynchronous
                )
            )
            {
                fs.Seek(q.Offset, SeekOrigin.Begin);
                var reader = ctx.Request.BodyReader;
                while (true)
                {
                    var r = await reader.ReadAsync(ct);
                    var buf = r.Buffer;
                    foreach (var seg in buf)
                    {
                        if (received + seg.Length > q.Size)
                            throw new InvalidDataException(
                                "Body is larger than the declared size."
                            );
                        await fs.WriteAsync(seg, ct);
                        received += seg.Length;
                        state.AddBytes(seg.Length);
                        state.CurrentFile = q.Name;
                        DiagLog.Info(
                            $"Phone upload finished: {Format.Bytes(q.Size)} from {slot.Upload.Peer}"
                        );
                    }
                    reader.AdvanceTo(buf.End);
                    if (r.IsCompleted)
                        break;
                }
                await fs.FlushAsync(ct);
                fs.Flush(true); // fsync before we call it done
            }

            if (received < q.Size)
            {
                state.Phase = Phase.Paused;
                return Results.Json(new { received }, statusCode: StatusCodes.Status202Accepted);
            }

            var finalPath = UniquePath(Path.Combine(dir, q.Name));
            File.Move(slot.Part, finalPath);
            state.CurrentFile = Path.GetFileName(finalPath);
            state.Phase = Phase.Completed;
            DiagLog.Info($"Phone upload finished: {Format.Bytes(q.Size)} from {slot.Upload.Peer}");
            lock (_slotsGate)
                _slots.Remove(q.Key);
            return Results.Json(new { done = true, name = Path.GetFileName(finalPath) });
        }
        catch (OperationCanceledException)
        {
            if (slot.Upload.Cts.IsCancellationRequested)
            {
                Discard(q.Key, slot, Phase.Cancelled);
                return Results.StatusCode(499);
            }
            state.Phase = Phase.Paused; // phone went away: keep the partial file for resume
            return Results.Empty;
        }
        catch (Exception e) when (e is BadHttpRequestException or ConnectionResetException)
        {
            state.Phase = Phase.Paused;
            return Results.Empty;
        }
        catch (InvalidDataException e)
        {
            Fail(q.Key, slot, e.Message);
            return Results.BadRequest(new { error = e.Message });
        }
        catch (IOException e)
        {
            Fail(q.Key, slot, e.Message); // typically disk full / permission
            return Results.Json(
                new { error = e.Message },
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    UploadSlot GetSlot(UploadQuery q, string dir, string peer)
    {
        WebUpload? started = null;
        UploadSlot slot;
        lock (_slotsGate)
        {
            if (!_slots.TryGetValue(q.Key, out slot!))
            {
                var part = PartPath(dir, q);
                long have = File.Exists(part) ? Math.Min(new FileInfo(part).Length, q.Size) : 0;
                var state = new TransferState(q.Size, 1) { Phase = Phase.Paused };
                if (have > 0)
                    state.AddBytes(have);

                var up = new WebUpload
                {
                    FileName = q.Name,
                    Peer = peer,
                    State = state,
                };
                slot = new UploadSlot(up, part);
                var captured = slot;
                var key = q.Key;
                up.OnCancel = () =>
                {
                    if (captured.Busy.CurrentCount == 1)
                        Discard(key, captured, Phase.Cancelled);
                };
                _slots[q.Key] = slot;
                started = up;
            }
        }
        if (started is not null)
            UploadStarted?.Invoke(started);
        return slot;
    }

    void Discard(string key, UploadSlot slot, Phase phase)
    {
        slot.Upload.State.Phase = phase;
        lock (_slotsGate)
            _slots.Remove(key);
        try
        {
            File.Delete(slot.Part);
        }
        catch (IOException) { }
    }

    void Fail(string key, UploadSlot slot, string message)
    {
        DiagLog.Warn($"Phone upload failed: {message}");
        slot.Upload.State.Error = message;
        slot.Upload.State.Phase = Phase.Failed;
        lock (_slotsGate)
            _slots.Remove(key); // partial file stays; a new attempt creates a new card and resumes
    }

    // ------------------------------------------------------------ helpers

    static UploadQuery? ParseQuery(IQueryCollection q)
    {
        var cid = q["cid"].ToString();
        if (cid.Length is < 8 or > 64 || !cid.All(char.IsAsciiLetterOrDigit))
            return null;
        var name = SafeName(q["name"].ToString());
        if (name is null)
            return null;
        if (!long.TryParse(q["size"].ToString(), out var size) || size < 0)
            return null;
        long.TryParse(q["offset"].ToString(), out var offset);
        var mt = q["mt"].ToString();
        if (offset < 0 || offset > size || mt.Length > 20)
            return null;
        return new UploadQuery(cid, name, size, offset, $"{cid}|{name}|{size}|{mt}");
    }

    static string PartPath(string dir, UploadQuery q) =>
        Path.Combine(
            dir,
            $"{q.Name}.{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(q.Key)))[..10]}.lfsweb"
        );

    internal static string? SafeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var n = Path.GetFileName(raw.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars())
            n = n.Replace(c, '_');
        n = n.Trim().TrimEnd('.', ' ');
        if (n.Length is 0 or > 200)
            return null;
        var stem = Path.GetFileNameWithoutExtension(n).ToUpperInvariant();
        if (
            stem is "CON" or "PRN" or "AUX" or "NUL"
            || (
                stem.Length == 4
                && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                && char.IsDigit(stem[3])
            )
        )
            n = "_" + n;
        return n;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p))
                return p;
        }
    }

    static bool IsLocalNetwork(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    bool TokenOk(string t)
    {
        var a = Encoding.ASCII.GetBytes(t);
        var b = Encoding.ASCII.GetBytes(Token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    static long FreeBytes(string dir)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    string LoadIndex()
    {
        using var s =
            typeof(WebShareServer).Assembly.GetManifestResourceStream("index.html")
            ?? throw new FileNotFoundException("index.html is not embedded.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("{{DEVICE}}", WebUtility.HtmlEncode(deviceName));
    }

    public void RotatePin()
    {
        Pin = NewPin();
        _sessions.Clear();
        _failures.Clear();
        Interlocked.Exchange(ref _totalFailures, 0);
        PinChanged?.Invoke(Pin);
    }

    static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    bool PinOk(string pin)
    {
        var a = Encoding.ASCII.GetBytes(pin);
        var b = Encoding.ASCII.GetBytes(Pin);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    bool SessionOk(string s, string ip) =>
        s.Length > 0 && _sessions.TryGetValue(s, out var info) && info.Ip == ip;

    bool IsLocked(string ip, out int secondsLeft)
    {
        secondsLeft = 0;
        if (_failures.TryGetValue(ip, out var f) && f.LockedUntil > DateTime.UtcNow)
        {
            secondsLeft = (int)Math.Ceiling((f.LockedUntil - DateTime.UtcNow).TotalSeconds);
            return true;
        }
        return false;
    }

    void RegisterFailure(string ip)
    {
        // 5 wrong PINs from one address = 5 minute lock; 20 in total = new PIN (somebody is guessing)
        _failures.AddOrUpdate(
            ip,
            _ => (1, DateTime.MinValue),
            (_, f) =>
                f.Fails + 1 >= 5 ? (0, DateTime.UtcNow.AddMinutes(5)) : (f.Fails + 1, f.LockedUntil)
        );
        if (Interlocked.Increment(ref _totalFailures) >= 20)
            RotatePin();
    }

    async Task DecideAsync(Approval ap)
    {
        bool ok;
        var ask = ApproveUploads;
        if (ask is null)
            ok = true;
        else
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                ok = await ask(ap.Request, cts.Token);
            }
            catch
            {
                ok = false;
            }
        }
        ap.State = ok ? 1 : 2;
        DiagLog.Info(
            $"Phone upload request from {ap.Ip}: {(ok ? "approved" : "declined")} ({ap.Request.Files.Count} files)"
        );
    }

    void PruneApprovals()
    {
        foreach (var (id, ap) in _approvals)
            if (DateTime.UtcNow - ap.Created > TimeSpan.FromHours(12))
                _approvals.TryRemove(id, out _);
    }

    static string StateName(int s) =>
        s switch
        {
            1 => "approved",
            2 => "declined",
            _ => "pending",
        };

    static string ClientIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip ? ip.MapToIPv4OrSelf() : "?";

    static string DeviceHint(string ua) =>
        ua.Contains("iPhone") ? "iPhone"
        : ua.Contains("iPad") ? "iPad"
        : ua.Contains("Android") ? "Android phone"
        : "Browser";
}

static class IpExtensions
{
    public static string MapToIPv4OrSelf(this IPAddress ip) =>
        (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}
