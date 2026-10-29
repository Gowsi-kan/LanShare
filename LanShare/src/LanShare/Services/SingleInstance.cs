using System.IO.Pipes;
using System.Text;

namespace LanShare.Services;

/// <summary>
/// One LanShare per user. A second start forwards its file arguments (or just "show yourself") to the
/// running instance through a user-only named pipe and exits.
/// </summary>
public static class SingleInstance
{
    static readonly string User = Environment.UserName;
    static string PipeName => $"lanshare-{User}";
    static FileStream? _lock;
    static readonly CancellationTokenSource Cts = new();

    /// <summary>Raised with the forwarded paths. An empty array means "just bring the window to the front".</summary>
    public static event Action<string[]>? Received;

    public static bool TryBecomePrimary(string[] args)
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"lanshare-{User}.lock");
        try
        {
            _lock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
        }
        catch (IOException)
        {
            Forward(args);
            return false;
        }
        _ = Task.Run(ServeAsync);
        return true;
    }

    public static string[] PathsFrom(string[] args) =>
        args.Where(a => !a.StartsWith("--"))
            .Select(a =>
            {
                try
                {
                    return Path.GetFullPath(a);
                }
                catch
                {
                    return "";
                }
            })
            .Where(p => p.Length > 0 && (File.Exists(p) || Directory.Exists(p)))
            .ToArray();

    static void Forward(string[] args)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', PathsFrom(args)));
        for (int i = 0; i < 25; i++) // the primary may still be starting: retry for ~5 s
        {
            try
            {
                using var c = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.CurrentUserOnly
                );
                c.Connect(300);
                c.Write(bytes, 0, bytes.Length);
                c.Flush();
                return;
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                Thread.Sleep(200);
            }
        }
    }

    static async Task ServeAsync()
    {
        var ct = Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );
                await server.WaitForConnectionAsync(ct);
                using var ms = new MemoryStream();
                await server.CopyToAsync(ms, ct);
                var paths = Encoding
                    .UTF8.GetString(ms.ToArray())
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => File.Exists(p) || Directory.Exists(p))
                    .ToArray();
                Received?.Invoke(paths);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
    }
}
