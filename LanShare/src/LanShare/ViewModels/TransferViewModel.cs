using System.Diagnostics;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanShare.Core;
using LanShare.Web;

namespace LanShare.ViewModels;

public sealed partial class TransferViewModel : ObservableObject
{
    static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#4C8DFF"));
    static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34C77B"));
    static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF5C6C"));
    static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5B942"));
    static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#8A93A6"));

    readonly MainViewModel _owner;
    readonly TransferState _state;
    readonly IncomingJob? _job;
    readonly CancellationTokenSource? _cts;
    readonly IPausable? _pausable;
    bool _wasLive;

    readonly bool _web;
    readonly Action? _onCancel;
    readonly bool _webDownload;

    long _startTs,
        _lastTs,
        _startBytes,
        _endTs,
        _lastBytes;
    double _speed,
        _elapsed;
    bool _frozen,
        _final;

    int _peakStreams;

    public SendPlan? Plan { get; private init; }
    public bool IsIncoming => _job is not null || (_web && !_webDownload);
    public string DirectionGlyph => IsIncoming ? "↓" : "↑";
    public string Title { get; private init; } = "";
    public string Subtitle { get; private init; } = "";

    public event Action<TransferViewModel>? Finished;
    public Phase FinalPhase => _state.Phase;
    public string? ErrorText => _state.Error;
    public bool IsDownloadToPhone => _webDownload;
    public double SpeedBytesPerSec => _state.Phase == Phase.Transferring ? _speed : 0;

    [ObservableProperty]
    double _percent;

    [ObservableProperty]
    string _phaseText = "";

    [ObservableProperty]
    IBrush _phaseBrush = Grey;

    [ObservableProperty]
    string _transferred = "";

    [ObservableProperty]
    string _speedText = "—";

    [ObservableProperty]
    string _etaText = "—";

    [ObservableProperty]
    string _linkText = "—";

    [ObservableProperty]
    string _filesText = "";

    [ObservableProperty]
    string _currentFile = "";

    [ObservableProperty]
    bool _isAwaiting;

    [ObservableProperty]
    bool _isActive;

    [ObservableProperty]
    bool _canRetry;

    [ObservableProperty]
    bool _isFinished;

    [ObservableProperty]
    bool _canOpenFolder;

    [ObservableProperty]
    bool _canPause;

    [ObservableProperty]
    bool _canResume;

    [ObservableProperty]
    string _diagText = "";

    TransferViewModel(
        MainViewModel owner,
        TransferState state,
        IncomingJob? job,
        CancellationTokenSource? cts,
        bool web = false,
        Action? onCancel = null,
        bool webDownload = false,
        IPausable? pausable = null
    )
    {
        _owner = owner;
        _state = state;
        _job = job;
        _cts = cts;
        _web = web;
        _onCancel = onCancel;
        _webDownload = webDownload;
        _pausable = pausable;
    }

    public static TransferViewModel Incoming(MainViewModel owner, IncomingJob job)
    {
        var o = job.Offer;
        var vm = new TransferViewModel(owner, job.State, job, null, pausable: job)
        {
            Title =
                $"{o.Files.Count} file{(o.Files.Count == 1 ? "" : "s")} · {Format.Bytes(o.TotalBytes)}",
            Subtitle = $"From {job.Peer}",
        };
        vm.Refresh();
        return vm;
    }

    public static TransferViewModel Outgoing(
        MainViewModel owner,
        SenderSession session,
        SendPlan plan
    )
    {
        var cts = new CancellationTokenSource();
        var n = plan.Files.Count;
        var vm = new TransferViewModel(owner, session.State, null, cts, pausable: session)
        {
            Plan = plan,
            Title = $"{n} file{(n == 1 ? "" : "s")} · {Format.Bytes(session.State.TotalBytes)}",
            Subtitle = $"To {plan.Peer.Name} ({plan.Peer.Address})",
        };
        vm.Refresh();
        _ = Task.Run(() => session.RunAsync(cts.Token));
        return vm;
    }

    public static TransferViewModel Web(MainViewModel owner, WebUpload up)
    {
        var vm = new TransferViewModel(
            owner,
            up.State,
            null,
            null,
            web: true,
            onCancel: up.Cancel,
            webDownload: up.IsDownload
        )
        {
            Title = up.FileName,
            Subtitle = up.IsDownload
                ? $"{Format.Bytes(up.State.TotalBytes)} · to phone / browser ({up.Peer})"
                : $"{Format.Bytes(up.State.TotalBytes)} · from phone / browser ({up.Peer})",
        };
        vm.Refresh();
        return vm;
    }

