namespace LanShare.Core;

/// <summary>Lock-free live state shared between transfer workers (writers) and the UI (reader).</summary>
public sealed class TransferState(long totalBytes, int totalFiles)
{
    long _done,
        _link,
        _sentRaw,
        _sentWire;
    int _phase,
        _filesDone,
        _streams,
        _retries;
    string? _error;
    string _file = "";

    public long TotalBytes { get; } = totalBytes;
    public int TotalFiles { get; } = totalFiles;
    public long DoneBytes => Interlocked.Read(ref _done);
    public long SentRawBytes => Interlocked.Read(ref _sentRaw);
    public long SentWireBytes => Interlocked.Read(ref _sentWire);
    public int FilesDone => Volatile.Read(ref _filesDone);
    public int ActiveStreams => Volatile.Read(ref _streams);
    public int Retries => Volatile.Read(ref _retries);

    public long LinkBps
    {
        get => Interlocked.Read(ref _link);
        set => Interlocked.Exchange(ref _link, value);
    }
    public Phase Phase
    {
        get => (Phase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }
    public string? Error
    {
        get => Volatile.Read(ref _error);
        set => Volatile.Write(ref _error, value);
    }
    public string CurrentFile
    {
        get => Volatile.Read(ref _file);
        set => Volatile.Write(ref _file, value);
    }

    public void AddBytes(long n) => Interlocked.Add(ref _done, n);

    public void AddSent(long raw, long wire)
    {
        Interlocked.Add(ref _sentRaw, raw);
        Interlocked.Add(ref _sentWire, wire);
    }

    public void FileDone() => Interlocked.Increment(ref _filesDone);

    public void StreamUp() => Interlocked.Increment(ref _streams);

    public void StreamDown() => Interlocked.Decrement(ref _streams);

    public void Retry() => Interlocked.Increment(ref _retries);
}
