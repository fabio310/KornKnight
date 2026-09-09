using System.Text;
using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Uci;

namespace ChessBot.Tests;

/// <summary>
/// Drives the protocol layer directly, without a process. What is checked here is the
/// contract a GUI relies on: a handshake, one bestmove per go, a bestmove that is always
/// legal in the position the GUI set up, and info lines carrying the fields it parses.
/// </summary>
public class UciSessionTests
{
    /// <summary>
    /// Collects protocol output line by line. The search thread writes info lines while the
    /// test thread reads, so the buffer is locked rather than a plain StringWriter.
    /// </summary>
    private sealed class RecordingWriter : TextWriter
    {
        private readonly List<string> _lines = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_lines) _lines.Add(value ?? string.Empty);
        }

        public override void Write(char value)
        {
            lock (_lines) _lines.Add(value.ToString());
        }

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToArray(); }
        }

        public string? LastLineStartingWith(string prefix) =>
            Lines.LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));

        /// <summary>
        /// Waits for a line with the given prefix. Searches run on their own thread, so a test
        /// that asserts immediately after "go" would be asserting on an empty buffer.
        /// </summary>
        public string WaitForLine(string prefix, int timeoutMs = 15_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (LastLineStartingWith(prefix) is string line) return line;
                Thread.Sleep(5);
            }

            throw new TimeoutException(
                $"No '{prefix}' line within {timeoutMs}ms. Output:\n{string.Join('\n', Lines)}");
        }
    }

    private static (UciSession session, ChessEngine engine, RecordingWriter output) NewSession()
    {
        var engine = new ChessEngine();
        var output = new RecordingWriter();
        return (new UciSession(engine, output), engine, output);
    }

    // ── Handshake ────────────────────────────────────────────────────────────

    [Fact]
    public void Uci_AnswersWithIdentificationAndUciok()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            Assert.True(session.Execute("uci"));
        }

        Assert.Equal(
            new[] { $"id name {UciSession.EngineName}", $"id author {UciSession.EngineAuthor}", "uciok" },
            output.Lines);
    }

    [Fact]
    public void Uci_PrefixedWithAByteOrderMark_IsStillRecognised()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("﻿uci");
        }

        // A dropped handshake looks to the GUI like an engine that never started.
        Assert.Contains("uciok", output.Lines);
    }

    [Fact]
    public void IsReady_AnswersReadyok()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("isready");
        }

        Assert.Equal(new[] { "readyok" }, output.Lines);
    }

    [Fact]
    public void Quit_EndsTheSession()
    {
        var (session, _, _) = NewSession();
        using (session)
        {
            Assert.False(session.Execute("quit"));
        }
    }

    [Fact]
    public void UnknownCommand_IsIgnoredSilently()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            Assert.True(session.Execute("frobnicate the bishop"));
            Assert.True(session.Execute(""));
        }

        Assert.Empty(output.Lines);
    }

    [Fact]
    public void Run_StopsAtQuitAndIgnoresWhatFollows()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Run(new StringReader("uci\nquit\nisready\n"));
        }

        Assert.Contains("uciok", output.Lines);
        Assert.DoesNotContain("readyok", output.Lines);
    }

    // ── position ─────────────────────────────────────────────────────────────

    [Fact]
    public void Position_Startpos_LoadsTheStartingPosition()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            engine.LoadFen("8/8/8/8/8/8/8/K6k w - - 0 1");
            session.Execute("position startpos");
        }

        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", engine.ExportFen());
    }

    [Fact]
    public void Position_StartposWithMoves_ReplaysThemInOrder()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute("position startpos moves e2e4 e7e5 g1f3 b8c6");
        }

        Assert.Equal("r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3",
                     engine.ExportFen());
    }

    [Fact]
    public void Position_Fen_LoadsThatPosition()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute("position fen r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        }

        Assert.Equal("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
                     engine.ExportFen());
    }

    [Fact]
    public void Position_FenWithMoves_AppliesThemToThatPosition()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute("position fen 4k3/8/8/8/8/8/4P3/4K3 w - - 0 1 moves e2e4");
        }

        Assert.Equal("4k3/8/8/8/4P3/8/8/4K3 b - e3 0 1", engine.ExportFen());
    }

    [Fact]
    public void Position_FenMissingTheMoveCounters_IsAccepted()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            // Some GUIs send only the first four fields; neither counter affects legality.
            session.Execute("position fen 4k3/8/8/8/8/8/4P3/4K3 w - -");
        }

        Assert.StartsWith("4k3/8/8/8/8/8/4P3/4K3 w -", engine.ExportFen());
    }

    [Fact]
    public void Position_Castling_IsAppliedAsACastle()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute("position fen r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1 moves e1g1");
        }

        // The rook has to have moved too — proof the move type survived the notation.
        Assert.Equal("r3k2r/8/8/8/8/8/8/R4RK1 b kq - 1 1", engine.ExportFen());
    }

    [Fact]
    public void Position_EnPassant_IsAppliedAsACapture()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute("position fen 4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1 moves e5d6");
        }

        // The black d5 pawn is gone even though the capture square was empty.
        Assert.Equal("4k3/8/3P4/8/8/8/8/4K3 b - - 0 1", engine.ExportFen());
    }

    [Fact]
    public void Position_Promotion_PromotesToThePieceNamed()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute("position fen 4k3/1P6/8/8/8/8/8/4K3 w - - 0 1 moves b7b8n");
        }

        Assert.Equal("1N2k3/8/8/8/8/8/8/4K3 b - - 0 1", engine.ExportFen());
    }

    [Fact]
    public void Position_IllegalMove_IsReportedAndStopsTheReplay()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos moves e2e4 e7e5 e4e5 g1f3");
        }

        // e4e5 is blocked; the two legal moves before it stand, and the move after it is not
        // applied — it belongs to a position that never occurred.
        Assert.Equal("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2", engine.ExportFen());
        Assert.Contains(output.Lines, l => l.StartsWith("info string illegal move", StringComparison.Ordinal));
    }

    [Fact]
    public void Position_MalformedFen_LeavesThePositionUntouched()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos moves e2e4");
            string before = engine.ExportFen();

            session.Execute("position fen not-a-fen at all");

            Assert.Equal(before, engine.ExportFen());
        }

        Assert.Contains(output.Lines, l => l.StartsWith("info string ignoring position", StringComparison.Ordinal));
    }

    // ── go / bestmove / info ─────────────────────────────────────────────────

    [Fact]
    public void Go_ReportsExactlyOneBestMoveAndItIsLegal()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos moves e2e4 e7e5");
            session.Execute("go depth 4");
            output.WaitForLine("bestmove");
        }

        var bestMoveLines = output.Lines.Where(l => l.StartsWith("bestmove", StringComparison.Ordinal)).ToList();
        Assert.Single(bestMoveLines);

        string token = bestMoveLines[0].Split(' ')[1];
        Assert.True(UciMoveNotation.TryParse(token, engine.GetLegalMoves(), out _),
            $"bestmove '{token}' is not legal in the position the GUI set up");
    }

    [Fact]
    public void Go_InfoLinesCarryTheFieldsAGuiParses()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos");
            session.Execute("go depth 5");
            output.WaitForLine("bestmove");
        }

        string info = output.Lines.Last(l => l.StartsWith("info depth", StringComparison.Ordinal));

        foreach (string field in new[] { "depth", "seldepth", "score", "nodes", "nps", "time", "pv" })
            Assert.Contains($" {field} ", $"{info} ");

        // Depths are reported as they complete, so the deepest one comes last.
        Assert.StartsWith("info depth 5 ", info);
    }

    [Fact]
    public void Go_InfoPvIsAPlayableLineFromTheCurrentPosition()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos moves d2d4 d7d5");
            session.Execute("go depth 5");
            output.WaitForLine("bestmove");

            string info = output.Lines.Last(l => l.StartsWith("info depth", StringComparison.Ordinal));
            string[] tokens = info.Split(' ');
            var pv = tokens.Skip(Array.IndexOf(tokens, "pv") + 1).ToArray();

            Assert.NotEmpty(pv);

            // Every PV move must be legal in the position its predecessors produce, otherwise
            // the line a GUI displays is not a line at all.
            int applied = 0;
            foreach (string token in pv)
            {
                Assert.True(UciMoveNotation.TryParse(token, engine.GetLegalMoves(), out Move move),
                    $"PV move '{token}' is not legal at ply {applied}");
                engine.MakeMove(move);
                applied++;
            }

            for (int i = 0; i < applied; i++) engine.UndoMove();
        }
    }

    [Fact]
    public void Go_ForcedMate_IsReportedAsAMateScore()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            // White mates in one (the position has more than one mating move, so the score is
            // asserted rather than a particular move).
            session.Execute("position fen 6k1/Q7/6K1/8/8/8/8/8 w - - 0 1");
            session.Execute("go depth 3");
            output.WaitForLine("bestmove");
        }

        Assert.Contains(output.Lines, l => l.Contains("score mate 1", StringComparison.Ordinal));

        // And the move reported really does mate.
        string token = output.LastLineStartingWith("bestmove")!.Split(' ')[1];
        Assert.True(UciMoveNotation.TryParse(token, engine.GetLegalMoves(), out Move mateMove));

        engine.MakeMove(mateMove);
        Assert.Empty(engine.GetLegalMoves());
        Assert.True(engine.GetBoardSnapshot().IsKingInCheck(Color.Black));
    }

    [Fact]
    public void Go_InACheckmatedPosition_ReportsTheNullMove()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("position fen rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3");
            session.Execute("go depth 2");
            output.WaitForLine("bestmove");
        }

        // There is no move to name, and inventing one would be worse than saying so.
        Assert.Equal($"bestmove {UciMoveNotation.NullMove}", output.LastLineStartingWith("bestmove"));
    }

    [Fact]
    public void Go_WithANodeBudget_IsHonoured()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos");
            session.Execute("go nodes 20000");
            output.WaitForLine("bestmove");
        }

        string info = output.Lines.Last(l => l.StartsWith("info depth", StringComparison.Ordinal));
        long nodes  = long.Parse(info.Split(' ')[info.Split(' ').ToList().IndexOf("nodes") + 1]);

        Assert.True(nodes <= 20_000, $"reported {nodes} nodes against a 20000 budget");
    }

    [Fact]
    public void Go_RespectsTheMoveTimeBudget()
    {
        var (session, _, output) = NewSession();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        using (session)
        {
            session.Execute("position startpos");
            session.Execute("go movetime 300");
            output.WaitForLine("bestmove");
        }

        clock.Stop();

        // Generous upper bound: this asserts the budget is applied at all, not its precision.
        Assert.True(clock.ElapsedMilliseconds < 3_000,
            $"a 300ms search took {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Stop_EndsAnInfiniteSearchAndReturnsTheBestMoveFound()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos");
            session.Execute("go infinite");

            // Let the search produce at least one iteration, so the returned move is a
            // searched one rather than the unsearched fallback.
            output.WaitForLine("info depth");

            Assert.Empty(output.Lines.Where(l => l.StartsWith("bestmove", StringComparison.Ordinal)));

            session.Execute("stop");
        }

        string? bestMove = output.LastLineStartingWith("bestmove");
        Assert.NotNull(bestMove);

        string token = bestMove!.Split(' ')[1];
        Assert.True(UciMoveNotation.TryParse(token, engine.GetLegalMoves(), out _));
    }

    [Fact]
    public void Stop_WithNoSearchRunning_DoesNothing()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("stop");
        }

        // A stray bestmove here would be answered as if it belonged to the next go.
        Assert.Empty(output.Lines);
    }

    [Fact]
    public void IsReady_IsAnsweredWhileASearchIsRunning()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos");
            session.Execute("go infinite");
            output.WaitForLine("info depth");

            session.Execute("isready");
            Assert.Contains("readyok", output.Lines);

            session.Execute("stop");
        }
    }

    [Fact]
    public void NewGame_ResetsToTheStartingPosition()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute("position fen 4k3/8/8/8/8/8/8/4K3 w - - 0 1");
            session.Execute("ucinewgame");
        }

        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", engine.ExportFen());
    }

    [Fact]
    public void FormatScore_UsesCentipawnsOutsideTheMateBandAndMateDistanceInside()
    {
        Assert.Equal("cp 0",    UciSession.FormatScore(0));
        Assert.Equal("cp -137", UciSession.FormatScore(-137));

        // Mate delivered at ply 1 is mate in 1 for us; at ply 2, mate in 1 against us.
        Assert.Equal("mate 1",  UciSession.FormatScore(ChessBot.Engine.Search.SearchScores.Mate - 1));
        Assert.Equal("mate -1", UciSession.FormatScore(-ChessBot.Engine.Search.SearchScores.Mate + 2));
        Assert.Equal("mate 3",  UciSession.FormatScore(ChessBot.Engine.Search.SearchScores.Mate - 5));
    }
}
