using Microsoft.Win32;

namespace LanShare.Services;

public static class AutoStart
{
    const string Name = "LanShare";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Path of the published executable, or null when running through `dotnet run`.</summary>
    public static string? ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            if (string.IsNullOrEmpty(p))
                return null;
            return Path.GetFileNameWithoutExtension(p)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? null
                : p;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(Name) is string;
            }
            if (OperatingSystem.IsLinux())
                return File.Exists(LinuxFile());
        }
        catch { }
        return false;
    }

    /// <returns>null on success, otherwise an error message.</returns>
    public static string? Set(bool enable)
    {
        var exe = ExePath;
        if (enable && exe is null)
            return "Start with system only works for the published app (not 'dotnet run').";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (enable)
                    k.SetValue(Name, $"\"{exe}\" --background");
                else
                    k.DeleteValue(Name, false);
                return null;
            }
            if (OperatingSystem.IsLinux())
            {
                var file = LinuxFile();
                if (enable)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(
                        file,
                        "[Desktop Entry]\nType=Application\nName=LanShare\nComment=LAN file transfer\n"
                            + $"Exec=\"{exe}\" --background\nIcon=lanshare\nTerminal=false\nX-GNOME-Autostart-enabled=true\n"
                    );
                }
                else if (File.Exists(file))
                    File.Delete(file);
                return null;
            }
            return "Start with system is not supported on this OS.";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    static string LinuxFile()
    {
        var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );
        return Path.Combine(cfg, "autostart", "lanshare.desktop");
    }
}
