using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// Validates the benchmark pipeline by replaying recorded game PGNs move-by-move.
/// Every move must be legal, and the resulting FEN must be consistent at each step.
/// These tests catch regressions in move generation, Board.MakeMove, FEN export/import,
/// castling, en passant, promotion, and side-to-move handling.
/// </summary>
public class PgnReplayTests
{
    // ── Game 1 (ChessBot as White) ──────────────────────────────────────────
    // Source: pgns/game1_20260620_132409.pgn  (79 plies, White wins by checkmate)
    // NOTE: The final ply (a3b4) is incoherent from the starting position because the
    // old GameRunner sent Stockfish double-applied positions (the UCI position bug).
    // The first 78 plies form a legal sequence; only the 79th is corrupted.
    private const string Game1Pgn =
        "g1f3 d7d5 b1c3 d5d4 c3e4 e7e5 c2c3 b8c6 c3d4 e5d4 d1c2 g8f6 e2e3 c8f5 " +
        "f1d3 c6b4 e4f6 d8f6 c2a4 f5d7 a4b3 e8c8 f3d4 c7c5 d4f3 d7h3 d3f1 h3f5 " +
        "d2d3 f5d3 f1d3 b4d3 e1f1 g7g5 h2h3 f8g7 e3e4 h8e8 c1g5 f6b2 b3b2 g7b2 " +
        "g5d8 b2a1 d8g5 e8e4 g5e3 a1g7 f3d2 e4a4 f1e2 d3f4 e3f4 a4f4 g2g3 f4a4 " +
        "h1c1 b7b6 c1c2 c8d7 f2f4 d7c6 d2c4 g7d4 h3h4 b6b5 c4e3 d4e3 e2e3 f7f5 " +
        "h4h5 a4a3 e3f2 a7a5 f2g2 b5b4 h5h6 c6b5 a3b4";

    // All legal plies from game1 (excludes the last corrupted move)
    private const string Game1PgnLegal78 =
        "g1f3 d7d5 b1c3 d5d4 c3e4 e7e5 c2c3 b8c6 c3d4 e5d4 d1c2 g8f6 e2e3 c8f5 " +
        "f1d3 c6b4 e4f6 d8f6 c2a4 f5d7 a4b3 e8c8 f3d4 c7c5 d4f3 d7h3 d3f1 h3f5 " +
        "d2d3 f5d3 f1d3 b4d3 e1f1 g7g5 h2h3 f8g7 e3e4 h8e8 c1g5 f6b2 b3b2 g7b2 " +
        "g5d8 b2a1 d8g5 e8e4 g5e3 a1g7 f3d2 e4a4 f1e2 d3f4 e3f4 a4f4 g2g3 f4a4 " +
        "h1c1 b7b6 c1c2 c8d7 f2f4 d7c6 d2c4 g7d4 h3h4 b6b5 c4e3 d4e3 e2e3 f7f5 " +
        "h4h5 a4a3 e3f2 a7a5 f2g2 b5b4 h5h6 c6b5";

    private const string Game1FinalFen = null; // not checked — only legality of each ply matters

