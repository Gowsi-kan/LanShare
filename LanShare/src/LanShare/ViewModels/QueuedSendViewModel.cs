using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanShare.Core;

namespace LanShare.ViewModels;

public sealed partial class QueuedSendViewModel : ObservableObject
{
    readonly MainViewModel _owner;

    public Guid Id { get; } = Guid.NewGuid();
    public SendPlan Plan { get; }
    public long TotalBytes { get; }
    public string FilePreview { get; }

    [ObservableProperty]
    string _errorText = "";

    public QueuedSendViewModel(MainViewModel owner, SendPlan plan)
    {
        _owner = owner;
        TotalBytes = plan.TotalBytes ?? plan.Files.Sum(file => new FileInfo(file.Full).Length);
        Plan = plan with { QueueId = Id, TotalBytes = TotalBytes };
        var names = plan.Files.Take(4).Select(file => Path.GetFileName(file.Rel)).ToArray();
        FilePreview = string.Join(", ", names)
            + (plan.Files.Count > names.Length ? $", +{plan.Files.Count - names.Length} more" : "");
        PeerText = $"{plan.Peer.Name} ({plan.Peer.Address})";
        FileCount = plan.Files.Count;
        SizeText = LanShare.Core.Format.Bytes(TotalBytes);
    }

    public string PeerText { get; }
    public int FileCount { get; }
    public string SizeText { get; }

    [RelayCommand]
    void MoveUp() => _owner.MoveQueuedSend(this, -1);

    [RelayCommand]
    void MoveDown() => _owner.MoveQueuedSend(this, 1);

    [RelayCommand]
    void Remove() => _owner.RemoveQueuedSend(this);
}

public sealed class TransferHistoryItem
{
    static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#34C77B"));
    static readonly IBrush CompletedSurface = new SolidColorBrush(Color.Parse("#142C27"));
    static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#FF7180"));
    static readonly IBrush FailedSurface = new SolidColorBrush(Color.Parse("#39212C"));
    static readonly IBrush OtherBrush = new SolidColorBrush(Color.Parse("#F5B942"));
    static readonly IBrush OtherSurface = new SolidColorBrush(Color.Parse("#342B1C"));

    readonly MainViewModel _owner;
    readonly SendPlan? _retryPlan;

    public TransferHistoryRecord Record { get; }
    public string Timestamp => Record.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Title => $"{Record.Direction} · {Record.Peer}";
    public string Detail => $"{Record.Summary} · {LanShare.Core.Format.Bytes(Record.TotalBytes)}";
    public string DirectionGlyph => Record.Direction == "Received" ? "↓" : "↑";
    public string Summary => Record.Summary;
    public string SizeText => LanShare.Core.Format.Bytes(Record.TotalBytes);
    public string Outcome => Record.Outcome.ToUpperInvariant();
    public IBrush OutcomeBrush => Record.Outcome switch
    {
        "Completed" => CompletedBrush,
        "Failed" => FailedBrush,
        _ => OtherBrush,
    };
    public IBrush OutcomeSurface => Record.Outcome switch
    {
        "Completed" => CompletedSurface,
        "Failed" => FailedSurface,
        _ => OtherSurface,
    };
    public string? Error => Record.Error;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool CanRetry => _retryPlan is not null;
    public bool CanOpenLocation => !string.IsNullOrWhiteSpace(Record.Location);

    public TransferHistoryItem(
        MainViewModel owner,
        TransferHistoryRecord record,
        SendPlan? retryPlan = null
    )
    {
        _owner = owner;
        Record = record;
        _retryPlan = retryPlan;
        RetryCommand = new RelayCommand(Retry, () => CanRetry);
        OpenLocationCommand = new RelayCommand(OpenLocation, () => CanOpenLocation);
    }

    public IRelayCommand RetryCommand { get; }
    public IRelayCommand OpenLocationCommand { get; }

    void Retry()
    {
        if (_retryPlan is not null)
            _owner.QueueRetry(_retryPlan);
    }

    void OpenLocation()
    {
        if (Record.Location is { } location)
            _owner.OpenHistoryLocation(location);
    }
}
