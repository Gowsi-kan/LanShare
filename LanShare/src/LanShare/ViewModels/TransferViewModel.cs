using System.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanShare.Core;

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

    long _startTs,
        _lastTs,
        _startBytes,
        _lastBytes;
    double _speed,
        _elapsed;
    bool _frozen,
        _final;

    public SendPlan? Plan { get; private init; }
    public bool IsIncoming => _job is not null;
    public string DirectionGlyph => IsIncoming ? "↓" : "↑";
    public string Title { get; private init; } = "";
    public string Subtitle { get; private init; } = "";

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

    TransferViewModel(
        MainViewModel owner,
        TransferState state,
        IncomingJob? job,
        CancellationTokenSource? cts
    )
    {
        _owner = owner;
        _state = state;
        _job = job;
        _cts = cts;
    }

    public static TransferViewModel Incoming(MainViewModel owner, IncomingJob job)
    {
        var o = job.Offer;
        var vm = new TransferViewModel(owner, job.State, job, null)
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
        var vm = new TransferViewModel(owner, session.State, null, cts)
        {
            Plan = plan,
            Title = $"{n} file{(n == 1 ? "" : "s")} · {Format.Bytes(session.State.TotalBytes)}",
            Subtitle = $"To {plan.Peer.Name} ({plan.Peer.Address})",
        };
        vm.Refresh();
        _ = Task.Run(() => session.RunAsync(cts.Token));
        return vm;
    }

    public void Refresh()
    {
        if (_final)
            return; // finished transfers need no further UI work
        var s = _state;
        var phase = s.Phase;
        long done = s.DoneBytes,
            total = s.TotalBytes;
        long now = Stopwatch.GetTimestamp();

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
            _ => ("", Grey),
        };

        IsAwaiting = phase == Phase.AwaitingYou;
        IsActive = phase is Phase.Connecting or Phase.WaitingForPeer or Phase.Transferring;
        IsFinished = phase is Phase.Completed or Phase.Failed or Phase.Cancelled or Phase.Declined;
        CanRetry = !IsIncoming && phase is Phase.Failed or Phase.Cancelled or Phase.Declined;
        if (IsFinished)
            _final = true;
    }

    public void CancelQuietly()
    {
        _cts?.Cancel();
        _job?.Cancel();
    }

    [RelayCommand]
    void Accept() => _job?.Accept(_owner.DownloadDir);

    [RelayCommand]
    void Decline() => _job?.Decline();

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
}
