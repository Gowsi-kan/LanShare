using Avalonia;
using LanShare.Services;

namespace LanShare;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // second start: hand the files / "show window" request to the running instance and exit
        if (!SingleInstance.TryBecomePrimary(args))
            return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
