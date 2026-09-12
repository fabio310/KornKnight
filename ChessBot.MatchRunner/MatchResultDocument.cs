namespace ChessBot.MatchRunner;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Versioned, serializable per-move record for the machine-readable match result format.
/// Mirrors <see cref="MoveRecord"/> but is a plain DTO so its shape is stable across engine
/// refactors (schema changes must bump <see cref="MatchResultDocument.CurrentSchemaVersion"/>).
///
/// Carries the full search diagnostics, not just depth/score/nodes/PV: the structured result is
/// meant to be the authoritative machine-readable record of a run, and a consumer that has to
/// fall back to scraping the human log for the rest is not served by it.
/// </summary>
public sealed class MoveRecordDto
{
    public int    MoveNumber     { get; set; }
    public bool   IsWhiteMove    { get; set; }
    public bool   IsChessBotMove { get; set; }
    public string UciMove        { get; set; } = string.Empty;
    public string Fen            { get; set; } = string.Empty;
    public int    Depth          { get; set; }
    public int    SelDepth       { get; set; }
    public int    ScoreCp        { get; set; }
    public int?   ScoreMate      { get; set; }
    public string ScoreBound     { get; set; } = "exact";
    public long   Nodes          { get; set; }
    public long   Nps            { get; set; }
    public int    HashFull       { get; set; }
    public long   TbHits         { get; set; }
    public long   ElapsedMs      { get; set; }
    public string Pv             { get; set; } = string.Empty;

    // ── Node accounting ──────────────────────────────────────────────────────
    public long   MainNodes      { get; set; }
    public long   QNodes         { get; set; }
    /// <summary>Share of total nodes spent in quiescence (0-1).</summary>
    public double QNodeShare     { get; set; }

    // ── Work done ────────────────────────────────────────────────────────────
    public long   EvaluationCalls { get; set; }
    public long   MovesGenerated  { get; set; }

    // ── Cutoffs and heuristics ───────────────────────────────────────────────
    public long   BetaCutoffs          { get; set; }
    /// <summary>Raw count, kept alongside the rate so the rate can be recomputed or re-aggregated.</summary>
    public long   BetaCutoffsFirstMove { get; set; }
    public double FirstMoveCutoffRate  { get; set; }
    public long   NullMoveAttempts     { get; set; }
    public long   NullMoveCutoffs      { get; set; }
    public long   LmrReductions        { get; set; }
    public long   LmrReSearches        { get; set; }
    public long   LmrPliesSaved        { get; set; }
    public long   FutilitySkips        { get; set; }
    public long   PvsReSearches        { get; set; }
    public long   AspirationFailLow    { get; set; }
    public long   AspirationFailHigh   { get; set; }
    public long   AspirationRetryNodes { get; set; }
    public long   RepetitionDraws      { get; set; }

    // ── Iteration shape ──────────────────────────────────────────────────────
    /// <summary>Last completed iteration's own node count.</summary>
    public long   LastIterationNodes { get; set; }
    /// <summary>
    /// Last completed iteration's nodes divided by the previous iteration's — the branching
    /// estimate. 0 when fewer than two iterations completed. Replaces the former
    /// "EffectiveBranchingFactor", which had no branching-factor meaning.
    /// </summary>
    public double IterationNodeRatio { get; set; }

    // ── Partial-iteration state ──────────────────────────────────────────────
    public int    PartialDepth          { get; set; }
    public bool   UsedPartialRootResult { get; set; }
    public int    RootMovesCompleted    { get; set; }
    public int    RootMoveCount         { get; set; }
    public double RootCoveragePercent   { get; set; }
    public bool   PartialScoreIsExact   { get; set; }
    /// <summary>Move made without any completed search because the budget was too small.</summary>
    public bool   IsUnsearchedFallbackMove { get; set; }

