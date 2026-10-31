using System.Diagnostics;

namespace LanShare.Core;

public enum Stage
{
    SourceRead,
    Hash,
    Compress,
    Send,
    AckWait,
    NetReceive,
    Decompress,
    Disk,
}

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
    int _pauseLocal,
        _pausePeer;

    string _linkName = "";
    public string LinkName
    {
        get => Volatile.Read(ref _linkName);
        set => Volatile.Write(ref _linkName, value);
    }

    readonly long[] _stageTicks = new long[8];

    /// <summary>Adds the time since <paramref name="startTimestamp"/> (Stopwatch.GetTimestamp()) to a stage.</summary>
    public void AddStage(Stage stage, long startTimestamp) =>
        Interlocked.Add(ref _stageTicks[(int)stage], Stopwatch.GetTimestamp() - startTimestamp);

    public double StageSeconds(Stage stage) =>
        Interlocked.Read(ref _stageTicks[(int)stage]) / (double)Stopwatch.Frequency;

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

    public bool PausedLocal
    {
        get => Volatile.Read(ref _pauseLocal) != 0;
        set => Volatile.Write(ref _pauseLocal, value ? 1 : 0);
    }
    public bool PausedByPeer
    {
        get => Volatile.Read(ref _pausePeer) != 0;
        set => Volatile.Write(ref _pausePeer, value ? 1 : 0);
    }
    public bool IsPaused => PausedLocal || PausedByPeer;

    /// <summary>Syncs Phase with the pause flags, but only while a transfer is running (never overwrites Completed/Failed).</summary>
    public void ApplyPause()
    {
        while (true)
        {
            int cur = Volatile.Read(ref _phase);
            if ((Phase)cur is not (Phase.Transferring or Phase.Paused))
                return;
            int want = (int)(IsPaused ? Phase.Paused : Phase.Transferring);
            if (want == cur || Interlocked.CompareExchange(ref _phase, want, cur) == cur)
                return;
        }
    }

    public void SetDone(long n) => Interlocked.Exchange(ref _done, n);

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
