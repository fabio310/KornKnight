using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Uci;

namespace ChessBot.Tests;

/// <summary>
/// The command shapes lichess-bot actually sends. It restates the whole game on every turn —
/// "position startpos moves ..." growing by two moves a turn — and sends a clock with an
/// increment and no movestogo. Both are ordinary UCI, and both have a way of going wrong that
/// only shows in a long game or on a short clock.
/// </summary>
[Collection(SerialCollection.Name)]   // asserts wall-clock bounds
public class LichessBotReadinessTests
{
    private readonly ITestOutputHelper _out;
    public LichessBotReadinessTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A long legal game in UCI notation, from seeded random moves so the test is deterministic.
    /// Random play almost never mates early, so a game of the requested length is found within a
    /// few seeds; the moves only have to be legal, not good.
    /// </summary>
    private static List<string> LongGame(int plies)
    {
        for (int seed = 1; ; seed++)
        {
            var engine = new ChessEngine();
            var random = new Random(seed);
            var moves  = new List<string>();

            while (moves.Count < plies)
            {
                var legal = engine.GetLegalMoves();
                if (legal.Count == 0) break;

                Move move = legal[random.Next(legal.Count)];
                moves.Add(UciMoveNotation.Format(move));
                engine.MakeMove(move);
            }

            if (moves.Count == plies) return moves;
        }
    }

    private static double Time(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// The replay of "position startpos moves ..." is linear in the length of the game — one
    /// legal-move generation and one make per move — and at the length of a long game it is a
    /// fraction of a millisecond, so restating the game every turn cannot eat the clock at move
    /// 80. It is not on the engine's own clock (the budget runs from "go"), which is exactly why
    /// it has to be small: on lichess the server's clock is already running.
    /// </summary>
    [Fact]
    public void RestatingTheWholeGameEveryTurn_IsCheap()
    {
        var game = LongGame(400);

        var engine = new ChessEngine();
        using var session = new UciSession(engine, TextWriter.Null);

        string Command(int n) => n == 0 ? "position startpos" : "position startpos moves " + string.Join(' ', game.Take(n));

        // Warm up the replay path so the first measurement is not the JIT's.
        for (int i = 0; i < 20; i++) session.Execute(Command(40));

        var inv    = System.Globalization.CultureInfo.InvariantCulture;
        var report = new StringBuilder("plies  replay ms (best of 5)\n");
        double at160 = 0, at400 = 0;
        foreach (int n in new[] { 20, 80, 160, 240, 400 })
        {
            double best = Enumerable.Range(0, 5).Min(_ => Time(() => session.Execute(Command(n))));
            report.AppendLine(string.Format(inv, "{0,5}  {1,8:F3}", n, best));
            if (n == 160) at160 = best;
            if (n == 400) at400 = best;
        }

        // The whole game as lichess-bot sends it to one side: every other prefix, 200 commands.
        double wholeGame = 0;
        for (int n = 0; n <= 400; n += 2) wholeGame += Time(() => session.Execute(Command(n)));
        report.AppendLine(string.Format(inv, "every turn of a 400-ply game, one side: {0:F1} ms total", wholeGame));
        _out.WriteLine(report.ToString());

        // And it built the real position, not merely a fast wrong one.
        var direct = new ChessEngine();
        foreach (string token in game)
        {
            Assert.True(UciMoveNotation.TryParse(token, direct.GetLegalMoves(), out Move m));
            direct.MakeMove(m);
        }
        Assert.Equal(direct.ExportFen(), engine.ExportFen());

        // Generous bounds — a loaded CI machine must not fail this — that still catch a
        // quadratic replay, which at 400 plies would be two orders of magnitude slower.
        Assert.True(at160 < 10, $"replaying 160 plies took {at160:F2} ms");
        Assert.True(at400 < 25, $"replaying 400 plies took {at400:F2} ms");
    }

    /// <summary>
    /// Stamps the instant the bestmove line is written, so a test can time a search to the
    /// microsecond. Polling a buffer with Thread.Sleep would add up to a timer tick — 15.6 ms by
    /// default on Windows — to a measurement whose whole budget is 40.
    /// </summary>
    private sealed class BestMoveClock : TextWriter
    {
        private readonly ManualResetEventSlim _written = new();
        public long WrittenAt { get; private set; }
        public string? BestMove { get; private set; }
        public int? AnnouncedBudgetMs { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (value is null) return;
            if (value.StartsWith("info string budget ", StringComparison.Ordinal))
                AnnouncedBudgetMs = int.Parse(value["info string budget ".Length..]);
            if (value.StartsWith("bestmove", StringComparison.Ordinal))
            {
                WrittenAt = Stopwatch.GetTimestamp();
                BestMove  = value;
                _written.Set();
            }
        }

        public void Reset() { _written.Reset(); BestMove = null; AnnouncedBudgetMs = null; }
        public bool Wait(int timeoutMs) => _written.Wait(timeoutMs);
    }

    /// <summary>
    /// The last seconds of a lost-on-time race: 40 ms on the clock and no increment. After the
    /// move overhead there is nothing to allocate, so the budget is the 1 ms floor — and the
    /// question is whether anything in the chain between "go" and "bestmove" (stopping the
    /// previous search, the thread-pool hop, the deadline check's granularity, the unwinding)
    /// still carries the move past the flag. Twenty searches per position, each timed from just
    /// before "go" is handed to the session to the instant bestmove is written.
    /// </summary>
    [Theory]
    [InlineData("go wtime 40 btime 40 winc 0 binc 0")]
    [InlineData("go wtime 40 btime 40 winc 1000 binc 1000")]   // increment cannot overdraw the clock
    [InlineData("go wtime 100 btime 100")]
    public void AnAlmostEmptyClock_StillAnswersInsideIt(string go)
    {
        int clockMs = int.Parse(go.Split(' ')[2]);
        var positions = new[]
        {
            "position startpos",
            "position fen r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "position fen r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1",
        };

        var clock  = new BestMoveClock();
        var engine = new ChessEngine();
        using var session = new UciSession(engine, clock);

        var worst = 0.0;
        foreach (string position in positions)
        {
            for (int i = 0; i < 20; i++)
            {
                session.Execute(position);
                clock.Reset();

                long start = Stopwatch.GetTimestamp();
                session.Execute(go);
                Assert.True(clock.Wait(5_000), $"no bestmove for '{go}'");

                double ms = (clock.WrittenAt - start) * 1000.0 / Stopwatch.Frequency;
                worst = Math.Max(worst, ms);
                Assert.True(ms < clockMs, $"'{go}' in '{position}' answered at {ms:F2} ms");
                Assert.True(UciMoveNotation.TryParse(clock.BestMove!.Split(' ')[1], engine.GetLegalMoves(), out _));
            }
        }

        _out.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}: budget {1} ms, worst of 60 answered at {2:F2} ms", go, clock.AnnouncedBudgetMs, worst));
    }

