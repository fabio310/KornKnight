namespace ChessBot.Tests;

using System.Reflection;
using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// The structured result is meant to be the authoritative machine-readable record of a run.
/// A round trip that only checks the handful of fields the format started with would let every
/// later addition silently fail to serialize, so these tests populate every field with a
/// distinct value and assert it survives — and a reflection sweep fails when a newly added
/// property is left untested.
/// </summary>
public class MatchResultDocumentRoundTripTests : IDisposable
{
    private readonly string _dir;

    public MatchResultDocumentRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"matchresult_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string TempPath(string name) => Path.Combine(_dir, name);

    /// <summary>A MoveRecord with every field set to a distinct, recognisable value.</summary>
    private static MoveRecord FullyPopulatedMove() => new()
    {
        MoveNumber     = 7,
        IsWhiteMove    = true,
        IsChessBotMove = true,
        UciMove        = "e2e4",
        Fen            = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        Depth          = 12,
        SelDepth       = 27,
        ScoreCp        = 35,
        ScoreMate      = null,
        ScoreBound     = "lowerbound",
        Nodes          = 123_456,
        Nps            = 1_000_000,
        HashFull       = 421,
        TbHits         = 5,
        ElapsedMs      = 987,
        Pv             = "e2e4 e7e5 g1f3",

        MainNodes      = 100_000,
        QNodes         = 23_456,

        EvaluationCalls = 90_000,
        MovesGenerated  = 400_000,

        BetaCutoffs          = 30_000,
        BetaCutoffsFirstMove = 24_000,
        FirstMoveCutoffRate  = 0.8,
        NullMoveAttempts     = 5_000,
        NullMoveCutoffs      = 4_100,
        LmrReductions        = 12_000,
        LmrReSearches        = 90,
        LmrPliesSaved        = 26_000,
        FutilitySkips        = 7_000,
        PvsReSearches        = 210,
        AspirationFailLow    = 3,
        AspirationFailHigh   = 2,
        AspirationRetryNodes = 45_000,
        RepetitionDraws      = 11,

        LastIterationNodes = 60_000,
        IterationNodeRatio = 3.25,

        PartialDepth             = 13,
        UsedPartialRootResult    = true,
        RootMovesCompleted       = 4,
        RootMoveCount            = 20,
        RootCoveragePercent      = 20.0,
        PartialScoreIsExact      = true,
        IsUnsearchedFallbackMove = false,
    };

    private static MoveLossAnalyzer.MoveLossRecord LossRecord(
        int moveNumber, int? loss, MoveLossAnalyzer.SampleEligibility eligibility,
        MoveLossAnalyzer.MateCategory mate = MoveLossAnalyzer.MateCategory.None) =>
        new(moveNumber, true, "e2e4", "fen", 40, null, "exact", 40 - (loss ?? 0), null, "exact",
            loss, eligibility, mate, 1, "note");

