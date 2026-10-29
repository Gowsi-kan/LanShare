using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LanShare.Services;
using LanShare.ViewModels;
using LanShare.Views;

namespace LanShare;

public partial class App : Application
{
    IClassicDesktopStyleApplicationLifetime? _desktop;
    MainViewModel? _vm;
    MainWindow? _window;
    DispatcherTimer? _trayTimer;
    bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the window must not end the app (tray)
            var args = desktop.Args ?? [];

            var settings = AppSettings.Load();
            var vm = new MainViewModel(settings);
            var window = new MainWindow { DataContext = vm };
            _vm = vm;
            _window = window;

            vm.IsWindowActive = () => window is { IsVisible: true, IsActive: true };
            vm.ShowMainWindow = ShowMain;

            // files forwarded by a second start (right-click "Send with LanShare") or "show window"
            SingleInstance.Received += paths =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (paths.Length > 0)
                        vm.QueuePaths(paths);
                    else
                        ShowMain();
                });

            var initial = SingleInstance.PathsFrom(args);
            bool background =
                args.Contains("--background") && settings.RunInBackground && initial.Length == 0;
            if (initial.Length > 0)
                vm.QueuePaths(initial);
            if (!background)
                ShowMain();

            _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trayTimer.Tick += (_, _) => UpdateTray();
            _trayTimer.Start();

            desktop.Exit += (_, _) =>
            {
                _trayTimer?.Stop();
                vm.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    void ShowMain()
    {
        if (_window is null)
            return;
        if (!_window.IsVisible)
            _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void OnTrayClicked(object? sender, EventArgs e) => ShowMain();

    void OnTrayOpen(object? sender, EventArgs e) => ShowMain();

    void OnTrayQuit(object? sender, EventArgs e) => Quit();

    public void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        if (_window is not null)
            _window.AllowClose = true;
        _desktop?.Shutdown();
    }

    void UpdateTray()
    {
        try
        {
            var icon = TrayIcon.GetIcons(this)?.FirstOrDefault();
            if (icon is not null && _vm is not null)
                icon.ToolTipText = _vm.TrayTooltip();
        }
        catch { }
    }
}
