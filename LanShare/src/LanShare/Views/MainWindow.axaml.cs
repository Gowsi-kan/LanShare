using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LanShare.ViewModels;

namespace LanShare.Views;

public partial class MainWindow : Window
{
    MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ---- custom title bar ----
    void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (Vm.RunInBackground)
            HideToTray();
        else
            WindowState = WindowState.Minimized;
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Fixed-size window: if the screen's work area is smaller than the design size, shrink once at startup and re-center.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
            if (screen is null)
                return;
            double scale = screen.Scaling;
            var wa = screen.WorkingArea;
            Width = Math.Min(Width, wa.Width / scale);
            Height = Math.Min(Height, wa.Height / scale);
            Position = new PixelPoint(
                wa.X + (int)((wa.Width - Width * scale) / 2),
                wa.Y + (int)((wa.Height - Height * scale) / 2)
            );
        }
        catch { }
    }

    void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    // async void handlers: an escaping exception would crash the whole app, so everything is guarded.
    async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var items = e.Data.GetFiles();
            if (items is null)
                return;
            var paths = ToPaths(items);
            if (paths.Count > 0)
                await Vm.SendPathsAsync(paths);
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex.Message);
        }
    }

    async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var res = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { AllowMultiple = true, Title = "Select files to send" }
            );
            if (res.Count > 0)
                await Vm.SendPathsAsync(ToPaths(res));
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex.Message);
        }
    }

    async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var res = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    AllowMultiple = true,
                    Title = "Select folders to send",
                }
            );
            if (res.Count > 0)
                await Vm.SendPathsAsync(ToPaths(res));
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex.Message);
        }
    }

    async void OnPickDownloadDir(object? sender, RoutedEventArgs e)
    {
        try
        {
            var res = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "Save received files to",
                }
            );
            if (res.Count > 0 && res[0].TryGetLocalPath() is { } p)
                Vm.DownloadDir = p;
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex.Message);
        }
    }

    static List<string> ToPaths(IEnumerable<IStorageItem> items) =>
        items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();

    PhoneAccessWindow? _phoneWindow;

    void OnPhoneAccess(object? sender, RoutedEventArgs e)
    {
        if (_phoneWindow is { IsVisible: true })
        {
            _phoneWindow.Activate();
            return;
        }
        _phoneWindow = new PhoneAccessWindow { DataContext = Vm.Phone };
        TrackChild(_phoneWindow);
        _phoneWindow.Show(this);
    }

    public bool AllowClose { get; set; }
    SettingsWindow? _settingsWindow;

    void HideToTray()
    {
        _phoneWindow?.Close();
        _phoneWindow = null;
        _settingsWindow?.Close();
        _settingsWindow = null;
        Hide();
        Vm.OnHiddenToTray();
    }

    // X button and Alt+F4 both end up here: hide instead of exit while "Run in background" is on
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (
            !AllowClose
            && Vm.RunInBackground
            && e.CloseReason
                is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        )
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (Application.Current as App)?.Quit(); // real close with background mode off = exit the app
    }

    void OnSettings(object? sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow { DataContext = Vm };
        TrackChild(_settingsWindow);
        _settingsWindow.Show(this);
    }

    readonly HashSet<Window> _children = [];

    void TrackChild(Window w)
    {
        _children.Add(w);
        w.Closed += (_, _) =>
        {
            _children.Remove(w);
            UpdateDim();
        };
        UpdateDim();
    }

    void UpdateDim()
    {
        bool dim = _children.Count > 0;
        DimOverlay.Opacity = dim ? 1 : 0;
        ContentRoot.Effect = dim ? new BlurEffect { Radius = 4 } : null; // delete this line for dimming without blur
    }
}
