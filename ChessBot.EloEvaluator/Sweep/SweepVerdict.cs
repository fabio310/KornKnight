namespace ChessBot.EloEvaluator.Sweep;

/// <summary>
/// Turns the ladder's two edges — the highest level beaten and the lowest level not
/// beaten — into the reported strength estimate.
///
/// Kept as a pure function separate from <see cref="SweepRunner"/> because the
/// interesting cases (a bracket that never closed, or rounds that contradict each
/// other) are awkward to provoke with real games but trivial to verify directly.
/// </summary>
public static class SweepVerdict
{
    /// <param name="highestPassed">Highest opponent Elo ChessBot beat, if any.</param>
    /// <param name="lowestFailed">Lowest opponent Elo ChessBot did not beat, if any.</param>
    /// <param name="gamesPerRound">Used only to explain a contradictory result.</param>
    /// <param name="atEngineFloor">True when the lower bound is the engine's own minimum.</param>
    public static (int? EstimatedElo, int? BracketWidth, string Verdict) Build(
        int? highestPassed, int? lowestFailed, int gamesPerRound, bool atEngineFloor)
    {
        if (highestPassed is int lo && lowestFailed is int hi)
        {
            int estimate = (lo + hi) / 2;

            if (lo < hi)
            {
                // Clean bracket: every level at or below `lo` was beaten, every level at or
                // above `hi` was not, so the answer lies between them.
                return (estimate, hi - lo,
                    $"ChessBot's estimated strength ≈ Stockfish Elo {estimate} (UCI_Elo), " +
                    $"bracketed between {lo} (beaten) and {hi} (not beaten) — ±{(hi - lo) / 2} Elo.");
            }

            // The ladder both beat and failed to beat the same level, or beat a level above
            // one it failed. The rounds disagree, so there is no real bracket and no
            // precision to quote — a sample-size problem, not a strength one.
            return (estimate, null,
                $"ChessBot's estimated strength ≈ Stockfish Elo {estimate} (UCI_Elo), " +
                $"but the rounds contradict each other: Elo {hi} was not beaten while " +
                $"Elo {lo} was. With {gamesPerRound} games per round that is within sampling " +
                $"noise — re-run with more games per round for a bracket that means something.");
        }

        if (highestPassed is int onlyPassed)
            return (null, null,
                $"ChessBot beat every level tested, up to Stockfish Elo {onlyPassed} (UCI_Elo). " +
                $"Its strength is at least that — raise --max-elo to bracket it.");

        if (lowestFailed is int onlyFailed)
            return (null, null,
                $"ChessBot did not beat any level tested, down to Stockfish Elo {onlyFailed} " +
                $"(UCI_Elo). Its strength is at or below that — " +
                $"{(atEngineFloor ? "that is already the engine's floor" : "lower --start-elo to bracket it")}.");

        return (null, null, "No rounds were played.");
    }
}
