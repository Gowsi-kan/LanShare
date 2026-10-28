namespace LanShare.Core;

public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long value)
    {
        double d = value;
        int i = 0;
        while (d >= 1024 && i < Units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return i == 0 ? $"{value} B" : $"{d:0.0} {Units[i]}";
    }

    public static string Rate(double bytesPerSec) => $"{Bytes((long)bytesPerSec)}/s";

    public static string LinkRate(long bps) =>
        bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0} Mbps";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s"
        : $"{Math.Max(0, (int)Math.Ceiling(t.TotalSeconds))}s";
}
