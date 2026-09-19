namespace ChessBot.Uci;

using ChessBot.Engine.Search;
using ChessBot.Engine.Types;

/// <summary>
/// Turns a parsed "go" command into <see cref="SearchSettings"/>: decides how much of the
/// clock this move may consume, and which of the protocol's limits become search limits.
///
/// Kept as a pure function of (command, side to move) so the mapping is testable on its own —
/// a time manager that can only be observed through a running search cannot be checked for
/// the case that actually matters, which is the one where it allocates too much.
/// </summary>
public static class UciTimeManager
{
    /// <summary>
    /// Time set aside for everything that is not search: process scheduling, writing the move,
    /// and the GUI's own accounting. The clock keeps running through all of it, so a budget
    /// computed from the raw remaining time is systematically optimistic.
    ///
    /// The right value depends on the host, not on the engine — a local pipe and a network game
    /// against a remote GUI are not the same number — which is why it is a UCI option with this
    /// as its default rather than a constant.
    ///
    /// 10 ms, down from an unmeasured 30. On a local pipe the host's side is small: comparing
    /// scripts/timing-probe.ps1 (the GUI's view, "go" written to "bestmove" read) against the
    /// engine's own "deadline to bestmove written", the difference is under 0.1 ms at p50, and
    /// the worst total tail past the hard deadline seen in 450 loaded-machine samples was
    /// 5.2 ms — a preempted thread. 10 covers that twice over, and is Stockfish's default, so a
    /// tester's conditions written for it carry over. Kept at 30 now that the overhead also
    /// comes off movetime, "go movetime 1000" would answer ~35 ms early, spending 3.5% of every
    /// fixed-time move on nothing. A network host (lichess-bot) reserves its own lag before it
    /// sends the clock, and an operator who knows the host is slower sets this option.
    /// </summary>
    public const int DefaultMoveOverheadMs = 10;

    /// <summary>The largest overhead an operator may set, in milliseconds.</summary>
    public const int MaxMoveOverheadMs = 5000;

    /// <summary>
    /// Assumed number of moves left when the GUI sends no "movestogo" (sudden death, or an
    /// increment control). Dividing the clock by a constant makes the budget decay
    /// geometrically instead of running out at a fixed move number.
    /// </summary>
    public const int DefaultMovesToGo = 30;

    /// <summary>
    /// Fraction of the usable clock a single move may never exceed, whatever the formula
    /// says. This is the forfeit guard: with a small clock and a large increment the
    /// per-move estimate can exceed the time that actually exists.
    /// </summary>
    private const int HardCapDivisor = 2;

    /// <summary>
    /// Stands in for "no time limit" in <see cref="SearchSettings.MaxTimeMs"/>, which treats
    /// null as a 10-second default rather than as unbounded. Needed for "go depth", "go nodes"
    /// and "go infinite", where the clock must not be what ends the search.
    /// </summary>
    public const int NoTimeLimitMs = int.MaxValue;

    /// <summary>
    /// Maps a "go" command to search settings for the given side to move. Depth and node
    /// limits are applied alongside the time limit rather than instead of it, so whichever
    /// bound is reached first ends the search.
    /// </summary>
    public static SearchSettings ToSearchSettings(GoParameters go, Color sideToMove,
                                                  int moveOverheadMs = DefaultMoveOverheadMs)
    {
        var settings = new SearchSettings
        {
            MaxDepth  = go.Depth,
            MaxNodes  = go.Nodes,
            MaxTimeMs = ResolveTimeBudgetMs(go, sideToMove, moveOverheadMs),
            StartTimestamp = go.ReceivedTimestamp,
        };

        return settings;
    }

    /// <summary>
    /// The per-move time budget in milliseconds. "movetime" wins over any clock; "infinite" and
    /// a command with no clock at all are unbounded and rely on "stop".
    ///
    /// "movetime" is an instruction about the GUI's wall clock, and the move overhead comes off
    /// it exactly as it comes off a clock. A GUI that says "movetime 1000" measures 1000 ms from
    /// writing "go" to reading "bestmove", pipe and scheduling included, so honouring the
    /// instruction means finishing inside it — the engine's share is the instruction minus the
    /// part the host spends. Taking it literally is how "go movetime 1000" came back at 1002 ms.
    /// </summary>
    public static int ResolveTimeBudgetMs(GoParameters go, Color sideToMove,
                                          int moveOverheadMs = DefaultMoveOverheadMs)
    {
        if (go.Infinite) return NoTimeLimitMs;
        if (go.MoveTimeMs is int mt)
            return Math.Max(1, mt - Math.Clamp(moveOverheadMs, 0, MaxMoveOverheadMs));
        if (go.HasNoTimeSource)      return NoTimeLimitMs;

        int? remaining = sideToMove == Color.White ? go.WhiteTimeMs : go.BlackTimeMs;
        int increment  = (sideToMove == Color.White ? go.WhiteIncrementMs : go.BlackIncrementMs) ?? 0;

        // Only the opponent's clock was sent — nothing to allocate from, so fall back to the
        // engine's own default rather than inventing a budget from the wrong side's time.
        if (remaining is not int remainingMs) return NoTimeLimitMs;

        return AllocateTimeMs(remainingMs, increment, go.MovesToGo, moveOverheadMs);
    }

    /// <summary>
    /// Allocates one move's share of the clock: an even split of the remaining time over the
    /// moves still to play, plus the increment that this move earns back, and never more than
    /// a fixed fraction of what is actually left.
    /// </summary>
    public static int AllocateTimeMs(int remainingMs, int incrementMs, int? movesToGo,
                                     int moveOverheadMs = DefaultMoveOverheadMs)
    {
        int usableMs = Math.Max(0, remainingMs - Math.Clamp(moveOverheadMs, 0, MaxMoveOverheadMs));
        if (usableMs <= 0) return 1;   // Out of time: move immediately rather than not at all.

        int movesLeft = Math.Max(1, movesToGo ?? DefaultMovesToGo);
        int inc       = Math.Max(0, incrementMs);

        // The increment is added in full because it is refunded on completion of this move;
        // the hard cap below is what stops that from overdrawing a clock too small to earn it.
        long budget  = (long)(usableMs / movesLeft) + inc;
        long hardCap = Math.Max(1, usableMs / HardCapDivisor);

        return (int)Math.Clamp(budget, 1, hardCap);
    }
}
