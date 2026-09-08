namespace ChessBot.MatchRunner;

using System.Text;

/// <summary>
/// Everything needed to diagnose a move that was played but would not apply to the board.
///
/// A rejected move is the one failure a match run cannot afford to record as a bare string. It
/// aborts the game it happens in, and in a 2,000-game run a handful of them inject forfeits into
/// exactly the measurement the run exists to make — while looking, in the summary, like ordinary
/// losses. Reconstructing one after the fact is impossible: the engine processes are gone, and the
/// position it rejected is not the position the summary records.
///
/// So the report is written at the moment of the rejection, from state that only exists then: the
/// position the mover was actually given, the move it answered with, what it claimed about that
/// move, and the last stretch of protocol conversation with BOTH engines — the opponent's included,
/// because a desynchronised transcript shows up as the other side's lines arriving out of turn.
///
/// This is not a debugging scaffold to be removed once the current defect is closed. It is the
/// evidence any future one will need.
/// </summary>
public sealed class RejectedMoveReport
{
    /// <summary>Which engine produced the move — the arm name, not the colour.</summary>
    public required string Player { get; init; }

    /// <summary>The colour that engine was playing when it produced the move.</summary>
    public required string Colour { get; init; }

    /// <summary>The move as the engine wrote it, verbatim and unparsed.</summary>
    public required string MoveText { get; init; }

    /// <summary>The position the move was rejected in, which is the position the engine was given.</summary>
    public required string FenBeforeMove { get; init; }

    /// <summary>The position the game started from, so the whole game can be replayed.</summary>
    public required string StartFen { get; init; }

    /// <summary>Every move played before this one, in the order they were sent to the engines.</summary>
    public required IReadOnlyList<string> MoveHistory { get; init; }

    /// <summary>
    /// The moves that WERE legal. A rejected move that appears in this list means the comparison
    /// is at fault (notation), and one that does not means the engine and the arbiter disagree
    /// about the position itself — two different defects, distinguished only by this line.
    /// </summary>
    public required IReadOnlyList<string> LegalMoves { get; init; }

    public int    MoveNumber { get; init; }
    public int    Depth      { get; init; }
    public string Score      { get; init; } = string.Empty;
    public long   Nodes      { get; init; }
    public long   ElapsedMs  { get; init; }

    /// <summary>
    /// The time the mover was allowed for this move. Paired with <see cref="ElapsedMs"/> this is
    /// what identifies a rejection that only happens when a search is cut off — the second place
    /// to look after notation.
    /// </summary>
    public long BudgetMs { get; init; }

    /// <summary>Recent protocol lines exchanged with the engine that produced the move.</summary>
    public IReadOnlyList<string> MoverTrace { get; init; } = Array.Empty<string>();

    /// <summary>Recent protocol lines exchanged with the other engine.</summary>
    public IReadOnlyList<string> OpponentTrace { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Renders the report as a self-contained text block. Self-contained is the requirement: it
    /// gets pasted into an issue or read out of a log years later, with no access to the run that
    /// produced it.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder(1024);

        sb.AppendLine("======== REJECTED MOVE ========");
        sb.AppendLine($"player      : {Player} ({Colour})");
        sb.AppendLine($"move        : {MoveText}");
        sb.AppendLine($"move number : {MoveNumber}");
        sb.AppendLine($"reported    : depth {Depth}, score {Score}, {Nodes} nodes, {ElapsedMs} ms of {BudgetMs} ms allowed");
        sb.AppendLine($"fen before  : {FenBeforeMove}");
        sb.AppendLine($"start fen   : {StartFen}");
        sb.AppendLine($"history     : {string.Join(' ', MoveHistory)}");
        sb.AppendLine($"legal moves : {string.Join(' ', LegalMoves)}");
        sb.AppendLine($"in legal set: {(LegalMoves.Contains(MoveText) ? "YES — notation/comparison defect" : "no — engine and arbiter disagree about the position")}");

        AppendTrace(sb, "protocol trace — mover", MoverTrace);
        AppendTrace(sb, "protocol trace — opponent", OpponentTrace);

        sb.AppendLine("===============================");
        return sb.ToString();
    }

    private static void AppendTrace(StringBuilder sb, string title, IReadOnlyList<string> trace)
    {
        sb.AppendLine($"--- {title} ---");
        if (trace.Count == 0)
        {
            sb.AppendLine("(none — this engine ran in-process and speaks no protocol)");
            return;
        }

        for (int i = 0; i < trace.Count; i++)
            sb.AppendLine(trace[i]);
    }

    /// <summary>
    /// Appends the report to <paramref name="path"/> and echoes it to stderr. Appended rather than
    /// overwritten so a run that produces several rejections keeps all of them, and echoed because
    /// a run that is being watched should not need its log files opened to show that something
    /// went wrong.
    /// </summary>
    public void Save(string path)
    {
        string text = Render();

        Console.Error.WriteLine(text);

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, text);
        }
        catch (IOException ex)
        {
            // The report has already been written to stderr; losing the file copy must not take
            // the run down on top of the rejection it was reporting.
            Console.Error.WriteLine($"(could not write rejected-move report to {path}: {ex.Message})");
        }
    }
}