    public static MoveRecordDto From(MoveRecord m) => new()
    {
        MoveNumber     = m.MoveNumber,
        IsWhiteMove    = m.IsWhiteMove,
        IsChessBotMove = m.IsChessBotMove,
        UciMove        = m.UciMove,
        Fen            = m.Fen,
        Depth          = m.Depth,
        SelDepth       = m.SelDepth,
        ScoreCp        = m.ScoreCp,
        ScoreMate      = m.ScoreMate,
        ScoreBound     = m.ScoreBound,
        Nodes          = m.Nodes,
        Nps            = m.Nps,
        HashFull       = m.HashFull,
        TbHits         = m.TbHits,
        ElapsedMs      = m.ElapsedMs,
        Pv             = m.Pv,

        MainNodes      = m.MainNodes,
        QNodes         = m.QNodes,
        QNodeShare     = m.Nodes > 0 ? (double)m.QNodes / m.Nodes : 0,

        EvaluationCalls = m.EvaluationCalls,
        MovesGenerated  = m.MovesGenerated,

        BetaCutoffs          = m.BetaCutoffs,
        BetaCutoffsFirstMove = m.BetaCutoffsFirstMove,
        FirstMoveCutoffRate  = m.FirstMoveCutoffRate,
        NullMoveAttempts     = m.NullMoveAttempts,
        NullMoveCutoffs      = m.NullMoveCutoffs,
        LmrReductions        = m.LmrReductions,
        LmrReSearches        = m.LmrReSearches,
        LmrPliesSaved        = m.LmrPliesSaved,
        FutilitySkips        = m.FutilitySkips,
        PvsReSearches        = m.PvsReSearches,
        AspirationFailLow    = m.AspirationFailLow,
        AspirationFailHigh   = m.AspirationFailHigh,
        AspirationRetryNodes = m.AspirationRetryNodes,
        RepetitionDraws      = m.RepetitionDraws,

        LastIterationNodes = m.LastIterationNodes,
        IterationNodeRatio = m.IterationNodeRatio,

        PartialDepth             = m.PartialDepth,
        UsedPartialRootResult    = m.UsedPartialRootResult,
        RootMovesCompleted       = m.RootMovesCompleted,
        RootMoveCount            = m.RootMoveCount,
        RootCoveragePercent      = m.RootCoveragePercent,
        PartialScoreIsExact      = m.PartialScoreIsExact,
        IsUnsearchedFallbackMove = m.IsUnsearchedFallbackMove,
    };
}

/// <summary>One reference-engine move-loss sample, in serializable form.</summary>
public sealed class MoveLossRecordDto
{
    public int    MoveNumber  { get; set; }
    public bool   IsWhiteMove { get; set; }
    public string Move        { get; set; } = string.Empty;
    public string Fen         { get; set; } = string.Empty;
    public int?   BestScoreCp      { get; set; }
    public int?   BestScoreMate    { get; set; }
    public string BestScoreBound   { get; set; } = "exact";
    public int?   PlayedScoreCp    { get; set; }
    public int?   PlayedScoreMate  { get; set; }
    public string PlayedScoreBound { get; set; } = "exact";
    public int?   CentipawnLoss    { get; set; }
    public string Eligibility      { get; set; } = string.Empty;
    public string MateCategory     { get; set; } = string.Empty;
    public int    RetriesUsed      { get; set; }
    public string? Note            { get; set; }

    public static MoveLossRecordDto From(MoveLossAnalyzer.MoveLossRecord r) => new()
    {
        MoveNumber       = r.MoveNumber,
        IsWhiteMove      = r.IsWhiteMove,
        Move             = r.Move,
        Fen              = r.Fen,
        BestScoreCp      = r.BestScoreCp,
        BestScoreMate    = r.BestScoreMate,
        BestScoreBound   = r.BestScoreBound,
        PlayedScoreCp    = r.PlayedScoreCp,
        PlayedScoreMate  = r.PlayedScoreMate,
        PlayedScoreBound = r.PlayedScoreBound,
        CentipawnLoss    = r.CentipawnLoss,
        Eligibility      = r.Eligibility.ToString(),
        MateCategory     = r.Mate.ToString(),
        RetriesUsed      = r.RetriesUsed,
        Note             = r.IntervalNote,
    };
}