    public void Refresh()
    {
        if (_final)
            return; // finished transfers need no further UI work

        _peakStreams = Math.Max(_peakStreams, _state.ActiveStreams);
        var s = _state;
        var phase = s.Phase;
        long done = s.DoneBytes,
            total = s.TotalBytes;
        long now = Stopwatch.GetTimestamp();

        bool nowLive = phase == Phase.Transferring;
        if (nowLive && !_wasLive && _startTs != 0) // resumed after a pause: restart the speed measurement
        {
            _lastTs = now;
            _lastBytes = done;
            _speed = 0;
        }
        _wasLive = nowLive;

        if (phase == Phase.Transferring)
        {
            if (_startTs == 0)
            {
                _startTs = _lastTs = now;
                _startBytes = _lastBytes = done;
            }
            else
            {
                double dt = Stopwatch.GetElapsedTime(_lastTs, now).TotalSeconds;
                if (dt >= 0.3)
                {
                    double inst = (done - _lastBytes) / dt;
                    _speed = _speed <= 0 ? inst : 0.3 * inst + 0.7 * _speed;
                    _lastTs = now;
                    _lastBytes = done;
                }
            }
        }
        else if (phase == Phase.Completed && _startTs != 0 && !_frozen)
        {
            _elapsed = Stopwatch.GetElapsedTime(_startTs, now).TotalSeconds;
            _speed = _elapsed > 0 ? (done - _startBytes) / _elapsed : 0;
            _frozen = true;
        }

        bool live = phase == Phase.Transferring;
        if (live && _startTs != 0)
        {
            double wall =
                Stopwatch.GetElapsedTime(_startTs, now).TotalSeconds * Math.Max(1, s.ActiveStreams);
            if (wall > 3)
            {
                double Pct(Stage st) => 100 * s.StageSeconds(st) / wall;
                var parts = new (string Name, double Pct)[]
                {
                    ("source read", Pct(Stage.SourceRead)),
                    ("hash", Pct(Stage.Hash)),
                    ("compress", Pct(Stage.Compress)),
                    ("network+TLS send", Pct(Stage.Send)),
                    ("waiting for acks", Pct(Stage.AckWait)),
                    ("receiving", Pct(Stage.NetReceive)),
                    ("decompress", Pct(Stage.Decompress)),
                    ("disk write", Pct(Stage.Disk)),
                };
                DiagText =
                    "Busy: "
                    + string.Join(
                        " · ",
                        parts
                            .Where(p => p.Pct >= 5)
                            .OrderByDescending(p => p.Pct)
                            .Take(4)
                            .Select(p => $"{p.Name} {p.Pct:0}%")
                    );
            }
        }
        Percent =
            total == 0 ? (phase == Phase.Completed ? 100 : 0) : Math.Min(100, done * 100.0 / total);
        Transferred = $"{Format.Bytes(done)} / {Format.Bytes(total)}";
        SpeedText =
            live && _speed > 0 ? Format.Rate(_speed)
            : _frozen ? $"{Format.Rate(_speed)} avg"
            : "—";
        EtaText =
            live && _speed > 1 ? Format.Duration(TimeSpan.FromSeconds((total - done) / _speed))
            : _frozen ? $"took {Format.Duration(TimeSpan.FromSeconds(_elapsed))}"
            : "—";

        if (s.LinkBps > 0)
        {
            var link = Format.LinkRate(s.LinkBps);
            LinkText =
                live && _speed > 0
                    ? $"{link} · {Math.Min(100, _speed * 8 / s.LinkBps * 100):0}% used"
                    : link;
        }
        else
            LinkText = "unknown";

        double saved = s.SentRawBytes > 0 ? 1.0 - (double)s.SentWireBytes / s.SentRawBytes : 0;
        FilesText =
            $"{s.FilesDone}/{s.TotalFiles} files · {s.ActiveStreams} streams · {s.Retries} retries"
            + (saved >= 0.02 ? $" · {saved:P0} saved by compression" : "");
        CurrentFile = s.CurrentFile;

        (PhaseText, PhaseBrush) = phase switch
        {
            Phase.Connecting => ("Connecting…", Blue),
            Phase.WaitingForPeer => ("Waiting for receiver…", Amber),
            Phase.AwaitingYou => ("Incoming — accept?", Amber),
            Phase.Transferring => (
                total > 0 && done >= total ? "Finalizing — flushing to disk…" : "Transferring",
                Blue
            ),
            Phase.Completed => ("Completed ✓", Green),
            Phase.Failed => ($"Failed: {s.Error}", Red),
            Phase.Cancelled => ("Cancelled", Grey),
            Phase.Declined => ("Declined", Grey),
            Phase.Paused => (
                _pausable is null ? "Paused, waiting for the phone"
                : s.PausedByPeer && !s.PausedLocal ? "Paused by the other device"
                : "Paused",
                Amber
            ),
            Phase.Pairing => ("Pairing — confirm the code", Amber),
            _ => ("", Grey),
        };

        IsAwaiting = phase == Phase.AwaitingYou;
        IsActive =
            phase
                is Phase.Connecting
                    or Phase.WaitingForPeer
                    or Phase.Transferring
                    or Phase.Paused
                    or Phase.Pairing;
        IsFinished = phase is Phase.Completed or Phase.Failed or Phase.Cancelled or Phase.Declined;
        CanRetry =
            !_web && !IsIncoming && phase is Phase.Failed or Phase.Cancelled or Phase.Declined;
        CanOpenFolder = IsFinished && !_webDownload;
        CanPause = _pausable is not null && phase == Phase.Transferring;
        CanResume = _pausable is not null && phase == Phase.Paused;
        if (IsFinished && !_final)
        {
            _endTs = now;
            _final = true;
            Finished?.Invoke(this);
        }
    }

