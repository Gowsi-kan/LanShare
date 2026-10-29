using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Fixed-size window: if the screen's work area is smaller than the design size, shrink once at startup and re-center.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
            if (screen is null) return;
            double scale = screen.Scaling;
            var wa = screen.WorkingArea;
            Width = Math.Min(Width, wa.Width / scale);
            Height = Math.Min(Height, wa.Height / scale);
            Position = new PixelPoint(
                wa.X + (int)((wa.Width - Width * scale) / 2),
                wa.Y + (int)((wa.Height - Height * scale) / 2));
        }
        catch { }
    }

    void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    // async void handlers: an escaping exception would crash the whole app, so everything is guarded.
    async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var items = e.Data.GetFiles();
            if (items is null) return;
            var paths = ToPaths(items);
            if (paths.Count > 0) await Vm.SendPathsAsync(paths);
        }
        catch (Exception ex) { Vm.ReportError(ex.Message); }
    }

    async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var res = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { AllowMultiple = true, Title = "Select files to send" });
            if (res.Count > 0) await Vm.SendPathsAsync(ToPaths(res));
        }
        catch (Exception ex) { Vm.ReportError(ex.Message); }
    }

    async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var res = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { AllowMultiple = true, Title = "Select folders to send" });
            if (res.Count > 0) await Vm.SendPathsAsync(ToPaths(res));
        }
        catch (Exception ex) { Vm.ReportError(ex.Message); }
    }

    async void OnPickDownloadDir(object? sender, RoutedEventArgs e)
    {
        try
        {
            var res = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { AllowMultiple = false, Title = "Save received files to" });
            if (res.Count > 0 && res[0].TryGetLocalPath() is { } p) Vm.DownloadDir = p;
        }
        catch (Exception ex) { Vm.ReportError(ex.Message); }
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
        _phoneWindow.Show(this);
    }
}
