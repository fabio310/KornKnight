namespace ChessBot.Uci;

using ChessBot.Engine;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;

/// <summary>
/// Drives a <see cref="ChessEngine"/> over the UCI protocol.
///
/// The session owns one game position, which "position" commands replace and "go" commands
/// search. Searching happens on a background thread for one protocol reason: "stop" and
/// "isready" must be answered while a search is running, so the command loop cannot be the
/// thread that is searching.
///
/// The reader and the writer are injected rather than read from <see cref="Console"/> so the
/// protocol can be driven from a test without a process.
/// </summary>
public sealed class UciSession : IDisposable
{
    /// <summary>Name reported to the GUI in the "uci" handshake.</summary>
    public const string EngineName = "KornKnight";

    /// <summary>Author reported to the GUI in the "uci" handshake.</summary>
    public const string EngineAuthor = "Fabio Kornfeld";

    /// <summary>
    /// Commit and build configuration this binary was produced from, e.g.
    /// <c>1.0.0+a1b2c3d4e5f6 (Release)</c>, or <c>… (Release, dirty)</c> when it was built from a
    /// working tree with uncommitted changes.
    ///
    /// Reported in the handshake because an A/B run compares two engine binaries, and a result
    /// is only traceable to two commits if each binary can say which commit it is. Asking git at
    /// measurement time answers a different question — what the harness's working tree is —
    /// which is routinely a third commit entirely.
    /// </summary>
    public static readonly string BuildIdentity = DescribeBuild();

    private static string DescribeBuild()
    {
        string version = System.Reflection.Assembly
            .GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";

        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        // The build stamps a ".dirty" suffix onto the commit id; it is pulled out into its own
        // word here so a reader sees it next to the configuration rather than buried in a hash.
        if (version.EndsWith(".dirty", StringComparison.Ordinal))
            return $"{version[..^".dirty".Length]} ({configuration}, dirty)";

        return $"{version} ({configuration})";
    }

    private const string StartPositionFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private readonly ChessEngine _engine;
    private readonly TextWriter  _out;

    // Every line the engine emits passes through here. Info lines are written from the search
    // thread while the command loop may be answering "readyok" on the main thread, so the two
    // must not interleave mid-line.
    private readonly object _writeLock = new();

    private Task?                    _searchTask;
    private CancellationTokenSource? _searchCts;

    private bool _disposed;

    /// <summary>
    /// Creates a session over the given engine, writing protocol output to
    /// <paramref name="output"/>.
    /// </summary>
    public UciSession(ChessEngine engine, TextWriter output)
    {
        _engine = engine;
        _out    = output;
    }

