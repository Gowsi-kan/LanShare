using System.Text;

namespace LanShare.Core;

/// <summary>
/// Small thread-safe log: daily file in the LanShare data folder (5 MB cap, 7 days kept) plus an in-memory ring
/// of the latest lines, which the "Copy diagnostics" buttons use. Never log file names or fingerprints.
/// </summary>
public static class DiagLog
{
    const int RingSize = 1000;
    const long MaxFileBytes = 5 * 1024 * 1024;
    const int KeepDays = 7;

    static readonly object Gate = new();
    static readonly Queue<string> Ring = new();
    static string? _dir;
    static string _path = "";
    static string _day = "";
    static StreamWriter? _writer;

    public static string? LogDirectory => _dir;

    public static void Init(string dir)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _dir = dir;
                foreach (var f in Directory.GetFiles(dir, "lanshare-*.log"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays))
                        try
                        {
                            File.Delete(f);
                        }
                        catch (IOException) { }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _dir = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static string Recent(int count)
    {
        lock (Gate)
            return string.Join(Environment.NewLine, Ring.Skip(Math.Max(0, Ring.Count - count)));
    }

    static void Write(string level, string message)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingSize)
                Ring.Dequeue();
            if (_dir is null)
                return;
            try
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (
                    _writer is not null
                    && (today != _day || _writer.BaseStream.Length > MaxFileBytes)
                )
                {
                    bool sameDay = today == _day;
                    _writer.Dispose();
                    _writer = null;
                    if (sameDay)
                        File.Move(_path, Path.ChangeExtension(_path, ".old.log"), overwrite: true);
                }
                if (_writer is null)
                {
                    _day = today;
                    _path = Path.Combine(_dir, $"lanshare-{today}.log");
                    _writer = new StreamWriter(
                        new FileStream(
                            _path,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite
                        ),
                        new UTF8Encoding(false)
                    )
                    {
                        AutoFlush = true,
                    };
                }
                _writer.WriteLine(line);
            }
            catch (IOException) { }
        }
    }
}
