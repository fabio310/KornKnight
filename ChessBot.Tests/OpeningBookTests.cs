namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// Gates for the start positions a run plays from.
///
/// Every game used to start from the initial position. Against an external engine both sides are
/// near-deterministic there, so two ten-game runs produced only four distinct openings across ten
/// games — the runs had a third of the sample size their confidence intervals were computed from.
/// These tests hold the replacement to what it has to be: positions that are real and playable, a
/// schedule that is balanced and reproducible, and an identity that two runs can be compared on.
/// </summary>
public class OpeningBookTests
{
    // ── The built-in set ─────────────────────────────────────────────────────

    [Fact]
    public void Standard_PositionsAreLegalPlayableAndDistinct()
    {
        var set = OpeningBook.Standard;

        Assert.Equal(16, set.Count);
        Assert.Equal(set.Count, set.Openings.Select(o => o.Fen).Distinct().Count());

        foreach (var opening in set.Openings)
        {
            var engine = new ChessEngine();
            engine.LoadFen(opening.Fen);

            // A position that will not load or has no moves is not an opening; it is a run that
            // dies eight games in.
            Assert.NotEmpty(engine.GetLegalMoves());

            // Material must be level. An opening that hands one side a pawn adds variance that
            // has nothing to do with what the run is measuring.
            Assert.Equal(0, engine.Evaluate().MaterialBalance.Imbalance);

            Assert.False(string.IsNullOrWhiteSpace(opening.Name));
        }
    }

    [Fact]
    public void Standard_CoversBothFirstMovesAndIsEightPliesDeep()
    {
        var set = OpeningBook.Standard;
        Assert.Equal(8, set.Plies);

        // Eight plies is four full moves, so every position is at move 5 with White to move.
        foreach (var opening in set.Openings)
        {
            var fields = opening.Fen.Split(' ');
            Assert.Equal("w", fields[1]);
            Assert.Equal("5", fields[5]);
        }
    }

    [Fact]
    public void Standard_HashIsStableAcrossCallsAndIdentifiesThePositions()
    {
        string first  = OpeningBook.Standard.Sha256;
        string second = OpeningSet.HashOf(OpeningBook.Standard.Openings);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);

        // Names are labels, not the experiment: a renamed opening set is the same set, and two
        // runs are comparable exactly when their positions match.
        var renamed = OpeningBook.Standard.Openings.Select(o => o with { Name = "x" }).ToList();
        Assert.Equal(first, OpeningSet.HashOf(renamed));

