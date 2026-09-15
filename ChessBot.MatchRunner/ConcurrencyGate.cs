namespace ChessBot.MatchRunner;

/// <summary>
/// A semaphore whose size can change while it is in use.
///
/// The run starts one worker per core it could ever use and then lets only <c>Target</c> of them
/// hold a permit at a time. Shrinking does not interrupt anybody: it parks a worker between games,
/// which is the only moment a game's conditions can change without changing the game. Growing
/// hands a permit straight back out.
///
/// Permits are taken away by a background absorber rather than by cancelling a waiter, because a
/// worker that is mid-game must keep its permit until the game ends — which is exactly the
/// property that makes "concurrency changed during the run" safe to report as a range rather than
/// as a caveat about individual games.
/// </summary>
public sealed class ConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _permits;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Permits currently in circulation: held by a worker, or free for one to take.</summary>
    private int _issued;

    /// <summary>Permits promised to the absorber but not yet reclaimed from workers.</summary>
    private int _owed;

    public ConcurrencyGate(int initial, int ceiling)
    {
        Ceiling = Math.Max(1, ceiling);
        _issued = Math.Clamp(initial, 1, Ceiling);
        _permits = new SemaphoreSlim(_issued, Ceiling);
    }

    /// <summary>The most permits this gate can ever have in circulation.</summary>
    public int Ceiling { get; }

    /// <summary>How many permits are in circulation right now.</summary>
    public int Issued { get { lock (_lock) return _issued; } }

    /// <summary>Takes a permit, waiting until one is free.</summary>
    public Task AcquireAsync(CancellationToken ct) => _permits.WaitAsync(ct);

    /// <summary>Gives a permit back. Safe to call from a finally block during shutdown.</summary>
    public void Release()
    {
        try { _permits.Release(); }
        catch (ObjectDisposedException) { /* run is tearing down; nobody is waiting */ }
        catch (SemaphoreFullException) { /* ceiling reached; the extra permit is not needed */ }
    }

    /// <summary>
    /// Moves the number of permits in circulation towards <paramref name="target"/>. Growing takes
    /// effect at once; shrinking takes effect as workers finish the games they are playing.
    /// </summary>
    public void Resize(int target)
    {
        target = Math.Clamp(target, 1, Ceiling);

        lock (_lock)
        {
            int effective = _issued - _owed;
            if (target == effective) return;

            if (target > effective)
            {
                // Cancel outstanding debt first, then issue new permits for whatever is left.
                int growth = target - effective;
                int cancelled = Math.Min(_owed, growth);
                _owed -= cancelled;
                growth -= cancelled;

                for (int i = 0; i < growth; i++)
                {
                    _issued++;
                    Release();
                }
            }
            else
            {
                int shrink = effective - target;
                _owed += shrink;
                for (int i = 0; i < shrink; i++) _ = AbsorbOneAsync();
            }
        }
    }

    /// <summary>
    /// Waits for one permit to come free and keeps it, which is how a permit leaves circulation
    /// without interrupting the worker that currently holds it.
    /// </summary>
    private async Task AbsorbOneAsync()
    {
        try
        {
            await _permits.WaitAsync(_shutdown.Token).ConfigureAwait(false);

            // The debt may have been cancelled while this absorber was waiting — load that came
            // and went inside one game. Cancelling it in the accounting is not enough on its own,
            // because this task was already blocked on the semaphore by then; without the
            // re-check it would retire a permit the run had just been told it could keep.
            bool retire;
            lock (_lock)
            {
                retire = _owed > 0;
                if (retire)
                {
                    _issued--;
                    _owed--;
                }
            }

            if (!retire) Release();
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { if (_owed > 0) _owed--; }
        }
        catch (ObjectDisposedException)
        {
            lock (_lock) { if (_owed > 0) _owed--; }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _permits.Dispose();
    }
}
