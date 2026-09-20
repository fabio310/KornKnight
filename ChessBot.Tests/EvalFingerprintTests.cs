using Xunit;
using Xunit.Abstractions;
using ChessBot.Engine;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// A fingerprint of the whole evaluation: both paths (the full-scan <c>Evaluate</c> and the
/// incremental <c>EvaluateFast</c>) over 10,000 positions, folded into one hash.
///
/// It exists for refactors that must not change a single score — moving the weights out of
/// compile-time constants into a tunable vector is the first — and it is how "bit-identical" is
/// checked rather than asserted. The positions come from seeded random playouts, so they cover
/// what a curated list would not: lopsided material, several queens, bare kings, promotions just
/// made, every phase from full to empty.
///
/// When an evaluation change is MEANT to change scores, the expected hash changes with it, in the
/// same commit, and the commit says so.
/// </summary>
[Collection(SerialCollection.Name)]   // must not run while another test swaps the active weights
public class EvalFingerprintTests
{
    private readonly ITestOutputHelper _out;
    public EvalFingerprintTests(ITestOutputHelper output) => _out = output;

    internal const int PositionCount = 10_000;

    /// <summary>
    /// The fingerprint of the evaluation as of P2.3 (king pawn shelter and storm), the first
    /// change that was MEANT to move it. It was 0xCD3AFFE5FD01C12B from v1.0.0 through the whole
    /// of phase 1, including the move of every weight into a tunable vector and the pawn hash
    /// table, which is what those two commits claimed and this number proved.
    /// </summary>
    internal const ulong RecordedHash = 0x017A51835A3C6545UL;

    /// <summary>
    /// Seeded random playouts, 80 plies each or until the game ends, every position kept, until
    /// there are enough. Deterministic: the same seed walks the same games.
    /// </summary>
    internal static IEnumerable<ChessEngine> Positions()
    {
        int produced = 0;
        for (int seed = 1; produced < PositionCount; seed++)
        {
            var engine = new ChessEngine(ChessEngine.MinHashSizeMb);
            var random = new Random(seed);

            for (int ply = 0; ply < 80 && produced < PositionCount; ply++)
            {
                var legal = engine.GetLegalMoves();
                if (legal.Count == 0) break;

                engine.MakeMove(legal[random.Next(legal.Count)]);
                produced++;
                yield return engine;
            }
        }
    }

    internal static ulong Fingerprint(out int positions, out int disagreements)
    {
        var evaluator = new Evaluator();
        ulong hash = 14695981039346656037UL;   // FNV-1a
        positions = 0;
        disagreements = 0;

        foreach (var engine in Positions())
        {
            var board = engine.GetBoardSnapshot();
            int full = evaluator.Evaluate(board);
            int fast = evaluator.EvaluateFast(board);
            if (full != fast) disagreements++;

            foreach (int value in new[] { full, fast })
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
            }
            positions++;
        }

        return hash;
    }

    [Fact]
    public void TheEvaluationIsBitIdenticalToItsRecordedFingerprint()
    {
        ulong hash = Fingerprint(out int positions, out int disagreements);
        _out.WriteLine($"positions {positions}, fingerprint 0x{hash:X16}");

        Assert.Equal(PositionCount, positions);

        // The incremental path must reproduce the full scan exactly, position by position.
        Assert.Equal(0, disagreements);
        Assert.Equal(RecordedHash, hash);
    }
}
