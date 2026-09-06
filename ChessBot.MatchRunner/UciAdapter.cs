namespace ChessBot.MatchRunner;

using System.Diagnostics;

/// <summary>
/// Manages a running UCI engine process.
/// Sends/receives UCI protocol messages via stdin/stdout.
/// Thread-safe for sequential move/response patterns.
/// </summary>
public sealed class UciAdapter : IDisposable
{
    private readonly string _enginePath;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _disposed;
    private readonly List<string> _handshakeLines = new();

    public string EngineName { get; private set; } = "Unknown";
    public bool IsRunning => _process is { HasExited: false };

    public UciAdapter(string enginePath)
    {
        _enginePath = enginePath;
    }

    /// <summary>
    /// Starts the engine process and performs the UCI handshake (uci → uciok, isready → readyok).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = _enginePath,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start engine: {_enginePath}");

        _stdin  = _process.StandardInput;
        _stdout = _process.StandardOutput;

        // UCI handshake
        await SendAsync("uci");
        await WaitForAsync("uciok", timeoutMs: 5000, ct);

        // Read engine name from uciok response
        await SendAsync("isready");
        await WaitForAsync("readyok", timeoutMs: 5000, ct);
    }

    /// <summary>
    /// Sets a UCI option (e.g., Hash size).
    /// </summary>
    public async Task SetOptionAsync(string name, string value)
    {
        await SendAsync($"setoption name {name} value {value}");
    }

    /// <summary>
    /// Sends "isready" and waits for "readyok" — used to make sure the engine has
    /// processed pending commands (e.g. setoption) before the next search.
    /// </summary>
    public async Task SyncAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        EnsureRunning();
        await SendAsync("isready");
        await WaitForAsync("readyok", timeoutMs, ct);
    }

    /// <summary>
    /// Asks the engine for the best move from the given FEN with move list applied,
    /// using a fixed move-time budget.
    /// Returns the best move in UCI format (e.g., "e2e4", "e7e8q") and any info lines.
    /// </summary>
    public async Task<UciMoveResult> GetBestMoveAsync(
        string fen,
        IReadOnlyList<string> moves,
        int moveTimeMs,
        CancellationToken ct = default)
    {
        EnsureRunning();

        // Build position command
        string posCmd = moves.Count > 0
            ? $"position fen {fen} moves {string.Join(' ', moves)}"
            : $"position fen {fen}";

        await SendAsync(posCmd);
        await SendAsync($"go movetime {moveTimeMs}");

        return await ReadUntilBestMoveAsync(timeoutMs: moveTimeMs + 5000, ct);
    }

    /// <summary>
    /// Analyses a position to a fixed depth and returns the engine's score for it.
    ///
    /// <paramref name="restrictToMove"/> maps to UCI "searchmoves": when set, the engine is
    /// forced to search only that root move, so its score is the value of *that* move rather
    /// than of the position. Analysing the same position twice — once unrestricted, once
    /// restricted to the move actually played — yields both sides of a move-loss measurement
    /// from one pre-move position, on one engine, on one scale.
    ///
    /// <paramref name="moveHistory"/>, when supplied together with <paramref name="fen"/> as the
    /// *initial* position, reconstructs the position via "position fen &lt;fen&gt; moves ..."
    /// instead of "position fen &lt;fen-at-this-ply&gt;" directly. A standalone FEN has no
    /// repetition history (the board state before any single position does not record how many
    /// times that position was reached before), so an engine analysing a bare mid-game FEN can
    /// never correctly detect or avoid a repetition draw that the actual game history would show.
    ///
    /// Clears the hash first so that a fixed depth gives a reproducible score independent of
    /// what was analysed before.
    /// </summary>
    public async Task<UciMoveResult> AnalyzeFenAsync(
        string fen,
        int depth,
        string? restrictToMove = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? moveHistory = null)
    {
        EnsureRunning();

        await SendAsync("ucinewgame");
        await SyncAsync(ct: ct);

        string posCmd = moveHistory is { Count: > 0 }
            ? $"position fen {fen} moves {string.Join(' ', moveHistory)}"
            : $"position fen {fen}";
        await SendAsync(posCmd);

        string go = $"go depth {depth}";
        if (!string.IsNullOrWhiteSpace(restrictToMove))
            go += $" searchmoves {restrictToMove}";

        await SendAsync(go);

        return await ReadUntilBestMoveAsync(timeoutMs: 120_000, ct);
    }

    /// <summary>
    /// Returns the engine's reported id/option lines from the handshake, so an analysis run
    /// can record the exact configuration it used instead of describing it loosely.
    /// </summary>
    public IReadOnlyList<string> HandshakeLines => _handshakeLines;

    /// <summary>
    /// Sends the "ucinewgame" command to reset engine state between games.
    /// </summary>
    public async Task NewGameAsync()
    {
        EnsureRunning();
        await SendAsync("ucinewgame");
        await SendAsync("isready");
        await WaitForAsync("readyok", timeoutMs: 5000);
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private async Task SendAsync(string command)
    {
        if (_stdin is null) throw new InvalidOperationException("Engine not started.");
        await _stdin.WriteLineAsync(command);
        await _stdin.FlushAsync();
    }

    private async Task WaitForAsync(string expectedToken, int timeoutMs, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        while (!cts.Token.IsCancellationRequested)
        {
            string? line = await ReadLineWithTimeoutAsync(cts.Token);
            if (line is null) break;

            // Capture engine name from "id name ..."
            if (line.StartsWith("id name ", StringComparison.OrdinalIgnoreCase))
                EngineName = line[8..].Trim();

            // Keep id/option/info-string lines so a run can record the engine's actual
            // reported configuration (threads, hash, NNUE nets) rather than asserting it.
            if (line.StartsWith("id ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("option name ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("info string ", StringComparison.OrdinalIgnoreCase))
                _handshakeLines.Add(line);

            if (line.StartsWith(expectedToken, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new TimeoutException($"Engine did not respond with '{expectedToken}' within {timeoutMs}ms.");
    }

    private async Task<UciMoveResult> ReadUntilBestMoveAsync(int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var result = new UciMoveResult();

        while (!cts.Token.IsCancellationRequested)
        {
            string? line = await ReadLineWithTimeoutAsync(cts.Token);
            if (line is null) break;

            if (line.StartsWith("info "))
            {
                result.InfoLines.Add(line);
                ParseInfoLine(line, result);
            }
            else if (line.StartsWith("bestmove "))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                result.BestMove = parts.Length >= 2 ? parts[1] : "(none)";

                // "bestmove (none)" means no legal moves
                if (result.BestMove is "(none)" or "0000")
                    result.BestMove = string.Empty;

                return result;
            }
        }

        throw new TimeoutException($"Engine did not return 'bestmove' within {timeoutMs}ms.");
    }

    private static void ParseInfoLine(string line, UciMoveResult result)
    {
        // Parses every token defined by the UCI protocol:
        // depth seldepth multipv score(cp/mate/lowerbound/upperbound)
        // nodes nps hashfull tbhits time currmove currmovenumber pv
        //
        // Every "info" line that carries a "score" token is a *complete* new score report —
        // it fully replaces whatever score was reported by an earlier line, it is never a
        // partial update. Without resetting ScoreMate/ScoreBound before parsing, a mate score
        // or a lowerbound/upperbound qualifier from an earlier (shallower) iteration would
        // silently persist onto a later line that reports a plain exact centipawn score,
        // corrupting the final result with a stale mate flag or stale bound.
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (Array.IndexOf(parts, "score") >= 0)
        {
            result.ScoreCp    = 0;
            result.ScoreMate  = null;
            result.ScoreBound = "exact";
        }

        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "depth" when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out int d))    result.Depth    = d;    break;
                case "seldepth" when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out int sd))   result.SelDepth = sd;   break;
                case "multipv" when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out int mpv))  result.MultiPv  = mpv;  break;
                case "cp" when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out int cp))   result.ScoreCp  = cp;   break;
                case "mate" when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out int m))    result.ScoreMate = m;   break;
                case "lowerbound":
                    result.ScoreBound = "lowerbound";  break;
                case "upperbound":
                    result.ScoreBound = "upperbound";  break;
                case "nodes" when i + 1 < parts.Length:
                    if (long.TryParse(parts[i + 1], out long n))  result.Nodes    = n;    break;
                case "nps" when i + 1 < parts.Length:
                    if (long.TryParse(parts[i + 1], out long nps))result.Nps      = nps;  break;
                case "hashfull" when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out int hf))   result.HashFull = hf;   break;
                case "tbhits" when i + 1 < parts.Length:
                    if (long.TryParse(parts[i + 1], out long tb)) result.TbHits   = tb;   break;
                case "time" when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out int t))    result.TimeMs   = t;    break;
                case "pv" when i + 1 < parts.Length:
                    result.Pv = string.Join(' ', parts[(i + 1)..]);
                    return; // pv is always last — stop here
            }
        }
    }

    private async Task<string?> ReadLineWithTimeoutAsync(CancellationToken ct)
    {
        if (_stdout is null) return null;
        try
        {
            return await _stdout.ReadLineAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void EnsureRunning()
    {
        if (!IsRunning)
            throw new InvalidOperationException("Engine process is not running.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (IsRunning)
            {
                _stdin?.WriteLine("quit");
                _stdin?.Flush();
                if (!_process!.WaitForExit(2000))
                    _process.Kill();
            }
        }
        catch { /* ignore cleanup errors */ }
        _process?.Dispose();
    }
}

/// <summary>
/// Result of a single engine UCI move request.
/// Captures every field emitted on UCI info lines.
/// </summary>
public class UciMoveResult
{
    public string BestMove   { get; set; } = string.Empty;
    // ── Search depth ─────────────────────────────────────────────────────────
    public int    Depth      { get; set; }
    public int    SelDepth   { get; set; }   // selective depth
    public int    MultiPv    { get; set; } = 1; // multipv index
    // ── Score ─────────────────────────────────────────────────────────────────
    public int    ScoreCp    { get; set; }
    public int?   ScoreMate  { get; set; }   // mate in N (negative = getting mated)
    public string ScoreBound { get; set; } = "exact"; // "exact" | "lowerbound" | "upperbound"
    // ── Search stats ─────────────────────────────────────────────────────────
    public long   Nodes      { get; set; }
    public long   Nps        { get; set; }
    public int    TimeMs     { get; set; }
    public int    HashFull   { get; set; } = -1; // TT fill in per-mille (0-1000); -1 = not reported
    public long   TbHits     { get; set; }   // tablebase hits
    // ── Move / variation ─────────────────────────────────────────────────────
    public string Pv         { get; set; } = string.Empty;
    // ── Raw output ───────────────────────────────────────────────────────────
    public List<string> InfoLines { get; } = new(); // every raw UCI "info ..." line

    /// <summary>Human-readable score label (e.g. "+42cp", "mate 3", "-15cp [lowerbound]").</summary>
    public string ScoreLabel
    {
        get
        {
            string base_ = ScoreMate.HasValue ? $"mate {ScoreMate}" : $"{ScoreCp:+#;-#;0}cp";
            return ScoreBound == "exact" ? base_ : $"{base_} [{ScoreBound}]";
        }
    }
}
