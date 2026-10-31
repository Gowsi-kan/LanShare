using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace LanShare.Services;

/// <summary>
/// One LanShare per user. A second start forwards its file arguments (or just "show yourself") to the running
/// instance. If the second start is a NEWER build, the running instance is asked to quit and the new one takes over.
/// </summary>
public static class SingleInstance
{
    enum Reply
    {
        Ok,
        Replace,
        NoReply,
        Unreachable,
    }

    static readonly string User = Environment.UserName;
    static string PipeName => $"lanshare-{User}";
    static string LockPath => Path.Combine(Path.GetTempPath(), $"lanshare-{User}.lock");
    static FileStream? _lock;
    static readonly CancellationTokenSource Cts = new();

    /// <summary>Raised with forwarded paths. An empty array means "just bring the window to the front".</summary>
    public static event Action<string[]>? Received;

    /// <summary>A newer build was started: this instance should quit.</summary>
    public static event Action? ReplaceRequested;

    public static bool TryBecomePrimary(string[] args)
    {
        for (int attempt = 0; attempt < 60; attempt++) // up to ~12 s: an older build may need a moment to shut down
        {
            if (TryLock())
            {
                _ = Task.Run(ServeAsync);
                return true;
            }

            switch (Forward(args))
            {
                case Reply.Ok:
                case Reply.NoReply: // an older build without this protocol: it just brought its window to the front
                    return false;
            }
            Thread.Sleep(200); // Replace: wait for the old instance to release the lock. Unreachable: still starting / stale.
        }
        return false;
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

    static bool TryLock()
    {
        try
        {
            _lock = new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static Reply Forward(string[] args)
    {
        try
        {
            using var c = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.CurrentUserOnly
            );
            c.Connect(300);

            var payload = Encoding.UTF8.GetBytes(
                BuildInfo.Ticks + "\n" + string.Join('\n', PathsFrom(args))
            );
            var len = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
            c.Write(len, 0, 4);
            c.Write(payload, 0, payload.Length);
            c.Flush();

            var one = new byte[1];
            var read = Task.Run(() =>
            {
                try
                {
                    return c.Read(one, 0, 1);
                }
                catch
                {
                    return 0;
                }
            });
            if (!read.Wait(1500))
                return Reply.NoReply;
            if (read.Result != 1)
                return Reply.Unreachable;
            return one[0] == (byte)'R' ? Reply.Replace : Reply.Ok;
        }
        catch (Exception e)
            when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return Reply.Unreachable;
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
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );
                await server.WaitForConnectionAsync(ct);

                using var rc = CancellationTokenSource.CreateLinkedTokenSource(ct);
                rc.CancelAfter(TimeSpan.FromSeconds(3));

                var lenBuf = new byte[4];
                await server.ReadExactlyAsync(lenBuf, rc.Token);
                int n = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (n is < 0 or > 1_000_000)
                    continue;
                var buf = new byte[n];
                await server.ReadExactlyAsync(buf, rc.Token);

                var lines = Encoding.UTF8.GetString(buf).Split('\n');
                long.TryParse(lines[0], out var theirTicks);
                bool replace = theirTicks > BuildInfo.Ticks; // only a NEWER build takes over

                await server.WriteAsync(new[] { (byte)(replace ? 'R' : 'K') }, rc.Token);
                await server.FlushAsync(rc.Token);
                if (OperatingSystem.IsWindows())
                    server.WaitForPipeDrain(); // let the client read before we disconnect

                if (replace)
                    ReplaceRequested?.Invoke();
                else
                    Received?.Invoke(
                        lines.Skip(1).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray()
                    );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
    }
}
