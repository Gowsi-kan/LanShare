using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LanShare.Views;

public partial class PromptWindow : Window
{
    bool _result;

    public PromptWindow() => InitializeComponent();

    /// <summary>Shows the prompt (works while the main window is hidden in the tray). true = confirmed.</summary>
    public static Task<bool> ShowAsync(
        string title,
        string heading,
        string body,
        string? code,
        string confirmText,
        string cancelText,
        CancellationToken ct,
        IEnumerable<string>? lines = null
    )
    {
        var w = new PromptWindow();
        w.TitleText.Text = title;
        w.HeadingText.Text = heading;
        w.BodyText.Text = body;
        w.ConfirmBtn.Content = confirmText;
        w.CancelBtn.Content = cancelText;
        if (code is not null)
        {
            w.CodeText.Text = code;
            w.CodeBox.IsVisible = true;
        }
        if (lines is not null)
        {
            w.Lines.ItemsSource = lines.ToList();
            w.LineScroll.IsVisible = true;
        }

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var reg = ct.Register(() => Dispatcher.UIThread.Post(() => w.Close()));
        w.Closed += (_, _) =>
        {
            reg.Dispose();
            tcs.TrySetResult(w._result);
        };
        w.Show();
        w.Activate();
        return tcs.Task;
    }

    void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }

    void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
