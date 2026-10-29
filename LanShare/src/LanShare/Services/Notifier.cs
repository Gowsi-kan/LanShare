using Avalonia;
using Avalonia.Threading;
using LanShare.Views;

namespace LanShare.Services;

/// <summary>Small always-on-top pop-ups in the screen corner (work while the main window is hidden in the tray).</summary>
public sealed class Notifier
{
    const int Margin = 14,
        Gap = 8,
        ToastW = 340,
        ToastH = 88,
        MaxVisible = 4;
    readonly List<ToastWindow> _open = [];

    public bool Enabled { get; set; } = true;

    public void Show(string title, string body, Action? onClick = null, bool error = false)
    {
        if (!Enabled)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                while (_open.Count >= MaxVisible)
                {
                    var oldest = _open[0];
                    _open.RemoveAt(0);
                    oldest.Close();
                }
                var w = new ToastWindow(title, body, onClick, error);
                w.Closed += (_, _) =>
                {
                    _open.Remove(w);
                    Layout();
                };
                _open.Add(w);
                w.Show();
                Layout();
            }
            catch { }
        });
    }

    void Layout()
    {
        if (_open.Count == 0)
            return;
        var screen = _open[0].Screens.Primary;
        if (screen is null)
            return;
        double s = screen.Scaling;
        var wa = screen.WorkingArea;
        for (int k = 0; k < _open.Count; k++)
        {
            int fromBottom = _open.Count - 1 - k; // newest sits in the corner
            int x = wa.Right - (int)((ToastW + Margin) * s);
            int y = wa.Bottom - (int)(((ToastH + Gap) * (fromBottom + 1) + Margin - Gap) * s);
            _open[k].Position = new PixelPoint(x, y);
        }
    }
}
