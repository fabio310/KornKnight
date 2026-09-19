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

    /// <summary>
    /// Time the time manager sets aside for everything that is not search. A UCI option because
    /// the right value is a property of the host — a local pipe and a network game against a
    /// remote GUI are not the same number — and the operator is the only one in a position to
    /// know it. The default is the value that was compiled in before, so a host that sets nothing
    /// gets exactly the old behaviour.
    /// </summary>
    private int _moveOverheadMs = UciTimeManager.DefaultMoveOverheadMs;

    /// <summary>
    /// A Hash size that arrived while a search was running, waiting to be applied. Resizing takes
    /// the engine's board lock, which the search holds; doing it on the command loop mid-search
    /// would block the loop until the search ended — and a "go infinite" only ends on a "stop"
    /// the blocked loop could never read. So it waits for the next point where no search is
    /// running: the next ucinewgame, position or go, each of which stops the search first.
    /// </summary>
    private int? _pendingHashMb;

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

                // Only options the engine honours. No Ponder: there is no ponderhit handling.
                // No UCI_Chess960: castling generation is standard-only. Advertising either would
                // invite a host to rely on something that is not there.
                WriteLine($"option name Hash type spin default {ChessEngine.DefaultHashSizeMb} " +
                          $"min {ChessEngine.MinHashSizeMb} max {ChessEngine.MaxHashSizeMb}");
                WriteLine("option name Threads type spin default 1 min 1 max 1");
                WriteLine($"option name Move Overhead type spin default " +
                          $"{UciTimeManager.DefaultMoveOverheadMs} min 0 " +
                          $"max {UciTimeManager.MaxMoveOverheadMs}");
                WriteLine("uciok");
                return true;

            case "setoption":
                HandleSetOption(tokens);
                return true;

            case "isready":
                // Must be answered even mid-search, which is why it touches neither the board
                // nor the search thread.
                WriteLine("readyok");
                return true;

            case "ucinewgame":
                StopSearch();
                ApplyPendingHashSize();
                _engine.NewGame();
                return true;

            case "position":
                StopSearch();
                ApplyPendingHashSize();
                HandlePosition(tokens);
                return true;

            case "go":
            {
                // First, before anything that takes time: the GUI started its clock when it
                // wrote this line, and StartSearch begins by waiting for any previous search to
                // finish. Every budget is measured from here, so none of that wait is free.
                long receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();

                var go = GoParameters.Parse(tokens, 1);
                go.ReceivedTimestamp = receivedAt;
                StartSearch(go);
                return true;
            }

            case "stop":
                StopSearch();
                return true;

            case "bench":
                // Not a UCI command. It is here because this is the only place that already
                // owns an engine and a writer, and because the node-rate regression it
                // measures has to be measurable from the shipped binary rather than from a
                // test host — the test host is a different process with a different GC
                // configuration, and those are two different numbers.
                StopSearch();
                HandleBench(tokens);
                return true;

            case "quit":
                StopSearch();
                return false;

            default:
                return true;
        }
    }

    // ── setoption ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies "setoption name &lt;name&gt; [value &lt;value&gt;]".
    ///
    /// An option name may contain spaces — "Move Overhead" does — so the name is everything
    /// between "name" and "value" rather than a single token. An unknown option, a missing value
    /// and a value that will not parse are all ignored rather than reported: the protocol has no
    /// error reply for them, and a GUI that sends an option this engine has never heard of must
    /// not be left waiting.
    /// </summary>
    private void HandleSetOption(string[] tokens)
    {
        int nameIndex  = IndexOfToken(tokens, "name");
        int valueIndex = IndexOfToken(tokens, "value");
        if (nameIndex < 0) return;

        int nameEnd = valueIndex >= 0 ? valueIndex : tokens.Length;
        if (nameEnd <= nameIndex + 1) return;

        string name  = string.Join(' ', tokens[(nameIndex + 1)..nameEnd]);
        string value = valueIndex >= 0 && valueIndex + 1 < tokens.Length
            ? string.Join(' ', tokens[(valueIndex + 1)..])
            : string.Empty;

        bool isInteger = int.TryParse(value, System.Globalization.NumberStyles.Integer,
                                      System.Globalization.CultureInfo.InvariantCulture, out int number);

        if (name.Equals("Move Overhead", StringComparison.OrdinalIgnoreCase) && isInteger)
        {
            _moveOverheadMs = Math.Clamp(number, 0, UciTimeManager.MaxMoveOverheadMs);
        }
        else if (name.Equals("Hash", StringComparison.OrdinalIgnoreCase) && isInteger)
        {
            _pendingHashMb = Math.Clamp(number, ChessEngine.MinHashSizeMb, ChessEngine.MaxHashSizeMb);

            // Now if nothing is searching, so the memory is committed before the host's next
            // "isready" is answered; otherwise at the next point where no search is running.
            if (_searchTask is null || _searchTask.IsCompleted)
                ApplyPendingHashSize();
        }

        // "Threads" is accepted and ignored, whatever the value: the search is single-threaded,
        // and the advertised range (min 1 max 1) is what says so. Advertising it at all is for
        // hosts that send it unconditionally, lichess-bot among them — an honest range documents
        // the limit, where silently ignoring an unknown option documents nothing.
    }

    /// <summary>
    /// Resizes the transposition table if a Hash change is waiting. Only called where no search
    /// is running. A size that cannot be allocated is reported and dropped; the engine keeps the
    /// table it had rather than being left without one.
    /// </summary>
    private void ApplyPendingHashSize()
    {
        if (_pendingHashMb is not int hashMb) return;
        _pendingHashMb = null;

        try
        {
            _engine.SetHashSize(hashMb);
        }
        catch (OutOfMemoryException)
        {
            WriteLine($"info string cannot allocate {hashMb} MB for Hash; keeping {_engine.HashSizeMb} MB");
        }
    }

    /// <summary>The move overhead currently in effect, for tests.</summary>
    internal int MoveOverheadMs => _moveOverheadMs;

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
        ApplyPendingHashSize();

        var settings = UciTimeManager.ToSearchSettings(go, _engine.SideToMove, _moveOverheadMs);
        settings.OnIterationComplete = WriteInfo;

        // Announced so a harness can judge a clock-based search against what the engine meant
        // to spend (scripts/timing-probe.ps1). A clock implies no single budget of its own, so
        // without this line an overshoot under a real time control cannot even be defined.
        // Unbounded searches have no budget to announce.
        if (settings.MaxTimeMs is int budgetMs && budgetMs != UciTimeManager.NoTimeLimitMs)
            WriteLine($"info string budget {budgetMs}");

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        // A "go" with nothing bounding it is "go infinite" (see GoParameters.IsUnbounded): its
        // bestmove waits for "stop" rather than going out whenever the depth loop runs dry.
        bool holdBestMove = go.Infinite || go.IsUnbounded;
        if (go.IsUnbounded)
            WriteLine("info string no limit given; searching until stop");
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

    // ── bench ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs "bench [depth] [hashMb]" on this thread and writes the report.
    ///
    /// On this thread deliberately: the command loop has nothing else to answer while a
    /// measurement is running, and running it on the search thread would put the bench in
    /// competition with whatever the thread pool is doing to service the reader — which is
    /// noise in the one number the command exists to produce.
    ///
    /// It runs on its own engine rather than this session's. The bench must not depend on what
    /// position the host happens to have loaded or on what the session's tables have learned,
    /// and the session must not silently lose its game because someone asked for a
    /// measurement.
    /// </summary>
    private void HandleBench(string[] tokens)
    {
        int depth  = ParseArgument(tokens, 1, Bench.DefaultDepth);
        int hashMb = ParseArgument(tokens, 2, ChessEngine.DefaultHashSizeMb);

        var report = new StringWriter();
        var summary = Bench.Run(report, depth, hashMb);

        // Written as one block under the write lock, so a bench report cannot be interleaved
        // line by line with anything else this session emits.
        lock (_writeLock)
        {
            _out.Write(report.ToString());
            _out.Flush();
        }

        _lastBench = summary;
    }

    /// <summary>The totals of the last bench run in this session, for tests.</summary>
    internal BenchSummary? LastBench => _lastBench;

    private BenchSummary? _lastBench;

    /// <summary>
    /// Reads a positional integer argument, falling back to <paramref name="fallback"/> when it
    /// is absent or will not parse. A bench with a mistyped argument runs at the default rather
    /// than not running, which matches how the rest of this session treats malformed input.
    /// </summary>
    private static int ParseArgument(string[] tokens, int index, int fallback) =>
        index < tokens.Length
        && int.TryParse(tokens[index], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

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
            .Append(" hashfull ").Append(progress.HashFull)
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
