namespace ChessBot.Tests;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ChessBot.Engine.Hashing;
using ChessBot.Engine.Types;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The transposition table packs an entry into two 64-bit words and verifies a read by XOR
/// instead of taking a lock. Two things can go wrong with that, and only one of them is loud.
///
/// A packing bug is silent: the search reads a plausible wrong move, or a score off by a power of
/// two, and simply plays worse. A torn read is silent in a different way: it only happens under
/// threads, only sometimes, and the wrong answer it produces is a legal-looking entry for a
/// position that was never searched.
/// </summary>
public class TranspositionTableTests
{
    private readonly ITestOutputHelper _out;
    public TranspositionTableTests(ITestOutputHelper output) => _out = output;

    // ── Packing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every field, at the edges of its range, through pack and unpack. The move carries all four
    /// of its own parts, because a promotion capture is the one that uses every bit.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(255, 31_999, 1, 255)]
    [InlineData(64, -31_999, 2, 128)]
    // The widest scores the search ever stores: a mate adjusted by ply, both signs.
    [InlineData(12, 100_127, 0, 7)]
    [InlineData(12, -100_127, 1, 7)]
    public void EveryFieldSurvivesThePackingRoundTrip(
        int depth, int score, int flagValue, int age)
    {
        var flag = (TranspositionTable.ScoreFlag)flagValue;
        var move = new Move(Square.FromAlgebraic("h7"), Square.FromAlgebraic("g8"),
                            MoveType.Capture | MoveType.Promotion, PieceType.Queen);

        var (d, s, f, m, a) = TranspositionTable.RoundTrip(depth, score, flag, move, (byte)age);

        Assert.Equal(depth, d);
        Assert.Equal(score, s);
        Assert.Equal(flag, f);
        Assert.Equal((byte)age, a);
        Assert.Equal(move, m);
        Assert.Equal(move.From, m.From);
        Assert.Equal(move.To, m.To);
        Assert.Equal(move.MoveType, m.MoveType);
        Assert.Equal(move.PromotionType, m.PromotionType);
    }

    /// <summary>
    /// Both squares, all 64 of each, so a mask that is one bit short shows up rather than working
    /// for the first half of the board.
    /// </summary>
    [Fact]
    public void EverySquarePairSurvivesThePackingRoundTrip()
    {
        for (int from = 0; from < 64; from++)
        {
            for (int to = 0; to < 64; to++)
            {
                var move = new Move(new Square(from), new Square(to));
                var (_, _, _, unpacked, _) = TranspositionTable.RoundTrip(
                    5, 42, TranspositionTable.ScoreFlag.Exact, move, 1);

                Assert.Equal(from, unpacked.From.Index);
                Assert.Equal(to, unpacked.To.Index);
            }
        }
    }

    // ── What a single thread sees ────────────────────────────────────────────

    [Fact]
    public void AStoredEntryIsFoundAndAnUnrelatedKeyIsNot()
    {
        var table = new TranspositionTable(1);
        var move  = new Move(Square.FromAlgebraic("e2"), Square.FromAlgebraic("e4"));

        table.Store(0xDEADBEEFCAFEF00DUL, depth: 7, score: -250,
                    TranspositionTable.ScoreFlag.UpperBound, move);

        var hit = table.Lookup(0xDEADBEEFCAFEF00DUL, depth: 7);
        Assert.NotNull(hit);
        Assert.Equal(-250, hit!.Value.score);
        Assert.Equal(7, hit.Value.depth);
        Assert.Equal(TranspositionTable.ScoreFlag.UpperBound, hit.Value.flag);
        Assert.Equal(move, hit.Value.bestMove);

        Assert.Equal(move, table.LookupBestMoveOnly(0xDEADBEEFCAFEF00DUL));

        // A shallower requirement is satisfied; a deeper one is not.
        Assert.NotNull(table.Lookup(0xDEADBEEFCAFEF00DUL, depth: 3));
        Assert.Null(table.Lookup(0xDEADBEEFCAFEF00DUL, depth: 8));

        // A key that was never stored must miss, even though the table is far from empty.
        Assert.Null(table.Lookup(0x1234567812345678UL, depth: 1));
        Assert.Equal(default, table.LookupBestMoveOnly(0x1234567812345678UL));
    }

