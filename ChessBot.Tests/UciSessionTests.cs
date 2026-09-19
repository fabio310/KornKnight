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

        /// <summary>
        /// Waits for a line with the given prefix written after the first <paramref name="skip"/>
        /// lines — for a test that already has an older line with the same prefix in the buffer.
        /// </summary>
        public string WaitForLineAfter(int skip, string prefix, int timeoutMs = 15_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Lines.Skip(skip).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))
                    is string line) return line;
                Thread.Sleep(5);
            }

            throw new TimeoutException(
                $"No new '{prefix}' line within {timeoutMs}ms. Output:\n{string.Join('\n', Lines)}");
        }
    }

    private static (UciSession session, ChessEngine engine, RecordingWriter output) NewSession()
    {
        var engine = new ChessEngine();
        var output = new RecordingWriter();
        return (new UciSession(engine, output), engine, output);
    }

    // ── Options ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A GUI can only set an option the engine advertises, so the handshake has to name it with
    /// its type and range. Before this the engine advertised nothing at all.
    /// </summary>
    [Fact]
    public void Uci_AdvertisesMoveOverhead()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("uci");
        }

        string? line = output.Lines.FirstOrDefault(
            l => l.StartsWith("option name Move Overhead", StringComparison.Ordinal));

        Assert.NotNull(line);
        Assert.Contains("type spin", line);
        Assert.Contains($"default {UciTimeManager.DefaultMoveOverheadMs}", line);
        Assert.Contains($"max {UciTimeManager.MaxMoveOverheadMs}", line);

        // The option must be advertised before uciok, or a GUI that stops reading there never
        // sees it.
        int optionIndex = output.Lines.ToList().FindIndex(
            l => l.StartsWith("option name Move Overhead", StringComparison.Ordinal));
        int uciokIndex  = output.Lines.ToList().FindIndex(l => l == "uciok");
        Assert.InRange(optionIndex, 0, uciokIndex - 1);
    }

    /// <summary>
    /// The option name contains a space, which is the case a single-token parser gets wrong.
    /// </summary>
    [Fact]
    public void SetOption_ChangesTheMoveOverhead()
    {
        var (session, _, _) = NewSession();
        using (session)
        {
            Assert.Equal(UciTimeManager.DefaultMoveOverheadMs, session.MoveOverheadMs);

            session.Execute("setoption name Move Overhead value 250");

            Assert.Equal(250, session.MoveOverheadMs);
        }
    }

    /// <summary>
    /// Advertising an option and then ignoring it is worse than not having one. This drives the
    /// whole path — setoption, then a "go" with a clock — and asserts the budget actually moved
    /// by the amount the option changed.
    /// </summary>
    [Fact]
    public void TheMoveOverheadReachesTheTimeBudget()
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            var go = GoParameters.Parse(
                "go wtime 60000 btime 60000 movestogo 10".Split(' '), 1);

            int withDefault = UciTimeManager.ResolveTimeBudgetMs(
                go, engine.SideToMove, UciTimeManager.DefaultMoveOverheadMs);
            int withLarge = UciTimeManager.ResolveTimeBudgetMs(go, engine.SideToMove, 5000);

            // 60,000 ms over 10 moves: the overhead comes off the clock before the split, so
            // extra overhead costs a tenth of itself per move.
            Assert.Equal((60_000 - UciTimeManager.DefaultMoveOverheadMs) / 10, withDefault);
            Assert.Equal((60_000 - 5_000) / 10, withLarge);
        }
    }

    /// <summary>
    /// An operator can ask for more overhead than the clock has. The protocol has no error reply,
    /// so the value is clamped rather than refused, and a budget of at least 1 ms still comes out.
    /// </summary>
    [Theory]
    [InlineData("setoption name Move Overhead value 999999", UciTimeManager.MaxMoveOverheadMs)]
    [InlineData("setoption name Move Overhead value -5", 0)]
    [InlineData("setoption name Move Overhead value banana", UciTimeManager.DefaultMoveOverheadMs)]
    [InlineData("setoption name Nonexistent Option value 7", UciTimeManager.DefaultMoveOverheadMs)]
    [InlineData("setoption name Move Overhead", UciTimeManager.DefaultMoveOverheadMs)]
    [InlineData("setoption", UciTimeManager.DefaultMoveOverheadMs)]
    public void SetOption_SurvivesWhateverAGuiSends(string command, int expected)
    {
        var (session, _, _) = NewSession();
        using (session)
        {
            session.Execute(command);

            Assert.Equal(expected, session.MoveOverheadMs);
        }
    }

    /// <summary>
    /// A tester sets Hash to its conditions; an engine that ignores it is not testable. With no
    /// search running the table is rebuilt at once, so the memory is committed before the host's
    /// next isready is answered.
    /// </summary>
    [Theory]
    [InlineData("setoption name Hash value 16", 16)]
    [InlineData("setoption name hash value 8",  8)]    // option names are case-insensitive
    [InlineData("setoption name Hash value 0",  ChessEngine.MinHashSizeMb)]
    [InlineData("setoption name Hash value -7", ChessEngine.MinHashSizeMb)]
    [InlineData("setoption name Hash value lots", ChessEngine.DefaultHashSizeMb)]
    [InlineData("setoption name Hash value",    ChessEngine.DefaultHashSizeMb)]
    [InlineData("setoption name Hash",          ChessEngine.DefaultHashSizeMb)]
    public void SetOption_Hash_ResizesTheTable(string command, int expectedMb)
    {
        var (session, engine, _) = NewSession();
        using (session)
        {
            session.Execute(command);
            Assert.Equal(expectedMb, engine.HashSizeMb);
        }
    }

    /// <summary>
    /// Hash is set between games in practice, but nothing guarantees it. Resizing takes the lock
    /// the search holds, so applying it mid-search on the command loop would block the loop — and
    /// a "go infinite" ends only on a "stop" the blocked loop could never read. The change must
    /// return at once and take effect at the next ucinewgame.
    /// </summary>
    [Fact]
    public void SetOption_Hash_DuringASearch_NeitherBlocksNorRaces()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos");
            session.Execute("go infinite");
            output.WaitForLine("info depth");

            var setOption = Task.Run(() => session.Execute("setoption name Hash value 16"));
            Assert.True(setOption.Wait(2_000), "setoption Hash blocked the command loop mid-search");

            session.Execute("stop");
            session.Execute("ucinewgame");

            Assert.Equal(16, engine.HashSizeMb);
        }

        Assert.Single(output.Lines, l => l.StartsWith("bestmove", StringComparison.Ordinal));
    }

    /// <summary>
    /// Threads is advertised as exactly 1 and any value is accepted without effect or reply: a
    /// host that asks for 8 still gets a working single-threaded engine and nothing it has to
    /// parse.
    /// </summary>
    [Theory]
    [InlineData("setoption name Threads value 1")]
    [InlineData("setoption name Threads value 8")]
    [InlineData("setoption name Threads value 0")]
    [InlineData("setoption name Threads value many")]
    [InlineData("setoption name Threads")]
    public void SetOption_Threads_IsAcceptedAndIgnored(string command)
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute(command);
            Assert.Empty(output.Lines);

            session.Execute("position startpos");
            session.Execute("go depth 3");
            output.WaitForLine("bestmove");
        }
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

        // Exhaustive on purpose: the handshake is the one exchange a GUI parses strictly, and a
        // stray line here is how an engine ends up unusable in one GUI and fine in another.
        Assert.Equal(
            new[]
            {
                $"id name {UciSession.EngineName} {UciSession.BuildIdentity}",
                $"id author {UciSession.EngineAuthor}",
                "option name Hash type spin default 64 min 1 max 4096",
                "option name Threads type spin default 1 min 1 max 1",
                $"option name Move Overhead type spin default {UciTimeManager.DefaultMoveOverheadMs}" +
                    $" min 0 max {UciTimeManager.MaxMoveOverheadMs}",
                "uciok",
            },
            output.Lines);
    }

    /// <summary>
    /// An advertised option is a promise. Ponder would need ponderhit handling, and UCI_Chess960
    /// would need Chess960 castling — MoveGenerator.GenerateCastlingMoves is standard-only — and
    /// the engine has neither. A host that sees either offered will use it.
    /// </summary>
    [Theory]
    [InlineData("Ponder")]
    [InlineData("UCI_Chess960")]
    [InlineData("MultiPV")]
    [InlineData("UCI_LimitStrength")]
    public void Uci_DoesNotAdvertiseWhatItDoesNotImplement(string option)
    {
        var (session, _, output) = NewSession();
        using (session) session.Execute("uci");

        Assert.DoesNotContain(output.Lines,
            l => l.StartsWith($"option name {option} ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Uci_IdNameCarriesTheCommitAndConfigurationTheBinaryWasBuiltFrom()
    {
        // An A/B run compares two binaries, so a result is only traceable to two commits if each
        // binary says which commit it is. Asking git at measurement time describes the harness's
        // working tree instead, which is routinely a third commit.
        var (session, _, output) = NewSession();
        using (session) session.Execute("uci");

        string idName = output.Lines.Single(l => l.StartsWith("id name ", StringComparison.Ordinal));

        Assert.StartsWith($"id name {UciSession.EngineName} ", idName);
        Assert.Matches(@"\((Debug|Release)(, dirty)?\)$", idName);
    }

    /// <summary>
    /// A rating list files results under the reported name and version, so the version has to be
    /// a release version — major.minor.patch — ahead of the commit stamp, not a bare hash.
    /// </summary>
    [Fact]
    public void Uci_IdNameLeadsWithAReleaseVersion()
    {
        var (session, _, output) = NewSession();
        using (session) session.Execute("uci");

        string idName = output.Lines.Single(l => l.StartsWith("id name ", StringComparison.Ordinal));

        Assert.Matches($@"^id name {UciSession.EngineName} \d+\.\d+\.\d+\+([0-9a-f]{{12}}|nogit) ", idName);
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

        foreach (string field in new[] { "depth", "seldepth", "score", "nodes", "nps", "hashfull", "time", "pv" })
            Assert.Contains($" {field} ", $"{info} ");

        // Depths are reported as they complete, so the deepest one comes last.
        Assert.StartsWith("info depth 5 ", info);
    }

    /// <summary>
    /// hashfull is per-mille of the table in use, so it has to be in 0..1000, and it has to move:
    /// a small table under a long enough search fills, and a value stuck at 0 would mean the
    /// field is wired to nothing.
    /// </summary>
    [Fact]
    public void Go_HashfullIsPerMilleAndTracksTheTable()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("setoption name Hash value 1");
            session.Execute("position fen r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
            session.Execute("go depth 8");
            output.WaitForLine("bestmove", 60_000);
        }

        var values = output.Lines
            .Where(l => l.StartsWith("info depth", StringComparison.Ordinal))
            .Select(l => l.Split(' '))
            .Select(t => int.Parse(t[Array.IndexOf(t, "hashfull") + 1]))
            .ToList();

        Assert.All(values, v => Assert.InRange(v, 0, 1000));
        Assert.True(values[^1] > 0, "a 1 MB table after a depth-8 Kiwipete search reports 0 fill");
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

    /// <summary>
    /// The timing harness judges a clock-based search against the budget the engine says it
    /// allocated, so the announcement has to carry exactly the budget the search was given, and
    /// has to be absent when there is no budget rather than announce a sentinel.
    /// </summary>
    [Fact]
    public void Go_AnnouncesItsTimeBudget_AndOnlyWhenItHasOne()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos");

            var go = GoParameters.Parse("go wtime 60000 btime 60000 winc 600 binc 600".Split(' '), 1);
            int expected = UciTimeManager.ResolveTimeBudgetMs(go, engine.SideToMove);

            session.Execute("go wtime 60000 btime 60000 winc 600 binc 600");
            session.Execute("stop");
            Assert.Contains($"info string budget {expected}", output.Lines);

            int before = output.Lines.Count;
            session.Execute("go depth 2");
            session.Execute("stop");   // waits for the search, so its output is all in

            Assert.DoesNotContain(output.Lines.Skip(before),
                l => l.StartsWith("info string budget", StringComparison.Ordinal));
        }
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

    /// <summary>
    /// A bare "go" is "go infinite". The sharp case is a position whose search ends at once — a
    /// checkmate has no moves to search — where the old behaviour sent an unsolicited bestmove
    /// the instant the search returned.
    /// </summary>
    [Theory]
    [InlineData("go")]
    [InlineData("go ponder")]
    [InlineData("go searchmoves e2e4")]
    public void UnboundedGo_HoldsItsBestMoveUntilStop(string command)
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("position fen rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3");
            session.Execute(command);

            Thread.Sleep(300);
            Assert.DoesNotContain(output.Lines, l => l.StartsWith("bestmove", StringComparison.Ordinal));

            session.Execute("stop");
        }

        Assert.Single(output.Lines, l => l.StartsWith("bestmove", StringComparison.Ordinal));
    }

    /// <summary>
    /// "go mate N" is bounded, so it answers on its own — and with the mate.
    /// </summary>
    [Fact]
    public void GoMate_AnswersWithoutStop()
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute("position fen 6k1/Q7/6K1/8/8/8/8/8 w - - 0 1");
            session.Execute("go mate 1");
            output.WaitForLine("bestmove", 10_000);
        }

        string token = output.LastLineStartingWith("bestmove")!.Split(' ')[1];
        Assert.True(UciMoveNotation.TryParse(token, engine.GetLegalMoves(), out Move mateMove));
        engine.MakeMove(mateMove);
        Assert.Empty(engine.GetLegalMoves());
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

    // ── Hostile host ─────────────────────────────────────────────────────────
    // An exception escaping Execute reaches Run and ends the process; to a tournament manager
    // that is a forfeit, and on CCRL an engine that does it is dropped from testing. So every
    // shape of bad input a host can produce must leave a session that still answers and plays.

    public static IEnumerable<object[]> HostileSequences() => new[]
    {
        new object[] { "stop with no search running",   new[] { "stop", "stop" } },
        new object[] { "go with no preceding position", new[] { "go depth 3" } },
        new object[] { "setoption with a missing value", new[]
            { "setoption name Hash value", "setoption name Move Overhead value", "setoption name Threads value",
              "setoption name value 5", "setoption name", "setoption value 3", "setoption" } },
        new object[] { "position startpos moves <garbage>", new[]
            { "position startpos moves e2e4 zz99 e7e5", "position startpos moves !! @@ e2e4", "go depth 2" } },
        new object[] { "malformed position", new[]
            { "position", "position fen", "position fen 8/8/8/8 w", "position startpos moves",
              "position banana", "position fen rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - x y" } },
        new object[] { "go depth 0",             new[] { "position startpos", "go depth 0" } },
        new object[] { "go nodes 0",             new[] { "position startpos", "go nodes 0" } },
        new object[] { "go wtime 1 btime 1",     new[] { "position startpos", "go wtime 1 btime 1" } },
        new object[] { "a line of pure whitespace", new[] { "   ", "\t", " \t \t " } },
        new object[] { "operands at the int limits", new[]
            { "position startpos", "go movetime -2147483648", "go wtime -2147483648 btime -2147483648",
              "go wtime 2147483647 btime 2147483647 winc 2147483647 binc 2147483647",
              "go depth -5", "go nodes -1", "go movestogo 0 wtime 1000 btime 1000",
              "go movetime 99999999999999999999" } },
        new object[] { "operands with no values", new[]
            { "position startpos", "go wtime", "go movetime", "go depth nodes", "go mate" } },
        new object[] { "commands in the wrong order", new[]
            { "go depth 2", "ucinewgame", "go depth 2", "uci", "go depth 2", "isready", "position startpos" } },
    };

    [Theory]
    [MemberData(nameof(HostileSequences))]
    public void HostileInput_NeverThrows_AndLeavesAWorkingEngine(string name, string[] commands)
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            foreach (string command in commands)
            {
                Exception? thrown = Record.Exception(() => session.Execute(command));
                Assert.True(thrown is null, $"{name}: '{command}' threw {thrown}");
            }

            session.Execute("stop");

            int before = output.Lines.Count;
            session.Execute("isready");
            Assert.Contains("readyok", output.Lines.Skip(before));

            // Still plays: a fresh position and a bounded search give a legal move.
            session.Execute("position startpos moves e2e4");
            before = output.Lines.Count;
            session.Execute("go depth 3");
            string best = output.WaitForLineAfter(before, "bestmove");

            Assert.True(UciMoveNotation.TryParse(best.Split(' ')[1], engine.GetLegalMoves(), out _),
                $"{name}: '{best}' is not legal afterwards");
        }
    }

    /// <summary>
    /// The degenerate bounds each still answer, promptly and legally: depth 0 and nodes 0 cannot
    /// complete even depth 1, so they get the fallback move, but a move is what the GUI is owed.
    /// </summary>
    [Theory]
    [InlineData("go depth 0")]
    [InlineData("go nodes 0")]
    [InlineData("go wtime 1 btime 1")]
    [InlineData("go movetime 0")]
    [InlineData("go depth 3")]   // with no position ever sent: the start position
    public void DegenerateBounds_StillAnswerWithALegalMove(string command)
    {
        var (session, engine, output) = NewSession();
        using (session)
        {
            session.Execute(command);
            string best = output.WaitForLine("bestmove", 5_000);

            Assert.True(UciMoveNotation.TryParse(best.Split(' ')[1], engine.GetLegalMoves(), out _),
                $"'{command}' answered '{best}'");
        }

        Assert.Single(output.Lines, l => l.StartsWith("bestmove", StringComparison.Ordinal));
    }

    [Fact]
    public void Whitespace_ProducesNoOutput()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            Assert.True(session.Execute("   "));
            Assert.True(session.Execute("\t \t"));
        }

        Assert.Empty(output.Lines);
    }

    /// <summary>
    /// "quit" mid-search ends the session promptly: it stops the search rather than waiting for
    /// an infinite one to finish, and the bestmove it owes is still written before it returns.
    /// </summary>
    [Fact]
    public void Quit_MidSearch_EndsPromptly()
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            session.Execute("position startpos");
            session.Execute("go infinite");
            output.WaitForLine("info depth");

            var quit = Task.Run(() => session.Execute("quit"));
            Assert.True(quit.Wait(2_000), "quit did not return within 2 s of a running search");
            Assert.False(quit.Result);
        }

        Assert.Single(output.Lines, l => l.StartsWith("bestmove", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host that dies, or closes the pipe without "quit", must not leave an orphan process
    /// searching forever: end of input ends the session, and the pending search with it.
    /// </summary>
    [Theory]
    [InlineData("position startpos\ngo infinite\n")]
    [InlineData("position startpos\ngo movetime 60000\n")]
    [InlineData("uci\nisready\nposition startpos moves e2e4\ngo\n")]
    public void Run_InputEndingWithoutQuit_EndsTheSession(string input)
    {
        var (session, _, output) = NewSession();
        using (session)
        {
            var run = Task.Run(() => session.Run(new StringReader(input)));
            Assert.True(run.Wait(5_000), "Run did not return at end of input");
        }

        Assert.Single(output.Lines, l => l.StartsWith("bestmove", StringComparison.Ordinal));
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
