namespace ChessBot.Uci;

/// <summary>
/// The parsed operands of a UCI "go" command. Purely a transcription of what the GUI sent:
/// interpreting it (which clock applies, how much of it to spend) is
/// <see cref="UciTimeManager"/>'s job, so that the mapping from protocol to search settings
/// can be tested without a running search.
///
/// Unknown or unsupported operands ("ponder", "searchmoves") are ignored rather than rejected,
/// as the protocol requires: a GUI may send tokens an engine does not implement. Ignoring one
/// never makes a search unbounded by accident — see <see cref="IsUnbounded"/>.
/// </summary>
public sealed class GoParameters
{
    /// <summary>White's remaining clock in milliseconds ("wtime").</summary>
    public int? WhiteTimeMs { get; set; }

    /// <summary>Black's remaining clock in milliseconds ("btime").</summary>
    public int? BlackTimeMs { get; set; }

    /// <summary>White's increment per move in milliseconds ("winc").</summary>
    public int? WhiteIncrementMs { get; set; }

    /// <summary>Black's increment per move in milliseconds ("binc").</summary>
    public int? BlackIncrementMs { get; set; }

    /// <summary>Moves remaining until the next time control ("movestogo").</summary>
    public int? MovesToGo { get; set; }

    /// <summary>An exact per-move budget ("movetime"), which overrides any clock allocation.</summary>
    public int? MoveTimeMs { get; set; }

    /// <summary>Fixed search depth in plies ("depth").</summary>
    public int? Depth { get; set; }

    /// <summary>Fixed node budget ("nodes").</summary>
    public long? Nodes { get; set; }

    /// <summary>
    /// "mate": look for a mate in this many moves. Honoured as a depth bound of 2N-1 plies —
    /// the depth at which a mate in N is first visible — so the search ends on its own and
    /// answers, as the protocol expects of a bounded "go".
    /// </summary>
    public int? Mate { get; set; }

    /// <summary>
    /// "infinite": search until "stop". The distinction matters beyond the time budget —
    /// the protocol forbids sending bestmove before the stop arrives.
    /// </summary>
    public bool Infinite { get; set; }

    /// <summary>
    /// When the session received the "go" line, as a <see cref="System.Diagnostics.Stopwatch"/>
    /// timestamp. Not an operand — the session stamps it — but it travels with the command
    /// because it is the instant the GUI's clock started, and every budget is measured from it.
    /// </summary>
    public long? ReceivedTimestamp { get; set; }

    /// <summary>
    /// True when the command carried no operand that bounds the search in time. A bare "go" is
    /// treated as "go infinite", which is what the protocol prescribes.
    /// </summary>
    public bool HasNoTimeSource => MoveTimeMs is null && WhiteTimeMs is null && BlackTimeMs is null;

    /// <summary>
    /// True when nothing in the command bounds the search at all — no clock, no movetime, no
    /// depth, no node budget, no mate — and it did not say "infinite" either. A bare "go", or one
    /// carrying only operands this engine ignores ("go ponder", "go searchmoves e2e4").
    ///
    /// Such a search is treated as "go infinite": it runs until "stop" and holds its bestmove
    /// until then. The alternative, answering whenever the depth loop happens to run out, sends
    /// a bestmove nobody asked for at an arbitrary moment — in a quiet position, never; in a
    /// terminal one, instantly — and a GUI that sent a bare "go" for analysis takes an
    /// unsolicited bestmove as the end of it.
    /// </summary>
    public bool IsUnbounded =>
        !Infinite && HasNoTimeSource && Depth is null && Nodes is null && Mate is null;

    /// <summary>
    /// Parses the operands of a "go" command. <paramref name="tokens"/> is the whole command
    /// split on whitespace; <paramref name="startIndex"/> is the index just after the "go".
    /// </summary>
    public static GoParameters Parse(IReadOnlyList<string> tokens, int startIndex)
    {
        var go = new GoParameters();

        for (int i = startIndex; i < tokens.Count; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "infinite":  go.Infinite         = true;                         break;
                case "wtime":     go.WhiteTimeMs      = NextInt(tokens, ref i);        break;
                case "btime":     go.BlackTimeMs      = NextInt(tokens, ref i);        break;
                case "winc":      go.WhiteIncrementMs = NextInt(tokens, ref i);        break;
                case "binc":      go.BlackIncrementMs = NextInt(tokens, ref i);        break;
                case "movestogo": go.MovesToGo        = NextInt(tokens, ref i);        break;
                case "movetime":  go.MoveTimeMs       = NextInt(tokens, ref i);        break;
                case "depth":     go.Depth            = NextInt(tokens, ref i);        break;
                case "nodes":     go.Nodes            = NextLong(tokens, ref i);       break;
                case "mate":      go.Mate             = NextInt(tokens, ref i);        break;
            }
        }

        return go;
    }

    /// <summary>
    /// Consumes the value following a keyword, advancing the loop index past it. A missing or
    /// non-numeric value leaves the field unset instead of throwing: a malformed "go" should
    /// degrade to a search with fewer constraints, never take the engine down mid-game.
    /// </summary>
    private static int? NextInt(IReadOnlyList<string> tokens, ref int i)
    {
        if (i + 1 >= tokens.Count) return null;
        if (!int.TryParse(tokens[i + 1], out int value)) return null;
        i++;
        return value;
    }

    private static long? NextLong(IReadOnlyList<string> tokens, ref int i)
    {
        if (i + 1 >= tokens.Count) return null;
        if (!long.TryParse(tokens[i + 1], out long value)) return null;
        i++;
        return value;
    }
}
