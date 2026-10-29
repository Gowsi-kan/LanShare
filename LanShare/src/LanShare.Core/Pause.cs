namespace LanShare.Core;

public interface IPausable
{
    void Pause();
    void Resume();
}

/// <summary>Async gate: waiters block while paused.</summary>
public sealed class PauseGate
{
    readonly object _lock = new();
    TaskCompletionSource? _tcs;

    public bool IsPaused
    {
        get
        {
            lock (_lock)
                return _tcs is not null;
        }
    }

    public void Set(bool paused)
    {
        lock (_lock)
        {
            if (paused)
                _tcs ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            else
            {
                var t = _tcs;
                _tcs = null;
                t?.TrySetResult();
            }
        }
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task? t;
        lock (_lock)
            t = _tcs?.Task;
        return t is null ? Task.CompletedTask : t.WaitAsync(ct);
    }
}