    /// <summary>
    /// Reads and executes commands until "quit" or end of input, then waits for any search
    /// still in flight so the process does not exit while a bestmove is being written.
    /// </summary>
    public void Run(TextReader input)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (!Execute(line))
                break;
        }

        StopSearch();
    }

    /// <summary>
    /// Executes a single command line. Returns false when the session should end ("quit").
    /// Unknown commands are ignored, as the protocol requires.
    /// </summary>
    public bool Execute(string line)
    {
        // Some hosts write a UTF-8 byte-order mark ahead of the first line they send, which
        // would otherwise turn the opening "uci" into an unrecognised command and leave the
        // GUI waiting for a handshake that never comes. Observed with a PowerShell-driven
        // process; harmless to strip in every case.
        string[] tokens = line.TrimStart('\uFEFF')
                              .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;

        switch (tokens[0].ToLowerInvariant())
        {
            case "uci":
                WriteLine($"id name {EngineName} {BuildIdentity}");
                WriteLine($"id author {EngineAuthor}");
                WriteLine("uciok");
                return true;

            case "isready":
                // Must be answered even mid-search, which is why it touches neither the board
                // nor the search thread.
                WriteLine("readyok");
                return true;

            case "ucinewgame":
                StopSearch();
                _engine.NewGame();
                return true;

            case "position":
                StopSearch();
                HandlePosition(tokens);
                return true;

            case "go":
                StartSearch(GoParameters.Parse(tokens, 1));
                return true;

            case "stop":
                StopSearch();
                return true;

            case "quit":
                StopSearch();
                return false;

            default:
                return true;
        }
    }

    // ── position ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies "position startpos [moves ...]" or "position fen &lt;fen&gt; [moves ...]".
    ///
    /// The moves are replayed onto the board rather than folded into a FEN, because the
    /// board's move history is what repetition detection reads: a position reconstructed from
    /// a bare FEN cannot know it has occurred before.
    /// </summary>
    private void HandlePosition(string[] tokens)
    {
        if (tokens.Length < 2)
            return;

        int movesIndex = IndexOfToken(tokens, "moves");
        int fenEnd     = movesIndex >= 0 ? movesIndex : tokens.Length;

        string fen;
        if (tokens[1].Equals("startpos", StringComparison.OrdinalIgnoreCase))
        {
            fen = StartPositionFen;
        }
        else if (tokens[1].Equals("fen", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildFen(tokens, 2, fenEnd, out fen))
            {
                WriteLine("info string ignoring position: malformed FEN");
                return;
            }
        }
        else
        {
            WriteLine($"info string ignoring position: expected 'startpos' or 'fen', got '{tokens[1]}'");
            return;
        }

        try
        {
            _engine.LoadFen(fen);
        }
        catch (Exception ex)
        {
            WriteLine($"info string ignoring position: {ex.Message}");
            return;
        }

        if (movesIndex < 0) return;

        for (int i = movesIndex + 1; i < tokens.Length; i++)
        {
            var legalMoves = _engine.GetLegalMoves();
            if (!UciMoveNotation.TryParse(tokens[i], legalMoves, out Move move))
            {
                // Stop replaying rather than skipping the move: every later move in the list
                // was made from a position this one produced, so continuing would build a
                // position that never occurred in the game.
                WriteLine($"info string illegal move in position command: {tokens[i]}");
                return;
            }

            _engine.MakeMove(move);
        }
    }

    /// <summary>
    /// Reassembles the FEN fields of a position command. A GUI may omit the halfmove clock and
    /// fullmove number; both are supplied as defaults, since the board parser requires all six
    /// fields and neither affects legality.
    /// </summary>
    private static bool TryBuildFen(string[] tokens, int start, int end, out string fen)
    {
        fen = string.Empty;
        int count = end - start;
        if (count < 4) return false;

        fen = string.Join(' ', tokens, start, count);
        if (count == 4)      fen += " 0 1";
        else if (count == 5) fen += " 1";

        return true;
    }

    private static int IndexOfToken(string[] tokens, string token)
    {
        for (int i = 0; i < tokens.Length; i++)
            if (tokens[i].Equals(token, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }

    // ── go / stop ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a search on a background thread. Any search still running is stopped first, so
    /// there is never more than one search — and never more than one pending bestmove.
    /// </summary>
    private void StartSearch(GoParameters go)
    {
        StopSearch();

        var settings = UciTimeManager.ToSearchSettings(go, _engine.SideToMove);
        settings.OnIterationComplete = WriteInfo;

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        bool holdBestMove = go.Infinite;
        _searchTask = Task.Run(() => RunSearch(settings, holdBestMove, cts.Token));
    }

    /// <summary>
    /// Cancels a running search and waits for it to finish. The wait is what guarantees the
    /// bestmove of the outgoing search is written before anything else happens — a "stop"
    /// followed immediately by "position" must not race the move it asked for.
    /// </summary>
    private void StopSearch()
    {
        var cts  = _searchCts;
        var task = _searchTask;

        cts?.Cancel();

        try
        {
            task?.Wait();
        }
        catch (AggregateException)
        {
            // A failed search has already reported itself on the info channel; the session
            // stays alive so the GUI is not left without an engine.
        }

        cts?.Dispose();
        _searchCts  = null;
        _searchTask = null;
    }

    private void RunSearch(SearchSettings settings, bool holdBestMove, CancellationToken ct)
    {
        try
        {
            var result = _engine.FindBestMove(settings, ct);

            // "go infinite" may not answer before "stop" arrives, even if the search ran out
            // of things to do (a forced mate ends iterative deepening early). Waiting on the
            // token is what keeps a mate-in-2 from producing an unsolicited bestmove.
            if (holdBestMove && !ct.IsCancellationRequested)
                ct.WaitHandle.WaitOne();

            WriteBestMove(result);
        }
        catch (Exception ex)
        {
            WriteLine($"info string search failed: {ex.Message}");
            WriteLine($"bestmove {UciMoveNotation.NullMove}");
        }
    }

    // ── Output ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits one "info" line per completed iteration. Called on the search thread from inside
    /// the search, so it copies nothing and holds nothing: the progress object is only valid
    /// for the duration of this call.
    /// </summary>
    private void WriteInfo(SearchProgress progress)
    {
        var line = new System.Text.StringBuilder(96);

        line.Append("info depth ").Append(progress.Depth)
            .Append(" seldepth ").Append(progress.SelDepth)
            .Append(" score ").Append(FormatScore(progress.Score))
            .Append(" nodes ").Append(progress.Nodes)
            .Append(" nps ").Append((long)progress.NodesPerSecond)
            .Append(" time ").Append(progress.ElapsedMs);

        var pv = progress.PrincipalVariation;
        if (pv.Count > 0)
        {
            line.Append(" pv");
            for (int i = 0; i < pv.Count; i++)
                line.Append(' ').Append(UciMoveNotation.Format(pv[i]));
        }

        WriteLine(line.ToString());
    }

    /// <summary>
    /// Formats a search score as UCI expects it: centipawns, or a distance in moves when the
    /// score is inside the mate band. Both are already from the side to move's point of view,
    /// which is the convention UCI uses. The classification itself belongs to
    /// <see cref="SearchScores.ToReported"/> and is shared with every other reporting surface.
    /// </summary>
    internal static string FormatScore(int score)
    {
        var reported = SearchScores.ToReported(score);
        return reported.MateInMoves is int mate ? $"mate {mate}" : $"cp {reported.Cp}";
    }

    /// <summary>
    /// Writes the bestmove line. A terminal position has no move to report; the protocol's
    /// "0000" is used rather than the search's default move value, which would name a square
    /// pair that is not a move at all.
    /// </summary>
    private void WriteBestMove(SearchResult result)
    {
        bool hasMove = !result.IsCheckmate && !result.IsStalemate;

        WriteLine(hasMove
            ? $"bestmove {UciMoveNotation.Format(result.BestMove)}"
            : $"bestmove {UciMoveNotation.NullMove}");
    }

    private void WriteLine(string text)
    {
        lock (_writeLock)
        {
            _out.WriteLine(text);
            _out.Flush();
        }
    }

    /// <summary>
    /// Stops any running search. Disposal does not close the writer: the session borrows it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSearch();
    }
}
