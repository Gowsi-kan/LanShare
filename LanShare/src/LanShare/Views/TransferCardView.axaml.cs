using Avalonia.Controls;
using Avalonia.Interactivity;
using LanShare.ViewModels;

namespace LanShare.Views;

public partial class TransferCardView : UserControl
{
    public TransferCardView() => InitializeComponent();

    async void OnCopyDetails(object? sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        try
        {
            if (
                DataContext is not TransferViewModel vm
                || TopLevel.GetTopLevel(this)?.Clipboard is not { } cb
            )
                return;
            if (btn is not null)
                btn.Content = "Collecting…";
            await cb.SetTextAsync(await vm.BuildDetailsAsync());
            if (btn is not null)
            {
                btn.Content = "Copied ✓";
                await Task.Delay(1500);
            }
        }
        catch { }
        finally
        {
            if (btn is not null)
                btn.Content = "Copy details";
        }
    }
}
