using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LanShare.ViewModels;

namespace LanShare.Views;

public partial class PhoneAccessWindow : Window
{
    PhoneAccessViewModel Vm => (PhoneAccessViewModel)DataContext!;

    public PhoneAccessWindow() => InitializeComponent();

    void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();

    async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var res = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { AllowMultiple = true, Title = "Select files to share with phones" });
            Vm.AddShared(res.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception ex) { Vm.Status = ex.Message; }
    }

    async void OnCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } cb && !string.IsNullOrEmpty(Vm.Url)) await cb.SetTextAsync(Vm.Url);
        }
        catch { }
    }
}