    public void CancelQuietly()
    {
        _cts?.Cancel();
        _job?.Cancel();
        _onCancel?.Invoke();
    }

    [RelayCommand]
    void Accept() => _job?.Accept(_owner.DownloadDir);

    [RelayCommand]
    void Decline() => _job?.Decline();

    [RelayCommand]
    void Pause() => _pausable?.Pause();

    [RelayCommand]
    void Resume() => _pausable?.Resume();

    [RelayCommand]
    void Cancel() => CancelQuietly();

    [RelayCommand]
    Task Retry() => _owner.RetryAsync(this);

    [RelayCommand]
    void Dismiss() => _owner.Dismiss(this);

    [RelayCommand]
    void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_owner.DownloadDir) { UseShellExecute = true });
        }
        catch { }
    }

    public string Report()
    {
        var s = _state;
        long done = s.DoneBytes;
        double wall =
            _startTs == 0
                ? 0
                : Stopwatch
                    .GetElapsedTime(_startTs, _endTs != 0 ? _endTs : Stopwatch.GetTimestamp())
                    .TotalSeconds;
        double avg = wall > 0 ? (done - _startBytes) / wall : 0;
        int streams = Math.Max(1, Math.Max(_peakStreams, s.ActiveStreams));
        double denom = Math.Max(0.001, wall * streams);
        var kind =
            _web ? (_webDownload ? "phone download (PC to phone)" : "phone upload (phone to PC)")
            : IsIncoming ? "receive (this PC is receiving)"
            : "send (this PC is sending)";

        var sb = new StringBuilder();
        sb.AppendLine($"[{kind}] {Subtitle}");
        sb.AppendLine(
            $"  result: {s.Phase}{(string.IsNullOrEmpty(s.Error) ? "" : " — " + s.Error)}"
        );
        sb.AppendLine(
            $"  data: {Format.Bytes(done)} of {Format.Bytes(s.TotalBytes)} · files {s.FilesDone}/{s.TotalFiles}"
        );
        sb.AppendLine(
            $"  time: {wall:0.0} s · average {Format.Rate(avg)} · latest {Format.Rate(SpeedBytesPerSec)}"
        );
        sb.AppendLine(
            $"  link: {(s.LinkName.Length > 0 ? s.LinkName : "unknown")} · using "
                + (s.LinkBps > 0 ? $"{Math.Min(999, avg * 8 / s.LinkBps * 100):0}%" : "n/a")
                + " of the negotiated speed"
        );
        sb.AppendLine($"  streams: peak {_peakStreams} · retries {s.Retries}");
        if (s.SentRawBytes > 0)
            sb.AppendLine(
                $"  compression: {Format.Bytes(s.SentWireBytes)} on the wire for {Format.Bytes(s.SentRawBytes)} of data"
            );

        double wallOnly = Math.Max(0.001, wall);
        var stages = Enum.GetValues<Stage>()
            .Select(st =>
                (
                    Stage: st,
                    Pct: 100
                        * s.StageSeconds(st)
                        / (
                            st is Stage.Prepare or Stage.Finalize or Stage.Persist
                                ? wallOnly
                                : denom
                        )
                )
            )
            .ToList();
        sb.AppendLine(
            "  stage busy % (of stream time): "
                + string.Join(
                    ", ",
                    stages.Where(x => x.Pct >= 0.5).Select(x => $"{x.Stage} {x.Pct:0}")
                )
        );
        var top = stages.Where(x => x.Stage != Stage.AckWait).OrderByDescending(x => x.Pct).First();
        if (top.Pct >= 5)
            sb.AppendLine($"  busiest stage: {top.Stage} -> {Hint(top.Stage)}");
        return sb.ToString().TrimEnd();
    }

    static string Hint(Stage st) =>
        st switch
        {
            Stage.Send or Stage.NetReceive => "network or encryption",
            Stage.AckWait => "waiting for the receiver (its disk or the network)",
            Stage.Disk => "the receiving disk",
            Stage.SourceRead => "the source disk",
            Stage.Prepare => "receiver pre-allocating the file on disk",
            Stage.Finalize => "receiver flushing the finished file to disk (disk write speed)",
            Stage.Persist => "receiver saving resume state to disk",
            _ => "CPU (hash / compression)",
        };

    public async Task<string> BuildDetailsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine(await SystemInfo.DescribeAsync(_owner.DownloadDir));
        if (Plan is { Files.Count: > 0 } plan)
            sb.AppendLine("Source disk: " + await SystemInfo.DescribeDiskAsync(plan.Files[0].Full));
        sb.AppendLine();
        sb.AppendLine(Report());
        sb.AppendLine();
        sb.AppendLine("Recent log:");
        sb.AppendLine(DiagLog.Recent(60));
        return sb.ToString();
    }
}
