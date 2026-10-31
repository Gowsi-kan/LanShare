using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanShare.Core;
using LanShare.Services;

namespace LanShare.ViewModels;

public sealed record SendPlan(
    Peer Peer,
    IReadOnlyList<(string Full, string Rel)> Files,
    Guid? QueueId = null,
    long? TotalBytes = null
);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly TransferService _service;
    readonly DiscoveryService _discovery;
    readonly DispatcherTimer _timer;
    readonly RateLimiter _limiter = new();
    int _tick;

    readonly AppSettings _settings;
    readonly TransferHistoryStore _historyStore = new();
    readonly List<string> _pending = [];
    bool _initializing = true;
    bool _trayHintShown;
    bool _startingQueuedSend;
    Guid? _activeQueuedSendId;

    public ObservableCollection<Peer> Peers { get; } = [];
    public ObservableCollection<TransferViewModel> Transfers { get; } = [];
    public ObservableCollection<QueuedSendViewModel> SendQueue { get; } = [];
    public ObservableCollection<TransferHistoryItem> History { get; } = [];
    public ObservableCollection<TransferHistoryItem> FilteredHistory { get; } = [];
    public bool HasTransfers => Transfers.Count > 0;
    public bool HasQueuedSends => SendQueue.Count > 0;
    public bool HasFilteredHistory => FilteredHistory.Count > 0;
    public string QueueCountText => $"Queue · {SendQueue.Count}";
    public string HistoryCountText => $"History · {History.Count}";

    public Notifier Notifier { get; } = new();
    public Func<bool> IsWindowActive { get; set; } = () => false;
    public Action? ShowMainWindow { get; set; }

    public string DeviceName { get; }
    public PhoneAccessViewModel Phone { get; }
    public string ListenText => $"Listening · TCP {_service.Port} · UDP {Protocol.DiscoveryPort}";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TargetText))]
    Peer? _selectedPeer;

    [ObservableProperty]
    string _downloadDir;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StreamsText))]
    double _streamsSetting; // 0 = auto-tune

    [ObservableProperty]
    bool _compressionEnabled = true;

    [ObservableProperty]
    int _bandwidthIndex;

    [ObservableProperty]
    bool _autoAccept;

    [ObservableProperty]
    string _manualHost = "";

    [ObservableProperty]
    string _statusMessage = "";

    [ObservableProperty]
    string _peersHint = "Searching the network…";

    [ObservableProperty]
    string _diskText = "";

    [ObservableProperty]
    double _diskUsedPercent;

    [ObservableProperty]
    string _firewallText = "Checking firewall…";

    [ObservableProperty]
    bool _firewallNeedsAction;

    [ObservableProperty]
    bool _firewallBusy;

    [ObservableProperty]
    bool _runInBackground;

    [ObservableProperty]
    bool _notificationsEnabled;

    [ObservableProperty]
    bool _startWithSystem;

    [ObservableProperty]
    bool _contextMenuEnabled;

    [ObservableProperty]
    bool _hasPending;

    [ObservableProperty]
    string _pendingText = "";

    [ObservableProperty]
    string _historyQuery = "";

    [ObservableProperty]
    int _activityTabIndex;

    public string StreamsText =>
        StreamsSetting < 0.5 ? "Auto" : $"{(int)Math.Round(StreamsSetting)}";
    public string TargetText =>
        SelectedPeer is { } p
            ? $"Sending to {p.Name} ({p.Address})"
            : "Select a device on the left, then drop files";

    readonly SecurityContext _security;
    public ObservableCollection<TrustedDeviceItem> TrustedDevices { get; } = [];
    public string OwnFingerprint => GroupFingerprint(_security.Identity.Fingerprint);

    public MainViewModel(AppSettings settings)
    {
        Transfers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTransfers));
        SendQueue.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasQueuedSends));
            OnPropertyChanged(nameof(QueueCountText));
        };
        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HistoryCountText));
            RefreshHistoryFilter();
        };
        foreach (var record in _historyStore.Load())
            History.Add(new TransferHistoryItem(this, record));
        DeviceName = Environment.MachineName;
        _settings = settings;
        RunInBackground = settings.RunInBackground;
        NotificationsEnabled = settings.Notifications;
        StartWithSystem = AutoStart.IsEnabled(); // reflect what the OS really has
        ContextMenuEnabled = ContextMenuIntegration.IsInstalled();
        _initializing = false;
        _downloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "LanShare"
        );

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanShare"
        );
        _security = new SecurityContext(
            DeviceIdentity.LoadOrCreate(dataDir),
            TrustStore.Load(Path.Combine(dataDir, "trusted.json"))
        )
        {
            PairingUi = new UiPrompts(Notifier),
        };
        _security.Trust.Changed += () => Dispatcher.UIThread.Post(RefreshTrusted);
        RefreshTrusted();

        _service = new TransferService(DeviceName, _security);
        _service.IncomingOffer += OnIncoming;
        _service.Start();

        _discovery = new DiscoveryService(_service.DeviceId, DeviceName, _service.Port);
        _discovery.PeersChanged += () =>
            Dispatcher.UIThread.Post(() => SyncPeers(_discovery.Snapshot));
        _discovery.Start();

        Phone = new PhoneAccessViewModel(
            () => DownloadDir,
            DeviceName,
            _security.Identity.Certificate,
            Notifier
        );
        Phone.UploadStarted += up =>
            Dispatcher.UIThread.Post(() =>
            {
                var t = TransferViewModel.Web(this, up);
                AddTransfer(t);
                Phone.Transfers.Insert(0, t); // phone window (same live object)
            });

        _ = RefreshFirewallAsync();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        RefreshDisk();
    }

    void OnTick()
    {
        foreach (var t in Transfers)
            t.Refresh();
        if (++_tick % 10 == 0)
            RefreshDisk();
    }

    partial void OnDownloadDirChanged(string value) => RefreshDisk();

    static readonly int[] BandwidthMBps = [0, 10, 25, 50, 100, 250, 500];
    public IReadOnlyList<string> BandwidthOptions { get; } =
    ["Unlimited", "10 MB/s", "25 MB/s", "50 MB/s", "100 MB/s", "250 MB/s", "500 MB/s"];

    partial void OnBandwidthIndexChanged(int value) =>
        _limiter.BytesPerSecond =
            BandwidthMBps[Math.Clamp(value, 0, BandwidthMBps.Length - 1)] * 1_048_576L;

    partial void OnHistoryQueryChanged(string value) => RefreshHistoryFilter();

    void RefreshHistoryFilter()
    {
        var query = HistoryQuery.Trim();
        FilteredHistory.Clear();
        foreach (var item in History)
        {
            var record = item.Record;
            if (
                query.Length == 0
                || record.Peer.Contains(query, StringComparison.OrdinalIgnoreCase)
                || record.Direction.Contains(query, StringComparison.OrdinalIgnoreCase)
                || record.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
                || record.Outcome.Contains(query, StringComparison.OrdinalIgnoreCase)
                || record.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
                || (record.Error?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            )
                FilteredHistory.Add(item);
        }
        OnPropertyChanged(nameof(HasFilteredHistory));
    }

    public void ReportError(string message) => StatusMessage = message;

    async Task RefreshFirewallAsync() =>
        ApplyFirewall(await FirewallHelper.CheckAsync(_service.Port));

    void ApplyFirewall(FirewallStatus s)
    {
        FirewallText = s.Message;
        FirewallNeedsAction = s.State is FirewallState.Closed or FirewallState.Unknown;
    }

    [RelayCommand]
    async Task OpenFirewallAsync()
    {
        if (FirewallBusy)
            return;
        FirewallBusy = true;
        try
        {
            ApplyFirewall(await FirewallHelper.OpenAsync(_service.Port));
        }
        finally
        {
            FirewallBusy = false;
        }
    }

    int _diskBusy;

    // DriveInfo can block for seconds (network share, sleeping/removable disk): never call it on the UI thread.
    void RefreshDisk()
    {
        if (Interlocked.Exchange(ref _diskBusy, 1) == 1)
            return;
        var dir = DownloadDir;
        _ = Task.Run(() =>
        {
            var (text, pct) = ProbeDisk(dir);
            Dispatcher.UIThread.Post(() =>
            {
                DiskText = text;
                DiskUsedPercent = pct;
                Interlocked.Exchange(ref _diskBusy, 0);
            });
        });
    }

    static (string Text, double Percent) ProbeDisk(string dir)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            long total = d.TotalSize,
                free = d.AvailableFreeSpace;
            return (
                $"{Format.Bytes(free)} free of {Format.Bytes(total)}",
                total > 0 ? (total - free) * 100.0 / total : 0
            );
        }
        catch
        {
            return ("—", 0);
        }
    }

    void SyncPeers(IReadOnlyList<Peer> snap)
    {
        for (int i = Peers.Count - 1; i >= 0; i--)
            if (snap.All(p => p.Id != Peers[i].Id))
                Peers.RemoveAt(i);

        foreach (var p in snap)
        {
            int idx = -1;
            for (int i = 0; i < Peers.Count; i++)
                if (Peers[i].Id == p.Id)
                {
                    idx = i;
                    break;
                }
            if (idx < 0)
                Peers.Add(p);
            else if (Peers[idx] != p)
                Peers[idx] = p;
        }
        PeersHint = Peers.Count == 0 ? "Searching the network…" : "";
    }

    void OnIncoming(IncomingJob job) =>
        Dispatcher.UIThread.Post(() =>
        {
            var transfer = TransferViewModel.Incoming(this, job);
            AddTransfer(transfer);
            if (AutoAccept)
                job.Accept(DownloadDir);
            else
            {
                ActivityTabIndex = 0;
                _ = CheckIncomingConflictsAsync(transfer, job.Offer);
            }
            if (!IsWindowActive())
                Notifier.Show(
                    AutoAccept ? "Receiving files" : "Incoming files",
                    $"{job.Peer}: {job.Offer.Files.Count} file(s) · {Format.Bytes(job.Offer.TotalBytes)}"
                        + (AutoAccept ? "" : " — click to review"),
                    ShowMainWindow
                );
        });

    static async Task CheckIncomingConflictsAsync(
        TransferViewModel transfer,
        Offer offer
    )
    {
        try
        {
            var count = await Task.Run(
                () => ReceiverSession.CountConflicts(offer, transfer.ReceiveDirectory)
            );
            Dispatcher.UIThread.Post(() => transfer.SetConflictPreview(count));
        }
        catch (
            Exception e
        ) when (
            e is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
        )
        {
            Dispatcher.UIThread.Post(() => transfer.SetConflictPreviewError(e.Message));
        }
    }

    [RelayCommand]
    void AddPeer()
    {
        if (
            !IPAddress.TryParse(ManualHost.Trim(), out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
        )
        {
            StatusMessage = "Enter a valid IPv4 address.";
            return;
        }
        Span<byte> id = stackalloc byte[16];
        ip.GetAddressBytes().CopyTo(id);
        _discovery.AddManual(new Peer(new Guid(id), ip.ToString(), ip, Protocol.DefaultTcpPort));
        ManualHost = "";
        StatusMessage = "";
    }

    public async Task SendPathsAsync(IReadOnlyList<string> paths)
    {
        if (SelectedPeer is not { } peer)
        {
            QueuePaths(paths);
            StatusMessage = "";
            return;
        }
        StatusMessage = "Scanning files…";
        List<(string Full, string Rel)> files;
        try
        {
            files = await Task.Run(() => Expand(paths));
        }
        catch (Exception e)
        {
            StatusMessage = e.Message;
            return;
        }
        if (files.Count == 0)
        {
            StatusMessage = "Nothing to send.";
            return;
        }
        QueuedSendViewModel item;
        try
        {
            long totalBytes = await Task.Run(() => files.Sum(file => new FileInfo(file.Full).Length));
            item = new QueuedSendViewModel(this, new SendPlan(peer, files, TotalBytes: totalBytes));
        }
        catch (Exception e)
        {
            StatusMessage = e.Message;
            return;
        }
        SendQueue.Add(item);
        ActivityTabIndex = 1;
        StatusMessage = $"Added to send queue: {item.FileCount} files · {item.SizeText}. Review the queue before sending.";
    }

    [RelayCommand]
    async Task StartQueuedSendAsync()
    {
        if (_startingQueuedSend || _activeQueuedSendId is not null || SendQueue.Count == 0)
            return;

        _startingQueuedSend = true;
        var item = SendQueue[0];
        _activeQueuedSendId = item.Id;
        item.ErrorText = "";
        var transfer = await StartSendAsync(item.Plan);
        if (transfer is null)
        {
            _activeQueuedSendId = null;
            item.ErrorText = StatusMessage;
        }
        else
        {
            SendQueue.Remove(item);
            ActivityTabIndex = 0;
            StatusMessage = "Transfer started. The next queued batch starts when this transfer finishes.";
        }
        _startingQueuedSend = false;
    }

    internal void MoveQueuedSend(QueuedSendViewModel item, int delta)
    {
        var index = SendQueue.IndexOf(item);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < SendQueue.Count)
            SendQueue.Move(index, target);
    }

    internal void RemoveQueuedSend(QueuedSendViewModel item)
    {
        SendQueue.Remove(item);
        StatusMessage = "Queued batch removed.";
    }

    // The SenderSession constructor stats every file; with big folders / network shares that must not run on the UI thread.
    internal async Task<TransferViewModel?> StartSendAsync(SendPlan plan)
    {
        var opts = new SendOptions(
            (int)Math.Round(StreamsSetting),
            CompressionEnabled,
            _limiter,
            _security
        );
        StatusMessage = "Preparing transfer…";
        try
        {
            var session = await Task.Run(() =>
                new SenderSession(
                    new IPEndPoint(plan.Peer.Address, plan.Peer.Port),
                    DeviceName,
                    plan.Files,
                    opts
                )
            );
            var transfer = TransferViewModel.Outgoing(this, session, plan);
            AddTransfer(transfer);
            StatusMessage = "";
            return transfer;
        }
        catch (Exception e)
        {
            StatusMessage = e.Message;
            return null;
        }
    }

    internal async Task RetryAsync(TransferViewModel t)
    {
        if (t.Plan is null)
            return;
        QueueRetry(t.Plan);
        await Task.CompletedTask;
    }

    internal void QueueRetry(SendPlan plan)
    {
        SendQueue.Add(
            new QueuedSendViewModel(this, plan with { QueueId = null })
        );
        ActivityTabIndex = 1;
        StatusMessage = "Retry added to the send queue. Review it before starting.";
    }

    internal void OpenHistoryLocation(string location)
    {
        try
        {
            if (!Directory.Exists(location))
            {
                StatusMessage = "The saved location is no longer available.";
                return;
            }
            Process.Start(new ProcessStartInfo(location) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = $"Could not open the saved location: {e.Message}";
        }
    }

    internal void Dismiss(TransferViewModel t)
    {
        Transfers.Remove(t);
        Phone.Transfers.Remove(t);
    }

    static List<(string Full, string Rel)> Expand(IEnumerable<string> paths)
    {
        var list = new List<(string Full, string Rel)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string full, string rel)
        {
            var r = rel;
            var ext = Path.GetExtension(rel);
            var stem = rel[..^ext.Length];
            for (int n = 1; !seen.Add(r); n++)
                r = $"{stem} ({n}){ext}";
            list.Add((full, r));
        }

        foreach (var p in paths)
        {
            if (File.Exists(p))
                Add(p, Path.GetFileName(p));
            else if (Directory.Exists(p))
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) ?? p;
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                };
                foreach (var f in Directory.EnumerateFiles(p, "*", opts))
                    Add(f, Path.GetRelativePath(parent, f).Replace('\\', '/'));
            }
        }
        return list;
    }

    partial void OnRunInBackgroundChanged(bool value)
    {
        if (_initializing)
            return;
        _settings.RunInBackground = value;
        _settings.Save();
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        Notifier.Enabled = value;
        if (_initializing)
            return;
        _settings.Notifications = value;
        _settings.Save();
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_initializing)
            return;
        if (AutoStart.Set(value) is { } err)
        {
            StatusMessage = err;
            _initializing = true;
            StartWithSystem = !value; // revert the checkbox
            _initializing = false;
        }
    }

    partial void OnContextMenuEnabledChanged(bool value)
    {
        if (_initializing)
            return;
        if (ContextMenuIntegration.Set(value) is { } err)
        {
            StatusMessage = err;
            _initializing = true;
            ContextMenuEnabled = !value;
            _initializing = false;
        }
    }

    partial void OnSelectedPeerChanged(Peer? value) => UpdatePending();

    // ---- "Send with LanShare" / open-with: paths wait here until a device is chosen
    public void QueuePaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if ((File.Exists(p) || Directory.Exists(p)) && !_pending.Contains(p))
                _pending.Add(p);
        UpdatePending();
        ShowMainWindow?.Invoke();
    }

    void UpdatePending()
    {
        HasPending = _pending.Count > 0;
        var what = _pending.Count switch
        {
            0 => "",
            1 => $"Ready to send: {Path.GetFileName(_pending[0])}",
            _ => $"Ready to send: {_pending.Count} items",
        };
        PendingText =
            what + (SelectedPeer is { } p ? $" → {p.Name}" : " — pick a device on the left");
    }

    [RelayCommand]
    async Task SendPendingAsync()
    {
        if (_pending.Count == 0)
            return;
        if (SelectedPeer is null)
        {
            StatusMessage = "Pick a device on the left first.";
            return;
        }
        var paths = _pending.ToList();
        _pending.Clear();
        UpdatePending();
        await SendPathsAsync(paths);
    }

    [RelayCommand]
    void CancelPending()
    {
        _pending.Clear();
        UpdatePending();
    }

    // ---- notifications / tray
    void AddTransfer(TransferViewModel t)
    {
        t.Finished += OnTransferFinished;
        Transfers.Insert(0, t);
    }

    void OnTransferFinished(TransferViewModel t)
    {
        DiagLog.Info("Transfer finished: " + t.Report().Replace(Environment.NewLine, " | "));
        var state = t.State;
        var record = new TransferHistoryRecord(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            t.IsIncoming ? "Received" : "Sent",
            t.PeerName,
            $"{state.TotalFiles} file{(state.TotalFiles == 1 ? "" : "s")}",
            state.TotalBytes,
            t.FinalPhase.ToString(),
            t.ErrorText,
            t.IsIncoming ? t.ReceiveDirectory : null
        );
        _ = Task.Run(() =>
        {
            var saved = _historyStore.Add(record, out var error);
            Dispatcher.UIThread.Post(() =>
            {
                if (saved)
                    History.Insert(0, new TransferHistoryItem(this, record, t.Plan));
                else
                    StatusMessage = $"Transfer finished, but its history could not be saved: {error}";
            });
        });

        if (t.Plan?.QueueId is { } queueId && _activeQueuedSendId == queueId)
        {
            _activeQueuedSendId = null;
            _startingQueuedSend = false;
            _ = StartQueuedSendAsync();
        }

        if (IsWindowActive())
            return; // the user is looking at the window
        switch (t.FinalPhase)
        {
            case Phase.Completed when t.IsIncoming:
                Notifier.Show(
                    "File received",
                    $"{t.Title} saved to {DownloadDir}",
                    OpenDownloadFolder
                );
                break;
            case Phase.Completed:
                Notifier.Show("Transfer finished", $"{t.Title} sent.", ShowMainWindow);
                break;
            case Phase.Failed:
                Notifier.Show(
                    "Transfer failed",
                    $"{t.Title}: {t.ErrorText ?? "unknown error"}",
                    ShowMainWindow,
                    error: true
                );
                break;
        }
    }

    public void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadDir);
            Process.Start(new ProcessStartInfo(DownloadDir) { UseShellExecute = true });
        }
        catch { }
    }

    public void OnHiddenToTray()
    {
        if (_trayHintShown)
            return;
        _trayHintShown = true;
        Notifier.Show(
            "LanShare is still running",
            "Receiving continues in the tray. Right-click the tray icon to quit.",
            ShowMainWindow
        );
    }

    public string TrayTooltip()
    {
        int active = 0;
        double speed = 0;
        foreach (var t in Transfers)
            if (t.IsActive)
            {
                active++;
                speed += t.SpeedBytesPerSec;
            }
        return active == 0
            ? "LanShare — idle"
            : $"LanShare — {active} active · {Format.Rate(speed)}";
    }

    void RefreshTrusted()
    {
        TrustedDevices.Clear();
        foreach (
            var d in _security.Trust.Items.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        )
            TrustedDevices.Add(new TrustedDeviceItem(d, fp => _security.Trust.Remove(fp)));
    }

    public static string GroupFingerprint(string fp, int chars = 32)
    {
        var s = fp.Length > chars ? fp[..chars] : fp;
        return string.Join(
            ' ',
            Enumerable.Range(0, s.Length / 4).Select(i => s.Substring(i * 4, 4))
        );
    }

    public async Task<string> BuildDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine(await SystemInfo.DescribeAsync(DownloadDir));
        sb.AppendLine();
        sb.AppendLine("Settings:");
        sb.AppendLine(
            $"  streams={(StreamsSetting < 0.5 ? "auto" : ((int)Math.Round(StreamsSetting)).ToString())}"
                + $" compression={CompressionEnabled} limit={BandwidthOptions[Math.Clamp(BandwidthIndex, 0, BandwidthOptions.Count - 1)]}"
                + $" autoAccept={AutoAccept} tray={RunInBackground}"
        );
        sb.AppendLine(
            $"  trusted devices: {TrustedDevices.Count} · discovered devices: {Peers.Count} · phone access: {(Phone.IsRunning ? "on" : "off")}"
        );
        sb.AppendLine();
        sb.AppendLine("Transfers in the window (newest first, file names left out):");
        foreach (var t in Transfers.Take(10))
            sb.AppendLine(t.Report()).AppendLine();
        sb.AppendLine("Recent log:");
        sb.AppendLine(DiagLog.Recent(200));
        return sb.ToString();
    }

    public string BuildStamp => BuildInfo.Stamp;

    public string LogFolderText =>
        DiagLog.LogDirectory is { } d
            ? $"Logs: {d}"
            : "Logging is unavailable: the log folder could not be created.";

    public void OpenLogFolder()
    {
        try
        {
            var d = DiagLog.LogDirectory;
            if (d is null)
            {
                StatusMessage = "Logging is unavailable: the log folder could not be created.";
                return;
            }
            Directory.CreateDirectory(d);
            Process.Start(new ProcessStartInfo(d) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = e.Message;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _discovery.Dispose();
        Phone.Dispose();
        foreach (var t in Transfers)
            t.CancelQuietly();
        try
        {
            Task.Run(() => _service.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }
}

public sealed class TrustedDeviceItem(TrustedDevice d, Action<string> forget)
{
    public string Name { get; } = d.Name;
    public string Detail { get; } = $"{d.Address} · paired {d.AddedUtc.ToLocalTime():yyyy-MM-dd}";
    public string Fingerprint { get; } = MainViewModel.GroupFingerprint(d.Fingerprint, 16);
    public RelayCommand ForgetCommand { get; } = new(() => forget(d.Fingerprint));
}
