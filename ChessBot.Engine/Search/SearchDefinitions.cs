namespace ChessBot.Engine.Search;

using ChessBot.Engine.Types;

/// <summary>
/// Configuration for an engine search operation.
/// Specifies the constraints under which the engine will analyze a position.
/// </summary>
public class SearchSettings
{
    /// <summary>
    /// Maximum search depth (plies). If null, no depth limit.
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Maximum time allowed for search in milliseconds. If null, no time limit.
    /// </summary>
    public int? MaxTimeMs { get; set; }

    /// <summary>
    /// Minimum number of nodes to search. If null, defaults to 300,000.
    /// </summary>
    public long? MinNodeTarget { get; set; }

    /// <summary>
    /// If true, the search will perform iterative deepening (search at depths 1, 2, ..., until time/depth limit).
    /// </summary>
    public bool UseIterativeDeepening { get; set; } = true;

    /// <summary>
    /// If true, the search will output debug/telemetry information.
    /// </summary>
    public bool Verbose { get; set; }

    public SearchSettings()
    {
        MinNodeTarget ??= 300_000;
    }
}

/// <summary>
/// The result of a search operation, containing the best move, evaluation, and telemetry.
/// </summary>
public class SearchResult
{
    /// <summary>
    /// The best move found.
    /// </summary>
    public Move BestMove { get; set; }

    /// <summary>
    /// The evaluation of the position in centipawns (positive = White advantage, negative = Black advantage).
    /// </summary>
    public int Evaluation { get; set; }

    /// <summary>
    /// The principal variation (line of best play from both sides).
    /// </summary>
    public List<Move> PrincipalVariation { get; set; } = new();

    /// <summary>
    /// The search depth achieved.
    /// </summary>
    public int DepthAchieved { get; set; }

    /// <summary>
    /// Total nodes evaluated during the search.
    /// </summary>
    public long NodesSearched { get; set; }

    /// <summary>
    /// Nodes per second (performance metric).
    /// </summary>
    public double NodesPerSecond { get; set; }

    /// <summary>
    /// Time taken for the search in milliseconds.
    /// </summary>
    public long ElapsedTimeMs { get; set; }

    /// <summary>
    /// Returns true if the search found a checkmate.
    /// </summary>
    public bool IsCheckmate { get; set; }

    /// <summary>
    /// Returns true if the position is a stalemate.
    /// </summary>
    public bool IsStalemate { get; set; }

    // ── Extended diagnostics ─────────────────────────────────────────────────

    /// <summary>Quiescence nodes searched (not counted in NodesSearched).</summary>
    public long QNodesSearched { get; set; }

    /// <summary>Maximum quiescence/selective depth reached.</summary>
    public int SelDepth { get; set; }

    /// <summary>Number of transposition-table probe attempts.</summary>
    public int TTProbes { get; set; }

    /// <summary>Number of TT probes that found a matching entry.</summary>
    public int TTHits { get; set; }

    /// <summary>Number of TT hits that caused an immediate cutoff.</summary>
    public int TTCutoffs { get; set; }

    /// <summary>Number of entries written to the TT.</summary>
    public int TTStores { get; set; }

    /// <summary>
    /// TT fill in per-mille (0–1000). -1 means not reported.
    /// Used by the match runner to log "Avg TT fill".
    /// </summary>
    public int HashFull { get; set; } = -1;
}

/// <summary>
/// The result of board evaluation (non-search static assessment).
/// </summary>
public class EvaluationResult
{
    /// <summary>
    /// The static evaluation in centipawns.
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// Breakdown of material balance.
    /// </summary>
    public MaterialBalance MaterialBalance { get; set; } = new();
}

/// <summary>
/// Detailed breakdown of material on the board.
/// </summary>
public class MaterialBalance
{
    /// <summary>
    /// White's material count in centipawns.
    /// </summary>
    public int WhiteMaterial { get; set; }

    /// <summary>
    /// Black's material count in centipawns.
    /// </summary>
    public int BlackMaterial { get; set; }

    /// <summary>
    /// The material imbalance (White - Black) in centipawns.
    /// </summary>
    public int Imbalance => WhiteMaterial - BlackMaterial;
}
