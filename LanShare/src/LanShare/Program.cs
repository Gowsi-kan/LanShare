using Avalonia;
using LanShare.Core;
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

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanShare",
            "logs"
        );
        DiagLog.Init(logDir);
        if (DiagLog.LogDirectory is null)
            DiagLog.Init(Path.Combine(Path.GetTempPath(), "LanShare-logs")); // fallback
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagLog.Error("Unhandled exception: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagLog.Warn("Unobserved task exception: " + e.Exception.GetBaseException().Message);
            e.SetObserved();
        };
        DiagLog.Info(
            $"LanShare starting · {Environment.OSVersion} · {BuildInfo.Stamp} · {args.Length} args"
        );

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