/// <summary>
/// Aggregate of move-loss samples. Eligible centipawn samples are summarised numerically;
/// every excluded category is counted separately so an average is never quietly computed over
/// a filtered subset without saying how much was filtered out.
/// </summary>
public sealed class MoveLossAggregateDto
{
    public int TotalSamples     { get; set; }
    public int EligibleSamples  { get; set; }
    public int ExcludedSamples  => TotalSamples - EligibleSamples;

    public double? MedianCentipawnLoss { get; set; }
    public double? MeanCentipawnLoss   { get; set; }
    public double? P95CentipawnLoss    { get; set; }
    public int     LossesAbove200Cp    { get; set; }

    /// <summary>Counts by <see cref="MoveLossAnalyzer.SampleEligibility"/> name.</summary>
    public Dictionary<string, int> EligibilityCounts { get; set; } = new();
    /// <summary>Counts by <see cref="MoveLossAnalyzer.MateCategory"/> name, excluding None.</summary>
    public Dictionary<string, int> MateCategoryCounts { get; set; } = new();

    public static MoveLossAggregateDto From(IEnumerable<MoveLossAnalyzer.MoveLossRecord> records)
    {
        var all = records.ToList();
        var agg = new MoveLossAggregateDto { TotalSamples = all.Count };

        foreach (var group in all.GroupBy(r => r.Eligibility.ToString()))
            agg.EligibilityCounts[group.Key] = group.Count();

        foreach (var group in all.Where(r => r.Mate != MoveLossAnalyzer.MateCategory.None)
                                 .GroupBy(r => r.Mate.ToString()))
            agg.MateCategoryCounts[group.Key] = group.Count();

        var losses = all
            .Where(r => r.Eligibility == MoveLossAnalyzer.SampleEligibility.Exact && r.CentipawnLoss.HasValue)
            .Select(r => r.CentipawnLoss!.Value)
            .OrderBy(v => v)
            .ToList();

        agg.EligibleSamples  = losses.Count;
        agg.LossesAbove200Cp = losses.Count(v => v > 200);

        if (losses.Count > 0)
        {
            agg.MeanCentipawnLoss   = losses.Average();
            agg.MedianCentipawnLoss = losses[losses.Count / 2];
            int p95 = (int)Math.Ceiling(0.95 * losses.Count) - 1;
            agg.P95CentipawnLoss    = losses[Math.Clamp(p95, 0, losses.Count - 1)];
        }

        return agg;
    }

    /// <summary>
    /// Folds per-game aggregates into a run-level one, for a long run that keeps a summary per
    /// game rather than every sample.
    ///
    /// The mean and the counts combine exactly, from the per-game sums. The median and the 95th
    /// percentile do not: an order statistic cannot be recovered from other order statistics, and
    /// averaging per-game medians would produce a number that is not the median of anything. They
    /// are left null rather than approximated, because a plausible wrong quantile is worse than a
    /// missing one — the per-game reports still hold the samples they were computed from.
    /// </summary>
    public static MoveLossAggregateDto? Combine(IReadOnlyList<MoveLossAggregateDto> parts)
    {
        if (parts.Count == 0) return null;

        var combined = new MoveLossAggregateDto();
        double lossSum = 0;

        foreach (var part in parts)
        {
            combined.TotalSamples    += part.TotalSamples;
            combined.EligibleSamples += part.EligibleSamples;
            combined.LossesAbove200Cp += part.LossesAbove200Cp;

            if (part.MeanCentipawnLoss is double mean)
                lossSum += mean * part.EligibleSamples;

            foreach (var (key, count) in part.EligibilityCounts)
                combined.EligibilityCounts[key] = combined.EligibilityCounts.GetValueOrDefault(key) + count;
            foreach (var (key, count) in part.MateCategoryCounts)
                combined.MateCategoryCounts[key] = combined.MateCategoryCounts.GetValueOrDefault(key) + count;
        }

        if (combined.EligibleSamples > 0)
            combined.MeanCentipawnLoss = lossSum / combined.EligibleSamples;

        return combined;
    }

    /// <summary>One line for a report header. Says what was measured and what was excluded.</summary>
    public override string ToString() =>
        MeanCentipawnLoss is double mean
            ? $"mean {mean:F1} cp over {EligibleSamples} exact samples " +
              $"({ExcludedSamples} excluded, {LossesAbove200Cp} above 200 cp)"
            : $"no exact samples ({TotalSamples} analysed, all excluded)";
}

