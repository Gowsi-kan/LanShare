using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LanShare.Core;

// LanShare loopback benchmark.
//   dotnet run -c Release --project bench/LanShare.Bench -- [--size-mb 1024] [--small-count 20000] [--repeat 3]
//        [--dir <workdir>] [--out results.json] [--baseline prev.json] [--tolerance 0.15] [--strict]
//        [--only <scenario text>] [--quick] [--keep]
// Exit codes: 0 ok, 1 verification failure, 2 regression (only when the baseline was recorded in the same environment, or --strict).

int sizeMb = 1024,
    smallCount = 20000,
    repeat = 1;
string? outPath = null,
    baselinePath = null,
    dirArg = null,
    only = null;
double tolerance = 0.15;
bool keep = false,
    strict = false;

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
        case "--repeat":
            repeat = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture));
            break;
        case "--quick":
            sizeMb = 256;
            smallCount = 3000;
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
        case "--only":
            only = args[++i];
            break;
        case "--tolerance":
            tolerance = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--strict":
            strict = true;
            break;
        case "--keep":
            keep = true;
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

Console.WriteLine(await SystemInfo.DescribeAsync(root));
Console.WriteLine($"Work dir: {root}");
Console.WriteLine($"Repeats per scenario: {repeat} (median reported)");
Console.WriteLine();

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
var warm = new List<(string Full, string Rel)> { Make("warm.bin", p => WriteRandom(p, 4L << 20)) };

Console.WriteLine("Flushing the generated data to disk...");
await SettleDiskAsync([large[0].Full, text[0].Full]);

var scenarios = new List<(string Name, List<(string Full, string Rel)> Files, SendOptions Opts)>();
var senderSecurity = SecurityContext.CreateEphemeral();
scenarios.Add(("large-random | 1 stream", large, new(1, false, null, senderSecurity)));
scenarios.Add(("large-random | 4 streams", large, new(4, false, null, senderSecurity)));
scenarios.Add(("large-random | auto-tune", large, new(0, false, null, senderSecurity)));
scenarios.Add(("text | compress off", text, new(4, false, null, senderSecurity)));
scenarios.Add(("text | compress on", text, new(4, true, null, senderSecurity)));
scenarios.Add(($"small x{smallCount} | compress off", small, new(4, false, null, senderSecurity)));
scenarios.Add(($"small x{smallCount} | compress on", small, new(4, true, null, senderSecurity)));
if (only is not null)
    scenarios = scenarios
        .Where(s => s.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
        .ToList();

await using var svc = new TransferService("bench", SecurityContext.CreateEphemeral());
string currentDst = dstDir;
TransferState? lastReceiver = null;
svc.IncomingOffer += job =>
{
    lastReceiver = job.State;
    job.Accept(currentDst);
};
svc.Start();

// warm-up: JIT, TLS and pairing happen here instead of inside the first measured scenario
await RunOnceAsync(warm, new SendOptions(2, false, null, senderSecurity), verify: false);

var results = new List<Result>();
Console.WriteLine();
Console.WriteLine(
    $"{"scenario", -34} {"time", 8} {"MB/s", 9} {"range", -13} {"files/s", 9} {"saved", 6}  verify"
);

foreach (var sc in scenarios)
{
    var secs = new List<double>();
    bool allOk = true;
    SenderSession? last = null;
    string busy = "";
    string error = "";

    for (int r = 0; r < repeat; r++)
    {
        var run = await RunOnceAsync(sc.Files, sc.Opts, verify: r == 0);
        secs.Add(run.Seconds);
        last = run.Session;
        busy = Busy(run.Session.State, run.Receiver, run.Seconds, run.Peak);
        if (!run.Ok)
        {
            allOk = false;
            error = run.Session.State.Error ?? run.Session.State.Phase.ToString();
            break;
        }
        await SettleDiskAsync();
    }

    double mb = last!.State.TotalBytes / 1_000_000.0;
    double med = Median(secs);
    double raw = last.State.SentRawBytes;
    var res = new Result(
        sc.Name,
        Math.Round(med, 3),
        Math.Round(mb / med, 1),
        Math.Round(sc.Files.Count / med, 1),
        Math.Round(raw > 0 ? 100.0 * (1.0 - last.State.SentWireBytes / raw) : 0, 1),
        allOk,
        Math.Round(mb / secs.Max(), 1),
        Math.Round(mb / secs.Min(), 1),
        busy
    );
    results.Add(res);

    var range = repeat > 1 ? $"{res.MinMBps:0}-{res.MaxMBps:0}" : "";
    Console.WriteLine(
        $"{res.Name, -34} {res.Seconds, 7:0.00}s {res.MBps, 9:0.0} {range, -13} {res.FilesPerSec, 9:0.0} {res.SavedPct, 5:0}%  {(allOk ? "OK" : "FAILED: " + error)}"
    );
    Console.WriteLine($"    busy: {busy}");
}

var envKey = EnvKey(root);
int exit = results.All(r => r.Verified) ? 0 : 1;

if (outPath is not null)
    File.WriteAllText(
        outPath,
        JsonSerializer.Serialize(
            new BenchFile(envKey, DateTime.UtcNow.ToString("u"), results),
            new JsonSerializerOptions { WriteIndented = true }
        )
    );

if (baselinePath is not null && File.Exists(baselinePath))
{
    var (baseEnv, baseline) = LoadBaseline(baselinePath);
    bool sameEnv = baseEnv == envKey;
    Console.WriteLine();
    if (!sameEnv)
        Console.WriteLine(
            $"NOTE: the baseline was recorded under different conditions ({baseEnv ?? "unknown: old file format"}). "
                + "Deltas are informational and do not fail the run. Save a new baseline here (scripts/bench-gate.sh --save), or use --strict."
        );

    foreach (var r in results)
    {
        var b = baseline.FirstOrDefault(x => x.Name == r.Name);
        if (b is null || b.MBps <= 0)
            continue;
        double delta = r.MBps / b.MBps - 1;
        bool slower = delta < -tolerance;
        bool counts = sameEnv || strict;
        var label =
            !slower ? "ok"
            : counts ? "REGRESSION"
            : "slower (different environment)";
        Console.WriteLine($"{r.Name, -34} {delta, 7:+0.0%;-0.0%}  {label}");
        if (slower && counts && exit == 0)
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

async Task<(
    double Seconds,
    bool Ok,
    SenderSession Session,
    TransferState? Receiver,
    int Peak
)> RunOnceAsync(List<(string Full, string Rel)> files, SendOptions opts, bool verify)
{
    if (Directory.Exists(dstDir))
        Directory.Delete(dstDir, true);
    Directory.CreateDirectory(dstDir);

    var session = new SenderSession(
        new IPEndPoint(IPAddress.Loopback, svc.Port),
        "bench",
        files,
        opts
    );
    int peak = 1;
    using var stop = new CancellationTokenSource();
    var poll = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            peak = Math.Max(peak, session.State.ActiveStreams);
            try
            {
                await Task.Delay(50, stop.Token);
            }
            catch (OperationCanceledException) { }
        }
    });

    var sw = Stopwatch.StartNew();
    await session.RunAsync(CancellationToken.None);
    sw.Stop();
    stop.Cancel();
    await poll;

    bool ok = session.State.Phase == Phase.Completed && (!verify || Verify(files, dstDir));
    return (sw.Elapsed.TotalSeconds, ok, session, lastReceiver, peak);
}

