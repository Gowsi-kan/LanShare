using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LanShare.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanShare.Web;

public sealed record SharedFile(string Id, string Path, string Name, long Size);

/// <summary>One browser upload (survives reconnects/resumes). State is shown in the desktop UI.</summary>
public sealed class WebUpload
{
    public required string FileName { get; init; }
    public required string Peer { get; init; }
    public required TransferState State { get; init; }
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

    sealed class UploadSlot(WebUpload upload, string part)
    {
        public WebUpload Upload { get; } = upload;
        public string Part { get; } = part;
        public SemaphoreSlim Busy { get; } = new(1, 1);
    }

    readonly record struct UploadQuery(string Cid, string Name, long Size, long Offset, string Key);

    readonly ConcurrentDictionary<string, SharedFile> _shared = new();
    readonly Dictionary<string, UploadSlot> _slots = new();
    readonly object _slotsGate = new();
    readonly SemaphoreSlim _uploadGate = new(MaxConcurrentUploads);
    WebApplication? _app;
    string _indexHtml = "";

    public int Port { get; private set; }
    public string Token { get; private set; } = NewToken();
    public bool IsRunning => _app is not null;
    public event Action<WebUpload>? UploadStarted;

    public IReadOnlyList<SharedFile> Shared =>
        _shared.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

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

    public async Task StartAsync(int port = DefaultPort)
    {
        if (_app is not null)
            return;
        Token = NewToken();
        _indexHtml = LoadIndex();
        try
        {
            _app = await BuildAndStartAsync(port);
            Port = port;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or SocketException)
        {
            int free = FreePort(); // default port busy
            _app = await BuildAndStartAsync(free);
            Port = free;
        }
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;

        List<UploadSlot> slots;
        lock (_slotsGate)
        {
            slots = _slots.Values.ToList();
            _slots.Clear();
        }
        foreach (var s in slots)
        {
            s.Upload.Cts.Cancel();
            s.Upload.State.Error = "Phone access was stopped.";
            s.Upload.State.Phase = Phase.Cancelled; // partial file is kept; re-sending the same file resumes it
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
                    var t =
                        ctx.Request.Headers["X-Token"].FirstOrDefault()
                        ?? ctx.Request.Query["t"].FirstOrDefault()
                        ?? "";
                    if (!TokenOk(t))
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
            () =>
                Results.Json(
                    new
                    {
                        device = deviceName,
                        free = FreeBytes(downloadDir()),
                        files = Shared.Select(f => new
                        {
                            id = f.Id,
                            name = f.Name,
                            size = f.Size,
                        }),
                    }
                )
        );

        app.MapGet(
            "/api/files/{id}",
            (string id) =>
            {
                if (!_shared.TryGetValue(id, out var f) || !File.Exists(f.Path))
                    return Results.NotFound();
                // physical-file result: sendfile-style streaming + HTTP Range (pause/resume in the phone's download manager)
                return Results.File(
                    f.Path,
                    "application/octet-stream",
                    f.Name,
                    enableRangeProcessing: true
                );
            }
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

    // ------------------------------------------------------------ upload (phone -> PC)

    async Task<IResult> UploadAsync(HttpContext ctx)
    {
        var parsed = ParseQuery(ctx.Request.Query);
        if (parsed is null)
            return Results.BadRequest(new { error = "bad parameters" });
        var q = parsed.Value;

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

    static string? SafeName(string? raw)
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
}

static class IpExtensions
{
    public static string MapToIPv4OrSelf(this IPAddress ip) =>
        (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}