/// <summary>
/// Versioned, serializable per-game record for the machine-readable match result format.
/// </summary>
public sealed class GameResultDto
{
    public int    GameNumber        { get; set; }
    public bool   ChessBotIsWhite   { get; set; }
    public string Outcome           { get; set; } = string.Empty;
    public string InitialFen        { get; set; } = string.Empty;
    public string? TerminationReason { get; set; }
    public string PgnPath           { get; set; } = string.Empty;
    public List<MoveRecordDto> Moves { get; set; } = new();

    /// <summary>Cross-engine evaluation disagreements found in this game (diagnostic only).</summary>
    public List<DisagreementDto> Disagreements { get; set; } = new();

    /// <summary>Reference-engine move-loss samples for this game; empty when analysis was off.</summary>
    public List<MoveLossRecordDto> MoveLoss { get; set; } = new();

    /// <summary>Per-game move-loss aggregate; null when analysis was off.</summary>
    public MoveLossAggregateDto? MoveLossAggregate { get; set; }

    /// <summary>
    /// LMR reductions summed over the game's ChessBot moves, bucketed by remaining depth
    /// (index = depth). Aggregated per game rather than stored per move: the point is to see
    /// where the schedule actually fires before tuning it, and a 32-entry array on every move
    /// would bloat the document without adding information.
    /// </summary>
    public long[] LmrReductionsByDepth { get; set; } = Array.Empty<long>();

    /// <summary>Same, bucketed by move number at the node (index = move number).</summary>
    public long[] LmrReductionsByMoveNumber { get; set; } = Array.Empty<long>();

    /// <summary>Sums the per-move LMR buckets of this game's ChessBot moves.</summary>
    public static long[] SumBuckets(IEnumerable<long[]> buckets)
    {
        long[]? total = null;
        foreach (var b in buckets)
        {
            if (b.Length == 0) continue;
            total ??= new long[b.Length];
            for (int i = 0; i < b.Length && i < total.Length; i++) total[i] += b[i];
        }
        return total ?? Array.Empty<long>();
    }

    public static GameResultDto From(GameResult g) => new()
    {
        GameNumber         = g.GameNumber,
        ChessBotIsWhite    = g.ChessBotIsWhite,
        Outcome            = g.Outcome.ToString(),
        InitialFen         = g.InitialFen,
        TerminationReason  = g.TerminationReason,
        PgnPath            = g.PgnPath,
        Moves              = g.Moves.Select(MoveRecordDto.From).ToList(),

        LmrReductionsByDepth =
            SumBuckets(g.Moves.Where(m => m.IsChessBotMove).Select(m => m.LmrReductionsByDepth)),
        LmrReductionsByMoveNumber =
            SumBuckets(g.Moves.Where(m => m.IsChessBotMove).Select(m => m.LmrReductionsByMoveNumber)),
    };
}

/// <summary>
/// One position where ChessBot's own evaluation and the opponent's disagreed. This is a
/// diagnostic signal about differing evaluation scales between two engines, not a measured
/// centipawn loss and not a confirmed mistake.
/// </summary>
public sealed class DisagreementDto
{
    public int    MoveNumber  { get; set; }
    public bool   IsWhiteMove { get; set; }
    public string Move        { get; set; } = string.Empty;
    public string Fen         { get; set; } = string.Empty;
    /// <summary>ChessBot's own score before the move (side-to-move positive).</summary>
    public int    ScoreBefore { get; set; }
    /// <summary>Opponent's score for the position the move created, before it replies.</summary>
    public int    ScoreAfter  { get; set; }
    /// <summary>Sum of the two, across two different engines' scales. Diagnostic only.</summary>
    public int    SwingCp     { get; set; }
}

