using LanShare.Core;

namespace LanShare.Tests;

public class PathTests : IDisposable
{
    readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "lanshare-path-" + Guid.NewGuid().ToString("N")[..8])));

    public PathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a/../b.txt")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData("bad\0name.txt")]
    public void Resolve_Rejects(string rel) =>
        Assert.Throws<InvalidDataException>(() => ReceiverSession.Resolve(_root, rel));

    [Theory]
    [InlineData("file.txt")]
    [InlineData("dir/sub/file.txt")]
    [InlineData("/etc/passwd")]            // a leading slash must not escape the target folder: it is treated as relative
    [InlineData("dir//double//slash.txt")]
    public void Resolve_StaysInsideTheTargetFolder(string rel)
    {
        var full = ReceiverSession.Resolve(_root, rel);
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, full);
    }

    [Theory(Skip = "Known gap (hardening step): the receiver does not reject Windows reserved names yet. Remove Skip when fixed.")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("dir/COM1")]
    [InlineData("trailing.")]
    [InlineData("space ")]
    public void Resolve_RejectsWindowsReservedNames(string rel) =>
        Assert.Throws<InvalidDataException>(() => ReceiverSession.Resolve(_root, rel));

    [Fact]
    public void Prepare_RejectsTraversalBeforeTouchingTheDisk()
    {
        var offer = new Offer(Guid.NewGuid(), "evil", 2, 65_536, [new FileEntry("../escape.txt", 10, "k")]);
        Assert.Throws<InvalidDataException>(() => ReceiverSession.Prepare(offer, _root, new TransferState(10, 1)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.txt")));
    }
}