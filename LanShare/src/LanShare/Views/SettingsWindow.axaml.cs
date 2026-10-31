using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LanShare.ViewModels;

namespace LanShare.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    MainViewModel Vm => (MainViewModel)DataContext!;

    void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    async void OnCopyDiagnostics(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } cb)
                return;
            CopyDiagBtn.Content = "Collecting…";
            await cb.SetTextAsync(await Vm.BuildDiagnosticsAsync());
            CopyDiagBtn.Content = "Copied ✓";
            await Task.Delay(1500);
        }
        catch { }
        finally
        {
            CopyDiagBtn.Content = "Copy diagnostics";
        }
    }

    void OnOpenLogs(object? sender, RoutedEventArgs e) => Vm.OpenLogFolder();
}
