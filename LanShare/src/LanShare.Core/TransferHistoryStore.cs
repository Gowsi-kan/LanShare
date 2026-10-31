using System.Text.Json;

namespace LanShare.Core;

public sealed record TransferHistoryRecord(
    Guid Id,
    DateTimeOffset CompletedAt,
    string Direction,
    string Peer,
    string Summary,
    long TotalBytes,
    string Outcome,
    string? Error,
    string? Location
);

public sealed class TransferHistoryStore(string path)
{
    const int MaxEntries = 500;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    readonly object _gate = new();
    List<TransferHistoryRecord> _entries = [];
    bool _loaded;

    public TransferHistoryStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LanShare",
                "transfer-history.json"
            )
        ) { }

    public IReadOnlyList<TransferHistoryRecord> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries.ToArray();
        }
    }

    public bool Add(TransferHistoryRecord record, out string? error)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _entries.Insert(0, record);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            try
            {
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, path, overwrite: true);
                error = null;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _entries.Remove(record);
                error = e.Message;
                DiagLog.Warn($"Could not save transfer history: {e.Message}");
                return false;
            }
        }
    }

    void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (File.Exists(path))
                _entries = JsonSerializer.Deserialize<List<TransferHistoryRecord>>(
                        File.ReadAllText(path)
                    ) ?? [];
            _entries = _entries
                .OrderByDescending(entry => entry.CompletedAt)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
            DiagLog.Warn($"Could not load transfer history: {e.Message}");
        }
    }
}
