using System.Text.Json;

namespace LanShare.Services;

public sealed class AppSettings
{
    public bool RunInBackground { get; set; } = true;
    public bool Notifications { get; set; } = true;

    static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanShare",
            "settings.json"
        );

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
            );
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}