/// <summary>Reference-engine identity and options actually in effect.</summary>
public sealed class ReferenceEngineDto
{
    public string Path    { get; set; } = string.Empty;
    public string Name    { get; set; } = string.Empty;
    public int    Depth   { get; set; }
    /// <summary>Options explicitly set for this run.</summary>
    public Dictionary<string, string> Options { get; set; } = new();
    /// <summary>Options the engine reported, including defaults it was left at.</summary>
    public Dictionary<string, string> ReportedOptions { get; set; } = new();
    /// <summary>Network/NNUE lines the engine reported, when it reports any.</summary>
    public List<string> Networks { get; set; } = new();
}

/// <summary>Opponent engine identity and strength configuration.</summary>
public sealed class OpponentEngineDto
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = "Unknown";
    /// <summary>UCI_Elo cap, or null when the opponent played at full strength.</summary>
    public int?   LimitedToElo { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

/// <summary>
/// Versioned, machine-readable snapshot of a whole match's results (all games).
/// <see cref="SchemaVersion"/> must be incremented whenever a breaking shape change is made,
/// so downstream tooling (sweeps, dashboards, external analysis scripts) can detect
/// incompatible formats instead of silently misreading fields.
/// </summary>
public sealed class MatchResultDocument
{
    /// <summary>
    /// Bump this whenever a breaking change is made to this document's shape.
    /// v2: full search diagnostics per move, effective configuration, requested vs actual game
    /// counts, opponent and reference-engine configuration, cross-engine disagreements,
    /// per-game and aggregate move-loss results, run id and artifact references.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int    SchemaVersion  { get; set; } = CurrentSchemaVersion;

    /// <summary>Identifier shared by every artifact this run produced.</summary>
    public string RunId          { get; set; } = string.Empty;

    public string OpponentName   { get; set; } = "Unknown";
    public int    Wins           { get; set; }
    public int    Draws          { get; set; }
    public int    Losses         { get; set; }
    public double ScoreRate      { get; set; }

    /// <summary>Games the invocation asked for.</summary>
    public int    RequestedGames { get; set; }
    /// <summary>Games actually played and recorded. A mismatch means the run did not complete.</summary>
    public int    ActualGames    { get; set; }
    /// <summary>Difference between games played as White and as Black (0 or 1).</summary>
    public int    ColorImbalance { get; set; }

    /// <summary>Whether ChessBot's search was allowed to use partial-iteration root results.</summary>
    public bool   UsePartialRootResult { get; set; }

    /// <summary>The configuration the invocation actually parsed to.</summary>
    public Dictionary<string, string> EffectiveConfig { get; set; } = new();

    public OpponentEngineDto?  Opponent        { get; set; }
    public ReferenceEngineDto? ReferenceEngine { get; set; }

    /// <summary>Move-loss aggregate over every game; null when reference analysis was off.</summary>
    public MoveLossAggregateDto? MoveLossAggregate { get; set; }

    /// <summary>Files this run produced, relative to the output directory.</summary>
    public List<string> ArtifactFiles { get; set; } = new();

    public List<GameResultDto> Games { get; set; } = new();

    public static MatchResultDocument From(MatchOutcome outcome) => new()
    {
        RunId        = outcome.RunId,
        OpponentName = outcome.OpponentName,
        Wins         = outcome.Wins,
        Draws        = outcome.Draws,
        Losses       = outcome.Losses,
        ScoreRate    = outcome.ScoreRate,
        ActualGames  = outcome.Games.Count,
        Games        = outcome.Games.Select(GameResultDto.From).ToList(),
    };
}

/// <summary>
/// Reads/writes <see cref="MatchResultDocument"/> as JSON.
/// </summary>
public static class MatchResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Write(MatchResultDocument document, string path)
    {
        string json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static void Write(MatchOutcome outcome, string path) =>
        Write(MatchResultDocument.From(outcome), path);

    public static MatchResultDocument Read(string path)
    {
        string json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<MatchResultDocument>(json, JsonOptions)
            ?? throw new InvalidDataException($"Could not deserialize match result document from {path}");

        if (doc.SchemaVersion > MatchResultDocument.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"{path} declares schema version {doc.SchemaVersion}, but this build understands at most " +
                $"{MatchResultDocument.CurrentSchemaVersion}. Reading it would silently misinterpret fields.");

        return doc;
    }
}
