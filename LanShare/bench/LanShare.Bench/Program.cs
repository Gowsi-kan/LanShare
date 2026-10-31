using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Text;
using System.Text.Json;
using LanShare.Core;

// LanShare loopback benchmark.
//   dotnet run -c Release --project bench/LanShare.Bench -- [--size-mb 1024] [--small-count 20000]
//        [--dir <workdir>] [--out results.json] [--baseline prev.json] [--tolerance 0.15] [--keep]
// Exit codes: 0 ok, 1 verification failure, 2 regression vs baseline.

int sizeMb = 1024,
    smallCount = 20000;
string? outPath = null,
    baselinePath = null,
    dirArg = null;
double tolerance = 0.15;
bool keep = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--size-mb":
            sizeMb = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--small-count":
            smallCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--out":
            outPath = args[++i];
            break;
        case "--baseline":
            baselinePath = args[++i];
            break;
        case "--dir":
            dirArg = args[++i];
            break;
        case "--tolerance":
            tolerance = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--keep":
            keep = true;
            break;
        case "--quick":
            sizeMb = 256;
            smallCount = 3000;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}");
            return 64;
    }
}

var root =
    dirArg
    ?? Path.Combine(Path.GetTempPath(), "lanshare-bench-" + Guid.NewGuid().ToString("N")[..8]);
var srcDir = Path.Combine(root, "src");
var dstDir = Path.Combine(root, "dst");
Directory.CreateDirectory(srcDir);

Console.WriteLine($"Work dir: {root}");
Console.WriteLine("Generating data...");
var large = new List<(string Full, string Rel)>
{
    Make("large-random.bin", p => WriteRandom(p, sizeMb * 1024L * 1024)),
};
var text = new List<(string Full, string Rel)>
{
    Make("text.log", p => WriteText(p, sizeMb / 2 * 1024L * 1024)),
};
var small = MakeSmall(smallCount);

await using var svc = new TransferService("bench", SecurityContext.CreateEphemeral());
var senderSecurity = SecurityContext.CreateEphemeral();

var scenarios = new List<(string Name, List<(string Full, string Rel)> Files, SendOptions Opts)>
{
    ("large-random | 1 stream", large, new(1, false, null, senderSecurity)),
    ("large-random | 4 streams", large, new(4, false, null, senderSecurity)),
    ("large-random | auto-tune", large, new(0, false, null, senderSecurity)),
    ("text | compress off", text, new(4, false, null, senderSecurity)),
    ("text | compress on", text, new(4, true, null, senderSecurity)),
    ($"small x{smallCount} | compress off", small, new(4, false, null, senderSecurity)),
    ($"small x{smallCount} | compress on", small, new(4, true, null, senderSecurity)),
};

string currentDst = dstDir;
svc.IncomingOffer += job => job.Accept(currentDst);
svc.Start();

var results = new List<Result>();
Console.WriteLine();
Console.WriteLine($"{"scenario", -34} {"time", 8} {"MB/s", 9} {"files/s", 9} {"saved", 6}  verify");

foreach (var sc in scenarios)
{
    if (Directory.Exists(dstDir))
        Directory.Delete(dstDir, true);
    Directory.CreateDirectory(dstDir);

    var session = new SenderSession(
        new IPEndPoint(IPAddress.Loopback, svc.Port),
        "bench",
        sc.Files,
        sc.Opts
    );
    var sw = Stopwatch.StartNew();
    await session.RunAsync(CancellationToken.None);
    sw.Stop();

    bool ok = session.State.Phase == Phase.Completed && Verify(sc.Files, dstDir);
    double secs = sw.Elapsed.TotalSeconds;
    double mb = session.State.TotalBytes / 1_000_000.0;
    var raw = session.State.SentRawBytes;
    double saved = raw > 0 ? 100.0 * (1.0 - (double)session.State.SentWireBytes / raw) : 0;
    var r = new Result(
        sc.Name,
        Math.Round(secs, 3),
        Math.Round(mb / secs, 1),
        Math.Round(sc.Files.Count / secs, 1),
        Math.Round(saved, 1),
        ok
    );
    results.Add(r);
    Console.WriteLine(
        $"{r.Name, -34} {r.Seconds, 7:0.00}s {r.MBps, 9:0.0} {r.FilesPerSec, 9:0.0} {r.SavedPct, 5:0}%  {(ok ? "OK" : "FAILED: " + session.State.Error)}"
    );
}

