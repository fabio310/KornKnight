namespace ChessBot.MatchRunner;

using System.Collections.Concurrent;

/// <summary>
/// A worker that runs one game at a time on a dedicated OS thread pinned to one core.
///
/// Pinning a thread pool thread is not enough for a match: a game is asynchronous — it waits on
/// the opponent process between moves — and every <c>await</c> is free to resume the game on a
/// different thread, taking ChessBot's next search with it. This worker installs itself as the
/// <see cref="SynchronizationContext"/> of its own thread, so every continuation of a game
/// dispatched to it comes back to the same pinned thread and the whole game, searches included,
/// runs on one core.
/// </summary>
public sealed class PinnedWorker : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback callback, object? state)> _queue = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Logical processor this worker asked for, or -1 when it runs unpinned.</summary>
    public int Core { get; }

    /// <summary>
    /// Whether the OS actually granted the pin. Recorded rather than assumed: affinity can be
    /// refused, and a manifest that claims a pinned run when the pin failed is worse than one
    /// that admits an unpinned one.
    /// </summary>
    public bool IsPinned { get; private set; }

    public PinnedWorker(int core, string name, bool raiseThreadPriority)
    {
        Core = core;

        _thread = new Thread(() => Pump(raiseThreadPriority))
        {
            IsBackground = true,
            Name = name,
        };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    private void Pump(bool raiseThreadPriority)
    {
        SetSynchronizationContext(this);

        IDisposable? pin = null;
        try
        {
            pin = MachineTopology.PinCurrentThreadToCore(Core);
            IsPinned = pin is not null;

            if (raiseThreadPriority)
            {
                // AboveNormal rather than Highest: a game thread that outranks the OS's own
                // input and I/O threads can starve the opponent process it is waiting on.
                try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
            }

            _ready.SetResult();

            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }
        finally
        {
            pin?.Dispose();
        }
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _thread) { d(state); return; }

        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        }, null);
        done.Wait();
        if (failure is not null) throw failure;
    }

    /// <summary>
    /// Runs an asynchronous body on this worker's thread and completes when the body does.
    /// Awaits inside the body resume here, so the work never leaves the pinned core.
    /// </summary>
    public Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(async _ =>
        {
            try   { completion.SetResult(await body()); }
            catch (OperationCanceledException oce) { completion.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, null);

        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}

/// <summary>
/// A fixed set of <see cref="PinnedWorker"/>s, one per concurrent game, each on its own core.
/// The pool size <em>is</em> the degree of parallelism: a game runs only once a worker is free,
/// so the number of games in flight is exactly the pool size at all times, rather than whatever
/// the thread pool's hill-climbing has decided to allow this second.
/// </summary>
public sealed class PinnedWorkerPool : IDisposable
{
    private readonly PinnedWorker[] _workers;
    private readonly BlockingCollection<PinnedWorker> _free;

    public PinnedWorkerPool(int size, bool raiseThreadPriority)
    {
        size = Math.Max(1, size);
        int stride = CoreSlotPool.StrideForThisMachine;
        int logical = Math.Max(1, MachineTopology.LogicalProcessorCount);

        _workers = new PinnedWorker[size];
        _free = new BlockingCollection<PinnedWorker>(new ConcurrentBag<PinnedWorker>(), size);

        for (int i = 0; i < size; i++)
        {
            _workers[i] = new PinnedWorker((i * stride) % logical, $"game-worker-{i}", raiseThreadPriority);
            _free.Add(_workers[i]);
        }
    }

    /// <summary>True only when every worker's pin was granted by the OS.</summary>
    public bool AllPinned => _workers.All(w => w.IsPinned);

    /// <summary>The logical processor each worker was pinned to, for the manifest.</summary>
    public IReadOnlyList<int> Cores => _workers.Select(w => w.Core).ToList();

    /// <summary>
    /// Runs one game on the next free worker. The returned task completes when the game does,
    /// and the worker is released for the next game before it completes.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        var worker = _free.Take(ct);
        try   { return await worker.RunAsync(body); }
        finally { _free.Add(worker); }
    }

    public void Dispose()
    {
        foreach (var worker in _workers) worker.Dispose();
        _free.Dispose();
    }
}