    /// <summary>
    /// An empty slot is two zero words, and a real entry must never read as one — which is what
    /// the occupied bit is for. A key whose payload happened to equal it would otherwise vanish.
    /// </summary>
    [Fact]
    public void AnEmptyTableReportsNothingUsed()
    {
        var table = new TranspositionTable(1);

        Assert.Equal(0, table.GetUsedEntries());
        Assert.Equal(0, table.GetFillPermille());

        table.Store(1, 1, 0, TranspositionTable.ScoreFlag.Exact, default);

        Assert.Equal(1, table.GetUsedEntries());
    }

    // ── What several threads see ─────────────────────────────────────────────

    /// <summary>
    /// The acceptance the task asks for. Several threads hammer one small table, each writing
    /// entries whose payload is derived from its key, and every read must either come back with
    /// exactly what some writer wrote for that key, or be rejected.
    ///
    /// The table is deliberately tiny and the key set deliberately large, so slots are fought over
    /// constantly — which is the only way a torn read is reachable at all. What must never happen
    /// is the third outcome: a read that is accepted and wrong.
    /// </summary>
    [Fact]
    public void UnderSeveralThreadsEveryAcceptedReadIsOneThatWasWritten()
    {
        var table = new TranspositionTable(1);
        int threads = Math.Max(4, Environment.ProcessorCount);
        var failures = new ConcurrentBag<string>();

        long accepted = 0, rejected = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        Parallel.For(0, threads, worker =>
        {
            var random = new Random(9001 + worker);
            long localAccepted = 0, localRejected = 0;

            while (!stop.IsCancellationRequested)
            {
                ulong key = (ulong)random.NextInt64(1, 200_000);

                if (random.Next(2) == 0)
                {
                    var (depth, score, flag, move) = PayloadFor(key);
                    table.Store(key, depth, score, flag, move);
                    continue;
                }

                var hit = table.Lookup(key, depth: 1);
                if (hit is null) { localRejected++; continue; }

                localAccepted++;
                var (expectedDepth, expectedScore, expectedFlag, expectedMove) = PayloadFor(key);

                if (hit.Value.depth != expectedDepth || hit.Value.score != expectedScore
                    || hit.Value.flag != expectedFlag || hit.Value.bestMove != expectedMove)
                {
                    failures.Add(
                        $"key {key}: read depth {hit.Value.depth} score {hit.Value.score} " +
                        $"flag {hit.Value.flag} move {hit.Value.bestMove}, but the only payload " +
                        $"ever written for it was depth {expectedDepth} score {expectedScore} " +
                        $"flag {expectedFlag} move {expectedMove}");
                }
            }

            Interlocked.Add(ref accepted, localAccepted);
            Interlocked.Add(ref rejected, localRejected);
        });

        _out.WriteLine($"{threads} threads, {accepted:N0} accepted reads, {rejected:N0} rejected, " +
                       $"{failures.Count} accepted-and-wrong");

        Assert.True(failures.IsEmpty,
                    $"{failures.Count} accepted read(s) returned something no writer ever wrote:" +
                    Environment.NewLine + string.Join(Environment.NewLine, failures));

        // A run that never accepted anything would pass the assertion above and prove nothing.
        Assert.True(accepted > 100_000,
                    $"only {accepted} accepted reads — the table was not exercised");
    }

    /// <summary>
    /// The one payload any thread is allowed to write for a given key, so that "is this what
    /// somebody wrote?" has a single answer. Every field is derived from the key and every field
    /// varies with it, so a torn read that mixed two entries would disagree on at least one.
    /// </summary>
    private static (int depth, int score, TranspositionTable.ScoreFlag flag, Move move)
        PayloadFor(ulong key)
    {
        int depth = (int)(key % 60) + 1;
        int score = (int)(key % 40_000) - 20_000;
        var flag  = (TranspositionTable.ScoreFlag)(key % 3);
        var move  = new Move(new Square((int)(key % 64)), new Square((int)((key / 64) % 64)),
                             MoveType.Quiet, PieceType.None);

        return (depth, score, flag, move);
    }
}
