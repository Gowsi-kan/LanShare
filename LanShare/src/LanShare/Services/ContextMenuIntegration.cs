using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LanShare.Services;

/// <summary>"Send with LanShare" in the file manager's right-click menu (per-user, no admin needed).</summary>
public static class ContextMenuIntegration
{
    const string Verb = "LanShare";
    const string Label = "Send with LanShare";

    public static bool IsInstalled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\" + Verb);
                return k is not null;
            }
            if (OperatingSystem.IsLinux())
                return File.Exists(NautilusScript())
                    || File.Exists(NemoAction())
                    || File.Exists(DolphinMenu());
        }
        catch { }
        return false;
    }

    /// <returns>null on success, otherwise an error message.</returns>
    public static string? Set(bool enable)
    {
        var exe = AutoStart.ExePath;
        if (enable && exe is null)
            return "Right-click integration only works for the published app (not 'dotnet run').";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetWindows(enable, exe);
                return null;
            }
            if (OperatingSystem.IsLinux())
            {
                SetLinux(enable, exe);
                return null;
            }
            return "Right-click integration is not supported on this OS.";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    // ---------------------------------------------------------------- Windows (HKCU)

    [SupportedOSPlatform("windows")]
    static void SetWindows(bool enable, string? exe)
    {
        string[] verbs =
        [
            @"Software\Classes\*\shell\" + Verb, // every file
            @"Software\Classes\Directory\shell\" + Verb, // folders
        ];
        var appKey =
            @"Software\Classes\Applications\"
            + (exe is null ? "LanShare.exe" : Path.GetFileName(exe));

        if (!enable)
        {
            foreach (var v in verbs)
                Registry.CurrentUser.DeleteSubKeyTree(v, false);
            Registry.CurrentUser.DeleteSubKeyTree(appKey, false);
            return;
        }

        foreach (var v in verbs)
        {
            using var k = Registry.CurrentUser.CreateSubKey(v);
            k.SetValue("", Label);
            k.SetValue("Icon", $"\"{exe}\"");
            using var cmd = k.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }

        // "Open with" -> LanShare
        using (var app = Registry.CurrentUser.CreateSubKey(appKey))
        {
            app.SetValue("FriendlyAppName", "LanShare");
            using var cmd = app.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
    }

    // ---------------------------------------------------------------- Linux (Files, Nemo, Dolphin, Open With)

    [UnsupportedOSPlatform("windows")]
    static void SetLinux(bool enable, string? exe)
    {
        string[] files =
        [
            NautilusScript(),
            NemoAction(),
            DolphinMenu(),
            DolphinMenuLegacy(),
            OpenWithDesktop(),
        ];
        if (!enable)
        {
            foreach (var f in files)
                if (File.Exists(f))
                    File.Delete(f);
            return;
        }

        Write(NautilusScript(), $"#!/bin/sh\nexec \"{exe}\" \"$@\"\n", executable: true);

        Write(
            NemoAction(),
            $"[Nemo Action]\nName={Label}\nComment=Send the selected files to another device\n"
                + $"Exec=\"{exe}\" %F\nSelection=notnone\nExtensions=any;dir;\nQuote=double\n"
        );

        var dolphin =
            "[Desktop Entry]\nType=Service\nServiceTypes=KonqPopupMenu/Plugin\nMimeType=all/all;inode/directory;\n"
            + "Actions=lanshareSend\nX-KDE-Priority=TopLevel\n\n"
            + $"[Desktop Action lanshareSend]\nName={Label}\nIcon=document-send\nExec=\"{exe}\" %F\n";
        Write(DolphinMenu(), dolphin, executable: true);
        Write(DolphinMenuLegacy(), dolphin, executable: true);

        Write(
            OpenWithDesktop(),
            "[Desktop Entry]\nType=Application\nName=LanShare (send)\nComment=Send files to another device\n"
                + $"Exec=\"{exe}\" %F\nIcon=lanshare\nTerminal=false\nCategories=Network;FileTransfer;\n"
                + "MimeType=inode/directory;application/octet-stream;application/pdf;application/zip;text/plain;"
                + "image/png;image/jpeg;video/mp4;audio/mpeg;\n"
        );
    }

    [UnsupportedOSPlatform("windows")]
    static void Write(string path, string content, bool executable = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (executable)
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute
            );
    }

    static string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
            ? d
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share"
            );

    static string NautilusScript() => Path.Combine(DataHome, "nautilus", "scripts", Label);

    static string NemoAction() =>
        Path.Combine(DataHome, "nemo", "actions", "lanshare-send.nemo_action");

    static string DolphinMenu() =>
        Path.Combine(DataHome, "kio", "servicemenus", "lanshare-send.desktop");

    static string DolphinMenuLegacy() =>
        Path.Combine(DataHome, "kservices5", "ServiceMenus", "lanshare-send.desktop");

    static string OpenWithDesktop() =>
        Path.Combine(DataHome, "applications", "lanshare-send.desktop");
}
