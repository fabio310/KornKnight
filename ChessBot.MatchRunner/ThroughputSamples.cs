namespace ChessBot.MatchRunner;

/// <summary>
/// The node rate the engines are actually achieving, as they achieve it.
///
/// This is the signal that says whether a run has spare capacity. Adding a game is free exactly
/// when it does not cost the engines nodes per move, and that is a measurement rather than a rule
/// of thumb: it depends on the machine, on what else is running, and on how much of a core a
/// search really uses. A governor that guessed would either leave half the box idle or quietly
/// halve every search.
///
/// Node rate rather than nodes per move, because a timed move's node count is its rate times a
/// fixed clock — the same quantity — while a node-budget move's is a constant and says nothing.
/// The median is used rather than the mean: a single endgame position with a tiny tree is a long
/// way from the middle, and a mean would chase it.
/// </summary>
public sealed class ThroughputSamples
{
    /// <summary>
    /// Kept small enough to refill within seconds — at a 50 ms budget and several games in
    /// flight, moves arrive in the hundreds per second — and large enough that the median is not
    /// moved by the handful of positions that happen to be in an endgame.
    /// </summary>
    public const int Capacity = 512;

    private readonly long[] _samples = new long[Capacity];
    private readonly object _lock = new();
    private int _count;
    private int _next;

    /// <summary>How many samples have arrived since the last <see cref="Clear"/>.</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>
    /// Records one move's node rate. Called from every game thread, so it does the least possible
    /// work under the lock; a rate of zero or less is dropped rather than recorded, because an
    /// engine that reported no rate would otherwise drag the median towards nothing.
    /// </summary>
    public void Add(long nodesPerSecond)
    {
        if (nodesPerSecond <= 0) return;

        lock (_lock)
        {
            _samples[_next] = nodesPerSecond;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Throws the window away, so the next measurement starts after a change rather than across it.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _next = 0;
        }
    }

    /// <summary>The middle node rate in the window, or 0 when nothing has been recorded.</summary>
    public double Median()
    {
        lock (_lock)
        {
            if (_count == 0) return 0;

            var copy = new long[_count];
            Array.Copy(_samples, copy, _count);
            Array.Sort(copy);
            return copy[_count / 2];
        }
    }
}