static async Task SettleDiskAsync(IEnumerable<string>? bigFiles = null)
{
    try
    {
        if (!OperatingSystem.IsWindows())
        {
            using var p = Process.Start(
                new ProcessStartInfo("sync") { UseShellExecute = false, CreateNoWindow = true }
            );
            if (p is not null)
                await p.WaitForExitAsync();
        }
        else if (bigFiles is not null)
        {
            foreach (var f in bigFiles)
            {
                using var h = File.OpenHandle(
                    f,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite
                );
                RandomAccess.FlushToDisk(h);
            }
        }
    }
    catch { }
    await Task.Delay(500);
}

static string EnvKey(string workDir) =>
    $"{RuntimeInformation.OSDescription} | {RuntimeInformation.OSArchitecture} | {Environment.ProcessorCount} cores | {Path.GetDirectoryName(workDir)}";

static string Busy(TransferState sender, TransferState? receiver, double wallSeconds, int streams)
{
    string Top(TransferState s, params Stage[] stages)
    {
        var parts = stages
            .Select(st =>
                (
                    Stage: st,
                    Pct: 100
                        * s.StageSeconds(st)
                        / Math.Max(
                            0.001,
                            st is Stage.Prepare or Stage.Finalize or Stage.Persist
                                ? wallSeconds
                                : wallSeconds * streams
                        )
                )
            )
            .Where(x => x.Pct >= 8)
            .OrderByDescending(x => x.Pct)
            .Take(3)
            .Select(x => $"{x.Stage} {x.Pct:0}%");
        var text = string.Join(", ", parts);
        return text.Length == 0 ? "-" : text;
    }

    return $"sender[{Top(sender, Stage.SourceRead, Stage.Hash, Stage.Compress, Stage.Send)}]"
        + (
            receiver is null
                ? ""
                : $" receiver[{Top(receiver, Stage.NetReceive, Stage.Hash, Stage.Decompress, Stage.Disk, Stage.Prepare, Stage.Finalize, Stage.Persist)}]"
        );
}

static double Median(List<double> values)
{
    var s = values.OrderBy(x => x).ToList();
    int n = s.Count;
    return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
}

static (string? Env, List<Result> Results) LoadBaseline(string path)
{
    var json = File.ReadAllText(path);
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.ValueKind == JsonValueKind.Array) // older file format: a bare list, no environment info
        return (null, JsonSerializer.Deserialize<List<Result>>(json) ?? []);
    var f = JsonSerializer.Deserialize<BenchFile>(json);
    return (f?.Env, f?.Results ?? []);
}

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
    bool Verified,
    double MinMBps = 0,
    double MaxMBps = 0,
    string Busy = ""
);

record BenchFile(string Env, string Created, List<Result> Results);