    /// <summary>
    /// lichess-bot never sends movestogo, so every move of every game goes through the
    /// default-horizon branch: remaining/30 plus the increment, capped at half the usable clock.
    /// At 3+2 that is an 8 s first move that the increment mostly refunds, and a clock that
    /// decays by a thirtieth a move rather than running out at a fixed move number.
    /// </summary>
    [Fact]
    public void ALichessBotClock_UsesTheDefaultHorizon()
    {
        var go = GoParameters.Parse("go wtime 180000 btime 180000 winc 2000 binc 2000".Split(' '), 1);
        int budget = UciTimeManager.ResolveTimeBudgetMs(go, Color.White);

        Assert.Null(go.MovesToGo);
        Assert.Equal((180_000 - UciTimeManager.DefaultMoveOverheadMs) / UciTimeManager.DefaultMovesToGo + 2_000,
                     budget);

        // Simulate the whole game on that rule, spending exactly the budget every move: the
        // clock must never run out, and must never be spent faster than it is earned back plus
        // a thirtieth of what is left.
        long remaining = 180_000;
        for (int move = 1; move <= 200; move++)
        {
            int spend = UciTimeManager.AllocateTimeMs((int)remaining, 2_000, null);
            Assert.True(spend < remaining, $"move {move}: budget {spend} ms with {remaining} ms left");
            remaining = remaining - spend + 2_000;
        }

        Assert.True(remaining > 2_000, $"after 200 moves only {remaining} ms remain");
    }
}