        // A different position is a different set.
        var changed = OpeningBook.Standard.Openings.ToList();
        changed[0] = changed[0] with { Fen = OpeningBook.StartFen };
        Assert.NotEqual(first, OpeningSet.HashOf(changed));
    }

    // ── Scheduling ───────────────────────────────────────────────────────────

    [Fact]
    public void ScheduleFor_PlaysEveryOpeningOnceWithEachColour()
    {
        var set = OpeningBook.Standard;
        var whiteGames = new Dictionary<string, int>();
        var blackGames = new Dictionary<string, int>();

        for (int game = 0; game < set.GamesBeforeRepeat; game++)
        {
            var (opening, firstPlayerIsWhite) = set.ScheduleFor(game);
            var tally = firstPlayerIsWhite ? whiteGames : blackGames;
            tally[opening.Fen] = tally.GetValueOrDefault(opening.Fen) + 1;
        }

        Assert.Equal(set.Count, whiteGames.Count);
        Assert.Equal(set.Count, blackGames.Count);
        Assert.All(whiteGames.Values, n => Assert.Equal(1, n));
        Assert.All(blackGames.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public void ScheduleFor_KeepsColoursBalancedAtEveryEvenPrefix()
    {
        // A run can be killed at any point, so a schedule that only balances at the end would
        // leave a partial run with a colour bias built into it.
        var set = OpeningBook.Standard;

        int white = 0, black = 0;
        for (int game = 0; game < 100; game++)
        {
            if (set.ScheduleFor(game).firstPlayerIsWhite) white++; else black++;
            if (game % 2 == 1) Assert.Equal(white, black);
        }
    }

    [Fact]
    public void ScheduleFor_IsAPureFunctionOfTheGameIndex()
    {
        // Reproducibility from the manifest alone depends on this: game n is always the same
        // position and the same colour, whatever order the games actually finished in.
        var set = OpeningBook.Standard;

        for (int game = 0; game < 40; game++)
        {
            var first  = set.ScheduleFor(game);
            var second = set.ScheduleFor(game);
            Assert.Equal(first.opening.Fen, second.opening.Fen);
            Assert.Equal(first.firstPlayerIsWhite, second.firstPlayerIsWhite);
        }
    }

    [Fact]
    public void ScheduleFor_CyclesRoundRobinPastTheEndOfTheSet()
    {
        var set = OpeningBook.Standard;
        Assert.Equal(set.ScheduleFor(0).opening.Fen, set.ScheduleFor(set.GamesBeforeRepeat).opening.Fen);
        Assert.Equal(set.ScheduleFor(1).opening.Fen, set.ScheduleFor(set.GamesBeforeRepeat + 1).opening.Fen);
    }

    // ── EPD / FEN loading ────────────────────────────────────────────────────

    [Fact]
    public void Load_ReadsEpdAndFenLinesAndNamesThemFromTheIdOperation()
    {
        string path = WriteTempFile(".epd",
            "# a comment line",
            "",
            "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2",
            "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - ; id \"Italian\"");

        var set = OpeningBook.Load(path);

        Assert.Equal(2, set.Count);
        Assert.Equal("epd/fen", set.Format);
        Assert.Equal("Italian", set.Openings[1].Name);

        // The EPD line had no move counters; they must be supplied, not left to make an
        // unparseable FEN.
        Assert.EndsWith(" 0 1", set.Openings[1].Fen);

        foreach (var opening in set.Openings)
        {
            var engine = new ChessEngine();
            engine.LoadFen(opening.Fen);
            Assert.NotEmpty(engine.GetLegalMoves());
        }
    }

    [Fact]
    public void Load_DropsDuplicatePositions()
    {
        // A file that lists a position twice would weight it double in a round-robin without
        // ever saying so.
        string fen = "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2";
        string path = WriteTempFile(".epd", fen, fen, fen);

        Assert.Equal(1, OpeningBook.Load(path).Count);
    }

    [Fact]
    public void Load_RejectsAFileWithNothingUsableInIt()
    {
        string path = WriteTempFile(".epd", "# nothing but comments");
        Assert.Throws<InvalidDataException>(() => OpeningBook.Load(path));
    }

    [Fact]
    public void Load_RejectsAPositionTheEngineCannotPlayFrom()
    {
        // Stalemate: it loads, and then the run has a game with no moves in it.
        string path = WriteTempFile(".epd", "7k/5Q2/6K1/8/8/8/8/8 b - - 0 1");
        Assert.Throws<InvalidDataException>(() => OpeningBook.Load(path));
    }

    [Fact]
    public void Load_ReportsAMissingFileRatherThanRunningWithoutOne()
    {
        Assert.Throws<FileNotFoundException>(() => OpeningBook.Load("no-such-openings.epd"));
    }

    // ── PGN loading ──────────────────────────────────────────────────────────

    [Fact]
    public void Load_ReplaysSanPgnMovetextToTheRequestedDepth()
    {
        string path = WriteTempFile(".pgn",
            "[Event \"Test\"]",
            "[Opening \"Ruy Lopez\"]",
            "[Result \"*\"]",
            "",
            "1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 4. Ba4 Nf6 5. O-O Be7 *");

        var set = OpeningBook.Load(path, plies: 8);

        Assert.Equal(1, set.Count);
        Assert.Equal("pgn", set.Format);
        Assert.Equal(8, set.Plies);
        Assert.Equal("Ruy Lopez", set.Openings[0].Name);

        // Four moves of the Ruy Lopez is exactly what the built-in set holds, reached by a
        // different route: SAN here, long algebraic there.
        Assert.Equal(
            OpeningBook.Standard.Openings.Single(o => o.Name == "Ruy Lopez").Fen,
            set.Openings[0].Fen);
    }

    [Fact]
    public void Load_IgnoresCommentsVariationsNagsAndResultsInMovetext()
    {
        string path = WriteTempFile(".pgn",
            "[Event \"Test\"]",
            "",
            "1. e4 {best by test} e5 $1 2. Nf3 (2. f4 exf4 is the King's Gambit) Nc6",
            "3. Bb5 a6 4. Ba4 Nf6 1/2-1/2");

        var set = OpeningBook.Load(path, plies: 8);

        Assert.Equal(
            OpeningBook.Standard.Openings.Single(o => o.Name == "Ruy Lopez").Fen,
            set.Openings[0].Fen);
    }

    [Fact]
    public void Load_ReplaysThisProjectsOwnLongAlgebraicPgnToo()
    {
        string path = WriteTempFile(".pgn",
            "[Event \"ChessBot Match\"]",
            "",
            "1. e2e4 e7e5 2. g1f3 b8c6 3. f1b5 a7a6 4. b5a4 g8f6 1-0");

        var set = OpeningBook.Load(path, plies: 8);

        Assert.Equal(
            OpeningBook.Standard.Openings.Single(o => o.Name == "Ruy Lopez").Fen,
            set.Openings[0].Fen);
    }

    [Fact]
    public void Load_StartsFromAGamesOwnFenTagWhenItHasOne()
    {
        string fen = "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1";
        string path = WriteTempFile(".pgn",
            "[Event \"Test\"]",
            "[SetUp \"1\"]",
            $"[FEN \"{fen}\"]",
            "",
            "1. e4 Kd7 *");

        var set = OpeningBook.Load(path, plies: 2);

        Assert.Equal(1, set.Count);
        Assert.StartsWith("8/3k4/8/8/4P3/8/8/4K3", set.Openings[0].Fen);
    }

    [Fact]
    public void Load_SkipsGamesTooShortToProvideTheRequestedBookDepth()
    {
        string path = WriteTempFile(".pgn",
            "[Event \"Short\"]",
            "",
            "1. e4 e5 *",
            "",
            "[Event \"Long\"]",
            "",
            "1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 4. Ba4 Nf6 *");

        var set = OpeningBook.Load(path, plies: 8);

        Assert.Equal(1, set.Count);
        Assert.Equal("Long", set.Openings[0].Name);
    }

    [Fact]
    public void Load_RefusesAMoveThatIsNotLegalRatherThanSkippingIt()
    {
        // A silently skipped move produces a plausible-looking position that is not the opening
        // the file names — the worst possible outcome for a reproducibility record.
        string path = WriteTempFile(".pgn",
            "[Event \"Broken\"]",
            "",
            "1. e4 e5 2. Nf6 *");

        Assert.Throws<InvalidDataException>(() => OpeningBook.Load(path, plies: 4));
    }

    [Fact]
    public void Load_ResolvesDisambiguatedSanAndPromotions()
    {
        // Two rooks on the first rank can both reach d1, so "Rad1" has to pick by file.
        string fen = "4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1";
        string path = WriteTempFile(".pgn",
            "[Event \"Disambiguation\"]",
            "[SetUp \"1\"]",
            $"[FEN \"{fen}\"]",
            "",
            "1. Rad1 Kf8 *");

        var set = OpeningBook.Load(path, plies: 2);
        Assert.StartsWith("5k2/8/8/8/8/8/8/3RK2R", set.Openings[0].Fen);

        string promoFen = "4k3/P7/8/8/8/8/8/4K3 w - - 0 1";
        string promoPath = WriteTempFile(".pgn",
            "[Event \"Promotion\"]",
            "[SetUp \"1\"]",
            $"[FEN \"{promoFen}\"]",
            "",
            "1. a8=Q+ Kf7 *");

        var promo = OpeningBook.Load(promoPath, plies: 2);
        Assert.StartsWith("Q7/5k2/8/8/8/8/8/4K3", promo.Openings[0].Fen);
    }

    // ── Start-position-only ──────────────────────────────────────────────────

    [Fact]
    public void StartPositionOnly_IsOneOpeningAndSaysSo()
    {
        var set = OpeningBook.StartPositionOnly;

        Assert.Equal(1, set.Count);
        Assert.Equal(OpeningBook.StartFen, set.Openings[0].Fen);

        // Two games before the same position repeats with the same colour — which is exactly
        // why this is no longer the default.
        Assert.Equal(2, set.GamesBeforeRepeat);
    }

    private static string WriteTempFile(string extension, params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"openings_{Guid.NewGuid():N}{extension}");
        File.WriteAllLines(path, lines);
        return path;
    }
}