if (outPath is not null)
    File.WriteAllText(
        outPath,
        JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
    );

int exit = results.All(r => r.Verified) ? 0 : 1;

if (baselinePath is not null && File.Exists(baselinePath))
{
    var baseline = JsonSerializer.Deserialize<List<Result>>(File.ReadAllText(baselinePath)) ?? [];
    Console.WriteLine();
    foreach (var r in results)
    {
        var b = baseline.FirstOrDefault(x => x.Name == r.Name);
        if (b is null || b.MBps <= 0)
            continue;
        double delta = r.MBps / b.MBps - 1;
        bool regress = delta < -tolerance;
        Console.WriteLine(
            $"{r.Name, -34} {delta, 7:+0.0%;-0.0%}  {(regress ? "REGRESSION" : "ok")}"
        );
        if (regress && exit == 0)
            exit = 2;
    }
}

if (!keep)
{
    try
    {
        Directory.Delete(root, true);
    }
    catch { }
}
return exit;

// ---------------------------------------------------------------- helpers

(string Full, string Rel) Make(string name, Action<string> write)
{
    var path = Path.Combine(srcDir, name);
    write(path);
    return (path, name);
}

List<(string Full, string Rel)> MakeSmall(int count)
{
    var list = new List<(string, string)>(count);
    var dir = Path.Combine(srcDir, "small");
    var rnd = new Random(42);
    for (int i = 0; i < count; i++)
    {
        var sub = Path.Combine(dir, $"d{i % 50:00}");
        Directory.CreateDirectory(sub);
        var rel = $"small/d{i % 50:00}/f{i:000000}.txt";
        var full = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(full, TextBytes(rnd, rnd.Next(1024, 32 * 1024)));
        list.Add((full, rel));
    }
    return list;
}

static void WriteRandom(string path, long bytes)
{
    var rnd = new Random(1);
    var buf = new byte[8 * 1024 * 1024];
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    for (long left = bytes; left > 0; )
    {
        rnd.NextBytes(buf);
        int n = (int)Math.Min(buf.Length, left);
        fs.Write(buf, 0, n);
        left -= n;
    }
}

static void WriteText(string path, long bytes)
{
    var rnd = new Random(2);
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    for (long left = bytes; left > 0; )
    {
        var chunk = TextBytes(rnd, (int)Math.Min(1 << 20, left));
        fs.Write(chunk, 0, chunk.Length);
        left -= chunk.Length;
    }
}

static byte[] TextBytes(Random rnd, int size)
{
    var sb = new StringBuilder(size + 128);
    string[] levels = ["INFO", "INFO", "INFO", "WARN", "DEBUG", "ERROR"];
    while (sb.Length < size)
        sb.Append("2026-10-28 12:")
            .Append(rnd.Next(60).ToString("00"))
            .Append(':')
            .Append(rnd.Next(60).ToString("00"))
            .Append(' ')
            .Append(levels[rnd.Next(levels.Length)])
            .Append(" worker-")
            .Append(rnd.Next(16))
            .Append(" handled request id=")
            .Append(rnd.Next(1_000_000))
            .Append(" status=")
            .Append(rnd.Next(3) == 0 ? 500 : 200)
            .Append(" latency=")
            .Append(rnd.Next(5, 400))
            .Append("ms\n");
    return Encoding.ASCII.GetBytes(sb.ToString(0, size));
}

bool Verify(IEnumerable<(string Full, string Rel)> files, string dst)
{
    foreach (var (full, rel) in files)
    {
        var d = Path.Combine(dst, rel.Replace('/', Path.DirectorySeparatorChar));
        if (
            !File.Exists(d)
            || new FileInfo(d).Length != new FileInfo(full).Length
            || HashFile(d) != HashFile(full)
        )
            return false;
    }
    return true;
}

static ulong HashFile(string path)
{
    var h = new XxHash3();
    using var fs = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1 << 20,
        FileOptions.SequentialScan
    );
    var buf = new byte[1 << 20];
    int n;
    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        h.Append(buf.AsSpan(0, n));
    return h.GetCurrentHashAsUInt64();
}

record Result(
    string Name,
    double Seconds,
    double MBps,
    double FilesPerSec,
    double SavedPct,
    bool Verified
);
