using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LanShare.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
