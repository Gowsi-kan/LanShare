using System.Security.Cryptography;
using System.Text;

namespace LanShare.Tests;

static class TestData
{
    public static void WriteFile(string path, long size, int seed, bool compressible = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rnd = new Random(seed);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buf = new byte[1 << 20];
        for (long left = size; left > 0;)
        {
            int n = (int)Math.Min(buf.Length, left);
            if (compressible) FillText(rnd, buf.AsSpan(0, n));
            else rnd.NextBytes(buf.AsSpan(0, n));
            fs.Write(buf, 0, n);
            left -= n;
        }
    }

    static void FillText(Random rnd, Span<byte> dst)
    {
        int pos = 0;
        while (pos < dst.Length)
        {
            var line = Encoding.ASCII.GetBytes(
                $"2026-10-30 12:{rnd.Next(60):00}:{rnd.Next(60):00} INFO worker-{rnd.Next(16)} request id={rnd.Next(1_000_000)} status=200\n");
            int n = Math.Min(line.Length, dst.Length - pos);
            line.AsSpan(0, n).CopyTo(dst[pos..]);
            pos += n;
        }
    }

    /// <summary>SHA-256 on purpose: independent of the XxHash3 used inside the product.</summary>
    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}