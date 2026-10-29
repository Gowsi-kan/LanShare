using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanShare.Core;

namespace LanShare.ViewModels;

public sealed record SendPlan(Peer Peer, IReadOnlyList<(string Full, string Rel)> Files);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly TransferService _service;
    readonly DiscoveryService _discovery;
    readonly DispatcherTimer _timer;
    readonly RateLimiter _limiter = new();
    int _tick;

    public ObservableCollection<Peer> Peers { get; } = [];
    public ObservableCollection<TransferViewModel> Transfers { get; } = [];

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

    public string StreamsText =>
        StreamsSetting < 0.5 ? "Auto" : $"{(int)Math.Round(StreamsSetting)}";
    public string TargetText =>
        SelectedPeer is { } p
            ? $"Sending to {p.Name} ({p.Address})"
            : "Select a device on the left, then drop files";

    public MainViewModel()
    {
        DeviceName = Environment.MachineName;
        _downloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "LanShare"
        );

        _service = new TransferService(DeviceName);
        _service.IncomingOffer += OnIncoming;
        _service.Start();

        _discovery = new DiscoveryService(_service.DeviceId, DeviceName, _service.Port);
        _discovery.PeersChanged += () =>
            Dispatcher.UIThread.Post(() => SyncPeers(_discovery.Snapshot));
        _discovery.Start();

        Phone = new PhoneAccessViewModel(() => DownloadDir, DeviceName);
        Phone.UploadStarted += up =>
            Dispatcher.UIThread.Post(() =>
            {
                var t = TransferViewModel.Web(this, up);
                Transfers.Insert(0, t); // main window
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
            Transfers.Insert(0, TransferViewModel.Incoming(this, job));
            if (AutoAccept)
                job.Accept(DownloadDir);
        });

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
            StatusMessage = "Select a device first.";
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
        await StartSendAsync(new SendPlan(peer, files));
    }

    // The SenderSession constructor stats every file; with big folders / network shares that must not run on the UI thread.
    internal async Task StartSendAsync(SendPlan plan)
    {
        var opts = new SendOptions((int)Math.Round(StreamsSetting), CompressionEnabled, _limiter);
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
            Transfers.Insert(0, TransferViewModel.Outgoing(this, session, plan));
            StatusMessage = "";
        }
        catch (Exception e)
        {
            StatusMessage = e.Message;
        }
    }

    internal async Task RetryAsync(TransferViewModel t)
    {
        if (t.Plan is null)
            return;
        Transfers.Remove(t);
        await StartSendAsync(t.Plan);
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
