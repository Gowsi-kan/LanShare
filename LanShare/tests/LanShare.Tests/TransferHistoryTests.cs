using LanShare.Core;

namespace LanShare.Tests;

public sealed class TransferHistoryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "lanshare-history-" + Guid.NewGuid().ToString("N"));
    string PathName => Path.Combine(_root, "history.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void History_RoundTripsNewestFirst()
    {
        var store = new TransferHistoryStore(PathName);
        var first = NewRecord(DateTimeOffset.UtcNow.AddMinutes(-1), "first");
        var second = NewRecord(DateTimeOffset.UtcNow, "second");

        Assert.True(store.Add(first, out var firstError), firstError);
        Assert.True(store.Add(second, out var secondError), secondError);

        var loaded = new TransferHistoryStore(PathName).Load();
        Assert.Equal([second, first], loaded);
    }

    [Fact]
    public void History_IsBoundedToFiveHundredEntries()
    {
        var store = new TransferHistoryStore(PathName);
        var records = Enumerable.Range(0, 501)
            .Select(i => NewRecord(DateTimeOffset.UtcNow.AddSeconds(i), $"record-{i}"))
            .ToArray();

        foreach (var record in records)
            Assert.True(store.Add(record, out var error), error);

        var loaded = new TransferHistoryStore(PathName).Load();
        Assert.Equal(500, loaded.Count);
        Assert.DoesNotContain(loaded, record => record.Id == records[0].Id);
        Assert.Equal(records[^1], loaded[0]);
    }

    [Fact]
    public void HistoryCsv_EscapesFieldsAndNeutralizesSpreadsheetFormulas()
    {
        var record = NewRecord(DateTimeOffset.Parse("2026-11-01T00:00:00+00:00"), "one, \"two\"\nthree")
            with
            {
                Peer = "=HYPERLINK(\"https://example.invalid\")",
                Error = "  @SUM(1,1)",
                Location = "/tmp/received file",
            };
        using var output = new StringWriter();

        TransferHistoryStore.WriteCsv(output, [record]);

        var csv = output.ToString();
        Assert.StartsWith(
            "id,completed_at_utc,direction,peer,summary,total_bytes,outcome,error,location",
            csv
        );
        Assert.Contains("\"'=HYPERLINK(\"\"https://example.invalid\"\")\"", csv);
        Assert.Contains("\"one, \"\"two\"\"\nthree\"", csv);
        Assert.Contains("\"'  @SUM(1,1)\"", csv);
        Assert.Contains("\"/tmp/received file\"", csv);
    }

    static TransferHistoryRecord NewRecord(DateTimeOffset completedAt, string summary) =>
        new(Guid.NewGuid(), completedAt, "Sent", "peer", summary, 42, "Completed", null, null);
}