    // ── Game 2 (ChessBot as Black) ──────────────────────────────────────────
    // Source: pgns/game2_20260620_132439.pgn  (aborted at move 8 in the old run)
    // The moves up to the abort are fully legal from the starting position — the
    // abort was caused by the GameRunner sending a wrong position to Stockfish,
    // not by a board bug. All 15 plies (7 full moves + Black's 8th half-move) must replay.
    private const string Game2PgnBeforeAbort =
        "e2e4 b8c6 d2d4 g8f6 d4d5 c6e5 f2f4 e5g6 e4e5 f6g8 f1d3 e7e6 d1e2 e6d5";

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void ReplayMoves(string moveList)
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        string[] moves = moveList.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < moves.Length; i++)
        {
            string uci = moves[i];
            var legal = engine.GetLegalMoves();

            // The move must appear in the legal move list (matched by UCI string).
            Move? matched = null;
            foreach (var m in legal)
            {
                if (m.ToString().Equals(uci, StringComparison.OrdinalIgnoreCase))
                {
                    matched = m;
                    break;
                }
            }

            Assert.True(matched.HasValue,
                $"Ply {i + 1}: move '{uci}' is not legal. " +
                $"FEN before: {engine.ExportFen()}. " +
                $"Legal moves: {string.Join(", ", legal.Select(m => m.ToString()))}");

            engine.MakeMove(matched!.Value);

            // After every half-move the FEN must be parseable and round-trip cleanly.
            string fen = engine.ExportFen();
            Assert.False(string.IsNullOrWhiteSpace(fen),
                $"Ply {i + 1}: ExportFen returned empty after '{uci}'.");

            // Verify round-trip FEN consistency: load-export must be stable.
            var verify = new ChessEngine();
            verify.LoadFen(fen);
            string roundTrip = verify.ExportFen();
            Assert.Equal(fen, roundTrip);
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReplayGame1_First78PliesAreAllLegal()
    {
        // The stored game1 PGN was generated while the UCI position bug was active;
        // the final ply (a3b4) is incoherent from the start position.  All 78 prior
        // plies must be legal — this validates move generation and board correctness.
        ReplayMoves(Game1PgnLegal78);
    }

    [Fact]
    public void ReplayGame1_LastPlyIsKnownCorruption()
    {
        // Confirms the known corruption: ply 79 (a3b4) is NOT legal from the start
        // position when replaying correctly.  This is expected because the old
        // GameRunner had the double-apply UCI bug; the PGN was recorded from wrong
        // Stockfish positions.  Once fresh benchmark games are run with the fix in
        // place, this test should be retired.
        string[] allMoves = Game1Pgn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        // Replay the clean 78 plies
        for (int i = 0; i < allMoves.Length - 1; i++)
        {
            var legal = engine.GetLegalMoves();
            Move? m = null;
            foreach (var lm in legal)
            {
                if (lm.ToString().Equals(allMoves[i], StringComparison.OrdinalIgnoreCase))
                { m = lm; break; }
            }
            Assert.True(m.HasValue, $"Ply {i + 1}: unexpected illegal move '{allMoves[i]}' before the known-corrupt final ply.");
            engine.MakeMove(m!.Value);
        }

        // The 79th move must NOT be legal (it's the corrupted final ply).
        string corruptedMove = allMoves[^1];
        var legalFinal = engine.GetLegalMoves();
        bool foundCorrupted = false;
        foreach (var lm in legalFinal)
        {
            if (lm.ToString().Equals(corruptedMove, StringComparison.OrdinalIgnoreCase))
            { foundCorrupted = true; break; }
        }
        Assert.False(foundCorrupted,
            $"Ply 79: move '{corruptedMove}' unexpectedly became legal — "
            + "it should remain illegal unless new benchmark games are recorded. "
            + "If the game was replayed from fixed benchmark data, retire this test.");
    }

    [Fact]
    public void ReplayGame2_MovesBeforeAbortAreAllLegal()
    {
        // The abort was caused by the GameRunner sending the wrong position to Stockfish,
        // not by a board illegality. Every move up to the abort should replay cleanly.
        ReplayMoves(Game2PgnBeforeAbort);
    }

    [Fact]
    public void ReplayGame1_SideToMoveAlternates()
    {
        // Use only the 78 clean plies to verify side-to-move correctness.
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        string[] moves = Game1PgnLegal78.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < moves.Length; i++)
        {
            Color expected = i % 2 == 0 ? Color.White : Color.Black;
            Assert.Equal(expected, engine.GetBoardSnapshot().State.ActiveColor);

            var legal = engine.GetLegalMoves();
            Move? m = null;
            foreach (var lm in legal)
            {
                if (lm.ToString().Equals(moves[i], StringComparison.OrdinalIgnoreCase))
                { m = lm; break; }
            }
            Assert.True(m.HasValue, $"Ply {i + 1}: move '{moves[i]}' not found in legal moves.");
            engine.MakeMove(m!.Value);
        }
    }

    [Fact]
    public void ReplayGame1_EnPassantAndCastlingRoundTrip()
    {
        // Verifies that FEN export/import round-trips correctly for positions that
        // have en passant squares and castling rights changes.
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        string[] moves = Game1PgnLegal78.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var uci in moves)
        {
            var legal = engine.GetLegalMoves();
            Move? m = null;
            foreach (var lm in legal)
            {
                if (lm.ToString().Equals(uci, StringComparison.OrdinalIgnoreCase))
                { m = lm; break; }
            }
            if (!m.HasValue) break; // Stop on first illegal (test ReplayGame1_First78PliesAreAllLegal catches that)
            engine.MakeMove(m!.Value);

            string fen = engine.ExportFen();
            var v = new ChessEngine();
            v.LoadFen(fen);
            // State must survive FEN round-trip
            Assert.Equal(engine.GetBoardSnapshot().State.ActiveColor,
                         v.GetBoardSnapshot().State.ActiveColor);
            Assert.Equal(engine.GetBoardSnapshot().State.CastlingRights,
                         v.GetBoardSnapshot().State.CastlingRights);
            Assert.Equal(engine.GetBoardSnapshot().State.HasEnPassant,
                         v.GetBoardSnapshot().State.HasEnPassant);
            if (engine.GetBoardSnapshot().State.HasEnPassant)
            {
                Assert.Equal(engine.GetBoardSnapshot().State.EnPassantTarget,
                             v.GetBoardSnapshot().State.EnPassantTarget);
            }
        }
    }

    [Fact]
    public void RegressionFen_Game2Blunder_MoveShouldBeConsidered()
    {
        // Regression FEN from game2 log: position where ChessBot played e6d5 as a blunder
        // (score swing +748 cp). The engine should at least recognise this as a legal move.
        const string fen = "r1bqkbnr/pppp1ppp/4p1n1/3PP3/5P2/3B4/PPP1Q1PP/RNB1K1NR b KQkq - 1 7";
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var legal = engine.GetLegalMoves();
        Assert.True(legal.Count > 0, "Expected at least one legal move in the blunder position.");

        // Verify e6d5 is a legal move (it is; the blunder was the evaluation, not legality)
        bool found = legal.Any(m => m.ToString() == "e6d5");
        Assert.True(found, "Expected e6d5 to be a legal move in the blunder position.");
    }
}
