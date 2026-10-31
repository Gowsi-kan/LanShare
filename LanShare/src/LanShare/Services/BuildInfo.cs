namespace LanShare.Services;

public static class BuildInfo
{
    /// <summary>Last-write time of this build's assembly (UTC ticks). A newer build replaces an older running instance.</summary>
    public static readonly long Ticks = Compute();

    public static string Stamp =>
        Ticks == 0
            ? "Build ?"
            : $"Build {new DateTime(Ticks, DateTimeKind.Utc).ToLocalTime():dd MMM HH:mm}";

    static long Compute()
    {
        try
        {
            var loc = typeof(BuildInfo).Assembly.Location;
            var path = !string.IsNullOrEmpty(loc) ? loc : Environment.ProcessPath; // single-file publish has no Location
            return path is null ? 0 : File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return 0;
        }
    }
}
