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
}
