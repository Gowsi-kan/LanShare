using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace LanShare.Views;

public partial class ToastWindow : Window
{
    readonly Action? _onClick;
    readonly DispatcherTimer _timer;

    public ToastWindow()
        : this("", "", null, false) { }

    public ToastWindow(string title, string body, Action? onClick, bool error)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        Accent.Background = new SolidColorBrush(Color.Parse(error ? "#FF5C6C" : "#34C77B"));
        _onClick = onClick;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 9 : 5) };
        _timer.Tick += (_, _) => Close();
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _onClick?.Invoke();
        Close();
    }

    void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