    [Fact]
    public void WriteThenRead_PreservesEverySerializedMoveField()
    {
        var outcome = new MatchOutcome { OpponentName = "TestEngine 1.0", Wins = 3, Draws = 1, Losses = 2, RunId = "20260906_223000" };
        var game = new GameResult
        {
            GameNumber      = 1,
            ChessBotIsWhite = true,
            Outcome         = GameOutcome.ChessBotWin,
            InitialFen      = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            PgnPath         = "game1.pgn",
            TerminationReason = "Checkmate",
        };
        game.Moves.Add(FullyPopulatedMove());
        outcome.Games.Add(game);

        string path = TempPath("match_result.json");
        MatchResultWriter.Write(outcome, path);
        var back = MatchResultWriter.Read(path);

        var expected = MoveRecordDto.From(FullyPopulatedMove());
        var actual   = back.Games.Single().Moves.Single();

        // Every public property of the DTO must survive the round trip. Reflection rather than a
        // hand-written list, so adding a field without serializing it fails here.
        foreach (var prop in typeof(MoveRecordDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? e = prop.GetValue(expected);
            object? a = prop.GetValue(actual);
            Assert.True(Equals(e, a), $"MoveRecordDto.{prop.Name}: expected {e}, got {a}");
        }

        // Spot-check the values that were added after the original format, so the reflection
        // sweep cannot pass by comparing two identically-empty objects.
        Assert.Equal(100_000, actual.MainNodes);
        Assert.Equal(23_456,  actual.QNodes);
        Assert.Equal(13,      actual.PartialDepth);
        Assert.True(actual.UsedPartialRootResult);
        Assert.Equal(20.0,    actual.RootCoveragePercent);
        Assert.Equal(3.25,    actual.IterationNodeRatio);
        Assert.Equal(24_000,  actual.BetaCutoffsFirstMove);
        Assert.Equal(45_000,  actual.AspirationRetryNodes);
        Assert.Equal(26_000,  actual.LmrPliesSaved);
    }

    [Fact]
    public void WriteThenRead_PreservesRunLevelMetadata()
    {
        var outcome = new MatchOutcome { OpponentName = "Stockfish 18", Wins = 0, Draws = 0, Losses = 4, RunId = "20260906_223000" };
        for (int i = 1; i <= 4; i++)
            outcome.Games.Add(new GameResult { GameNumber = i, ChessBotIsWhite = i % 2 == 1, Outcome = GameOutcome.ChessBotLoss });

        var doc = MatchResultDocument.From(outcome);
        doc.RequestedGames       = 5;
        doc.ColorImbalance       = 1;
        doc.UsePartialRootResult = true;
        doc.EffectiveConfig      = new Dictionary<string, string> { ["TotalGames"] = "5", ["MoveTimeMs"] = "1000" };
        doc.Opponent = new OpponentEngineDto
        {
            Path = @"C:\engines\sf.exe", Name = "Stockfish 18", LimitedToElo = 1800,
            Options = new Dictionary<string, string> { ["Threads"] = "1" },
        };
        doc.ReferenceEngine = new ReferenceEngineDto
        {
            Path = @"C:\engines\sf.exe", Name = "Stockfish 18", Depth = 18,
            Options = new Dictionary<string, string> { ["Hash"] = "128" },
            ReportedOptions = new Dictionary<string, string> { ["Threads"] = "1", ["Hash"] = "16" },
            Networks = new List<string> { "info string NNUE evaluation using nn-abc.nnue" },
        };
        doc.ArtifactFiles = new List<string> { "game1.pgn", "match_result.json" };
        doc.Games[0].Disagreements.Add(new DisagreementDto
        { MoveNumber = 9, IsWhiteMove = true, Move = "d2d4", Fen = "fen", ScoreBefore = 20, ScoreAfter = 300, SwingCp = 320 });

        string path = TempPath("meta.json");
        MatchResultWriter.Write(doc, path);
        var back = MatchResultWriter.Read(path);

        Assert.Equal(MatchResultDocument.CurrentSchemaVersion, back.SchemaVersion);
        Assert.Equal(2, back.SchemaVersion);              // v2 carries the full diagnostics
        Assert.Equal("20260906_223000", back.RunId);
        Assert.Equal(5, back.RequestedGames);
        Assert.Equal(4, back.ActualGames);                // requested and actual differ visibly
        Assert.Equal(1, back.ColorImbalance);
        Assert.True(back.UsePartialRootResult);
        Assert.Equal("1000", back.EffectiveConfig["MoveTimeMs"]);
        Assert.Equal(1800, back.Opponent!.LimitedToElo);
        Assert.Equal("Stockfish 18", back.ReferenceEngine!.Name);
        Assert.Equal(18, back.ReferenceEngine.Depth);
        Assert.Equal("16", back.ReferenceEngine.ReportedOptions["Hash"]);   // default it was left at
        Assert.Single(back.ReferenceEngine.Networks);
        Assert.Equal(2, back.ArtifactFiles.Count);
        Assert.Equal(320, back.Games[0].Disagreements.Single().SwingCp);
    }

    [Fact]
    public void WriteThenRead_PreservesMoveLossRecordsAndAggregate()
    {
        var outcome = new MatchOutcome { OpponentName = "Stockfish 18", Losses = 1, RunId = "r" };
        outcome.Games.Add(new GameResult { GameNumber = 1, Outcome = GameOutcome.ChessBotLoss });

        var records = new List<MoveLossAnalyzer.MoveLossRecord>
        {
            LossRecord(1,  10, MoveLossAnalyzer.SampleEligibility.Exact),
            LossRecord(2,  30, MoveLossAnalyzer.SampleEligibility.Exact),
            LossRecord(3, 250, MoveLossAnalyzer.SampleEligibility.Exact),
            LossRecord(4, null, MoveLossAnalyzer.SampleEligibility.UnresolvedBound),
            LossRecord(5, null, MoveLossAnalyzer.SampleEligibility.Inconsistent),
            LossRecord(6, null, MoveLossAnalyzer.SampleEligibility.AnalysisError),
            LossRecord(7, null, MoveLossAnalyzer.SampleEligibility.MateInvolved,
                       MoveLossAnalyzer.MateCategory.MissedForcedMate),
        };

        var doc = MatchResultDocument.From(outcome);
        doc.Games[0].MoveLoss          = records.Select(MoveLossRecordDto.From).ToList();
        doc.Games[0].MoveLossAggregate = MoveLossAggregateDto.From(records);
        doc.MoveLossAggregate          = MoveLossAggregateDto.From(records);

        string path = TempPath("loss.json");
        MatchResultWriter.Write(doc, path);
        var back = MatchResultWriter.Read(path);

        Assert.Equal(7, back.Games[0].MoveLoss.Count);
        var agg = back.MoveLossAggregate!;
        Assert.Equal(7, agg.TotalSamples);
        Assert.Equal(3, agg.EligibleSamples);      // only the Exact ones are averaged
        Assert.Equal(4, agg.ExcludedSamples);
        Assert.Equal(1, agg.LossesAbove200Cp);
        Assert.Equal(30, agg.MedianCentipawnLoss);
        Assert.Equal(1, agg.EligibilityCounts["UnresolvedBound"]);
        Assert.Equal(1, agg.EligibilityCounts["Inconsistent"]);
        Assert.Equal(1, agg.EligibilityCounts["AnalysisError"]);
        Assert.Equal(1, agg.MateCategoryCounts["MissedForcedMate"]);

        // A mate sample must never be folded into the centipawn average.
        Assert.DoesNotContain(back.Games[0].MoveLoss,
            r => r.MateCategory != "None" && r.CentipawnLoss.HasValue);
    }

    [Fact]
    public void Read_RefusesANewerSchemaInsteadOfMisreadingIt()
    {
        string path = TempPath("future.json");
        File.WriteAllText(path, $"{{\"SchemaVersion\":{MatchResultDocument.CurrentSchemaVersion + 1}}}");

        var ex = Assert.Throws<InvalidDataException>(() => MatchResultWriter.Read(path));
        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
