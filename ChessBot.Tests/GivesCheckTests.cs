namespace ChessBot.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using ChessBot.Engine.Board;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// <see cref="MoveGenerator.GivesCheck"/> answers, without touching the board, the question the
/// pruning rules used to answer with MakeMove + IsInCheck + UndoMove. The only acceptance that
/// means anything for such a predicate is exhaustive equivalence with the thing it replaces: a
/// rule that prunes a checking move it failed to recognise loses mates, and one that reports a
/// check where there is none simply stops pruning.
/// </summary>
public class GivesCheckTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public GivesCheckTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Every legal move of several hundred positions, judged both ways. The positions are the
    /// generated opening suite walked forward under a fixed pseudo-random line, so they are real
    /// positions of every phase rather than hand-picked ones, and the same set every run.
    /// </summary>
    [Fact]
    public void AgreesWithMakeUnmakeOnEveryLegalMoveOfSeveralHundredPositions()
    {
        var positions = Positions(count: 400, seed: 20260915);
        Assert.InRange(positions.Count, 400, int.MaxValue);

        int movesJudged = 0, checksFound = 0;
        var disagreements = new List<string>();

        foreach (string fen in positions)
        {
            var board = new Board();
            board.LoadFromFen(fen);

            var generator = new MoveGenerator(board);
            var detector  = new CheckDetector(board);

            var buffer = new Move[MoveGenerator.MaxMoves];
            generator.GenerateLegalMovesInto(buffer, out int count);

            for (int i = 0; i < count; i++)
            {
                Move move = buffer[i];
                Color them = board.State.ActiveColor.Opposite();

                bool predicted = generator.GivesCheck(move);

                board.MakeMove(move);
                bool actual = detector.IsInCheck(them);
                board.UndoMove();

                movesJudged++;
                if (actual) checksFound++;

                if (predicted != actual && disagreements.Count < 20)
                    disagreements.Add($"{fen}  {move}  predicted {predicted}, actually {actual}");
            }
        }

        Assert.True(disagreements.Count == 0,
                    $"{disagreements.Count} disagreement(s) out of {movesJudged} moves:" +
                    Environment.NewLine + string.Join(Environment.NewLine, disagreements));

        _out.WriteLine($"{positions.Count} positions, {movesJudged:N0} legal moves judged, " +
                       $"{checksFound:N0} of them checks, 0 disagreements");

        // A run in which nothing ever gives check would agree perfectly and prove nothing.
        Assert.True(checksFound > 500,
                    $"only {checksFound} checking moves in {movesJudged} — the corpus is too quiet to test with");
    }

    /// <summary>
    /// The three special moves, each in a position where it is the only way the check can arrive.
    /// They are rare enough in the corpus above to be worth naming: castling checks with the rook
    /// and not the king, en passant empties two squares rather than one, and a promotion checks as
    /// the piece it became.
    /// </summary>
    [Theory]
    // Rook lands on f1 and checks a king on f8 down the open file; the king itself checks nothing.
    [InlineData("5k2/8/8/8/8/8/8/4K2R w K - 0 1",            "e1g1", true)]
    // The same castle with the king on a8: the rook on f1 reaches nothing, and the king's own
    // arrival on g1 is not a check either.
    [InlineData("k7/8/8/8/8/8/8/4K2R w K - 0 1",             "e1g1", false)]
    // Queen-side: the rook lands on d1 and checks a king on d8.
    [InlineData("3k4/8/8/8/8/8/8/R3K3 w Q - 0 1",            "e1c1", true)]
    // En passant empties two squares, and it is the captured pawn's — not the mover's — that was
    // holding the rank shut between a rook on a5 and the king on g5.
    [InlineData("8/8/8/R1pP2k1/8/8/8/K7 w - c6 0 2",         "d5c6", true)]
    // A promotion checks as the piece it became: a knight on f8 reaches h7, where a queen cannot.
    [InlineData("5b2/4P2k/8/8/8/8/8/4K3 w - - 0 1",          "e7f8n", true)]
    [InlineData("5b2/4P2k/8/8/8/8/8/4K3 w - - 0 1",          "e7f8q", false)]
    public void JudgesTheSpecialMoves(string fen, string uci, bool expected)
    {
        var board = new Board();
        board.LoadFromFen(fen);

        var generator = new MoveGenerator(board);
        var buffer = new Move[MoveGenerator.MaxMoves];
        generator.GenerateLegalMovesInto(buffer, out int count);

        Move move = default;
        for (int i = 0; i < count; i++)
            if (Uci(buffer[i]) == uci) { move = buffer[i]; break; }

        Assert.True(move != default, $"{uci} is not legal in {fen}");
        Assert.Equal(expected, generator.GivesCheck(move));
    }

    private static string Uci(Move move)
    {
        string text = $"{move.From.FileLetter}{move.From.Rank + 1}{move.To.FileLetter}{move.To.Rank + 1}";
        return (move.MoveType & MoveType.Promotion) != 0
            ? text + char.ToLowerInvariant(new Piece(Color.White, move.PromotionType).ToFenChar())
            : text;
    }

    /// <summary>
    /// Positions from the generated opening suite, each walked forward a deterministic number of
    /// random legal plies so the corpus spans opening, middlegame and endgame rather than book
    /// positions alone.
    /// </summary>
    private static List<string> Positions(int count, int seed)
    {
        string epd = FindOpeningSuite();
        var starts = new List<string>();
        foreach (string line in File.ReadLines(epd))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#')) starts.Add(trimmed);
        }

        var random = new Random(seed);
        var positions = new List<string>(count);
        var buffer = new Move[MoveGenerator.MaxMoves];

        for (int i = 0; positions.Count < count && i < starts.Count * 4; i++)
        {
            var board = new Board();
            board.LoadFromFen(starts[i % starts.Count]);
            var generator = new MoveGenerator(board);

            int plies = random.Next(0, 60);
            for (int p = 0; p < plies; p++)
            {
                generator.GenerateLegalMovesInto(buffer, out int legal);
                if (legal == 0) break;
                board.MakeMove(buffer[random.Next(legal)]);
            }

            positions.Add(board.ExportToFen());
        }

        return positions;
    }

    private static string FindOpeningSuite()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "openings", "generated-500.epd");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("openings/generated-500.epd not found above " + AppContext.BaseDirectory);
    }
}
