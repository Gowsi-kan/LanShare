using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanShare.Core;
using LanShare.Services;
using LanShare.Views;
using LanShare.Web;
using QRCoder;

namespace LanShare.ViewModels;

public sealed class SharedFileItem(SharedFile file, Action<string> remove)
{
    public string Name { get; } = file.Name;
    public string SizeText { get; } = Format.Bytes(file.Size);
    public RelayCommand RemoveCommand { get; } = new(() => remove(file.Id));
}

public sealed partial class PhoneAccessViewModel : ObservableObject, IDisposable
{
    readonly WebShareServer _server;

    public event Action<WebUpload>? UploadStarted;

    public ObservableCollection<string> Addresses { get; } = [];
    public ObservableCollection<SharedFileItem> Shared { get; } = [];
    public ObservableCollection<TransferViewModel> Transfers { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ToggleText))]
    bool _isRunning;

    [ObservableProperty]
    bool _busy;

    [ObservableProperty]
    string _url = "";

    [ObservableProperty]
    Bitmap? _qr;

    [ObservableProperty]
    string _status = "Phone access is off.";

    [ObservableProperty]
    string _firewallText = "";

    [ObservableProperty]
    string? _selectedAddress;

    [ObservableProperty]
    string _pin = "";

    [ObservableProperty]
    bool _askApproval = true;

    [ObservableProperty]
    bool _useHttps;

    public string ToggleText => IsRunning ? "Stop phone access" : "Start phone access";
    public bool HasMultipleAddresses => Addresses.Count > 1;

    readonly X509Certificate2 _cert;
    readonly Notifier _notifier;

    public PhoneAccessViewModel(
        Func<string> downloadDir,
        string deviceName,
        X509Certificate2 cert,
        Notifier notifier
    )
    {
        _cert = cert;
        _notifier = notifier;
        _server = new WebShareServer(deviceName, downloadDir);
        _server.UploadStarted += up => UploadStarted?.Invoke(up);
        _server.PinChanged += pin => Dispatcher.UIThread.Post(() => Pin = pin);
        _server.ApproveUploads = ApproveAsync;
    }

    partial void OnSelectedAddressChanged(string? value) => BuildUrl();

    [RelayCommand]
    async Task ToggleAsync()
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            if (IsRunning)
            {
                await _server.StopAsync();
                IsRunning = false;
                BuildUrl();
                FirewallText = "";
                Status = "Phone access is off.";
                Pin = "";
            }
            else
            {
                await _server.StartAsync(WebShareServer.DefaultPort, UseHttps ? _cert : null);
                Pin = _server.Pin;
                Addresses.Clear();
                foreach (var a in LanAddress.Candidates())
                    Addresses.Add(a);
                OnPropertyChanged(nameof(HasMultipleAddresses));
                IsRunning = true;
                SelectedAddress = Addresses.FirstOrDefault();
                BuildUrl();
                Status =
                    Addresses.Count == 0
                        ? "No network address found. Connect to Wi-Fi / LAN first."
                        : "Running. Scan the QR code with the phone camera (same Wi-Fi).";
            }
        }
        catch (Exception e)
        {
            Status = $"Could not change phone access: {e.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    async Task OpenFirewallAsync()
    {
        if (!IsRunning)
            return;
        FirewallText = "Waiting for the administrator prompt…";
        var r = await FirewallHelper.OpenWebPortAsync(_server.Port);
        FirewallText = r.Message;
    }

    [RelayCommand]
    void NewPin()
    {
        if (IsRunning)
            _server.RotatePin(); // everyone has to log in again
    }

    async Task<bool> ApproveAsync(UploadRequest req, CancellationToken ct)
    {
        if (!AskApproval)
            return true;
        _notifier.Show(
            "Phone wants to send files",
            $"{req.Device} ({req.Peer}): {req.Files.Count} file(s)"
        );
        long total = req.Files.Sum(f => f.Size);
        var lines = req.Files.Take(50).Select(f => $"{f.Name} · {Format.Bytes(f.Size)}").ToList();
        if (req.Files.Count > 50)
            lines.Add($"… and {req.Files.Count - 50} more");
        return await Dispatcher.UIThread.InvokeAsync(() =>
            PromptWindow.ShowAsync(
                "Phone upload request",
                $"{req.Device} ({req.Peer}) wants to send {req.Files.Count} file(s), {Format.Bytes(total)}",
                "Only accept if this is your phone.",
                null,
                "Accept",
                "Decline",
                ct,
                lines
            )
        );
    }

    public void AddShared(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            _server.AddShared(p);
        RefreshShared();
    }

    void Remove(string id)
    {
        _server.RemoveShared(id);
        RefreshShared();
    }

    void RefreshShared()
    {
        Shared.Clear();
        foreach (var f in _server.Shared)
            Shared.Add(new SharedFileItem(f, Remove));
    }

    void BuildUrl()
    {
        var old = Qr;
        if (!IsRunning || SelectedAddress is null)
        {
            Url = "";
            Qr = null;
        }
        else
        {
            Url =
                $"{(_server.UseHttps ? "https" : "http")}://{SelectedAddress}:{_server.Port}/?t={_server.Token}";
            Qr = MakeQr(Url);
        }
        old?.Dispose();
    }

    static Bitmap MakeQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return new Bitmap(new MemoryStream(png));
    }

    public void Dispose()
    {
        try
        {
            Task.Run(() => _server.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }
}
