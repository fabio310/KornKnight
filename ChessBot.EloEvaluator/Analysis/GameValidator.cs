using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Analysis;

/// <summary>
/// Validates a parsed game and appends error messages to ValidationErrors.
/// An invalid game is excluded from Elo scoring but still listed in the report.
///
/// Checks performed
/// ─────────────────
///   • Outcome must be Win / Draw / Loss (not Aborted or Unknown)
///   • ResultTag must not be "*"
///   • ChessBotColor must be "White" or "Black"
///   • Minimum ply count sanity check
///   • Result tag must be consistent with Outcome + color
///   • Termination text must not contradict ResultTag
///   • FEN color-to-move must match the reported IsWhiteMove flag (log files)
/// </summary>
public static class GameValidator
{
    public static void Validate(ParsedGame game)
    {
        game.ValidationErrors.Clear();
        game.IsValid = true;

        // 1. Aborted game
        if (game.Outcome == GameOutcomeKind.Aborted)
        {
            Fail(game, "Game was aborted — excluded from scoring.");
        }

        // 2. Unknown outcome
        if (game.Outcome == GameOutcomeKind.Unknown)
        {
            Fail(game, "Unknown game outcome — cannot score.");
        }

        // 3. Missing result tag
        if (game.ResultTag == "*")
        {
            Fail(game, "No result tag ('*') — cannot score.");
        }

        // 4. Unknown ChessBot color
        if (game.ChessBotColor is not ("White" or "Black"))
        {
            Fail(game, $"Unknown ChessBot color: '{game.ChessBotColor}'.");
        }

        // 5. Ply count sanity
        if (game.TotalPlies > 0 && game.TotalPlies < 2)
        {
            Warn(game, $"Very few plies ({game.TotalPlies}) — possibly truncated.");
        }

        // 6. Result tag ↔ Outcome + color consistency
        bool resultOk = (game.Outcome, game.ChessBotColor, game.ResultTag) switch
        {
            (GameOutcomeKind.Win,  "White", "1-0")     => true,
            (GameOutcomeKind.Win,  "Black", "0-1")     => true,
            (GameOutcomeKind.Loss, "White", "0-1")     => true,
            (GameOutcomeKind.Loss, "Black", "1-0")     => true,
            (GameOutcomeKind.Draw, _,       "1/2-1/2") => true,
            (GameOutcomeKind.Aborted, _,    "*")       => true,
            (GameOutcomeKind.Unknown, _,    _)         => false,
            _                                          => false
        };

        if (!resultOk &&
            game.Outcome is not GameOutcomeKind.Unknown &&
            game.ResultTag != "*")
        {
            Fail(game,
                $"Result inconsistency: outcome={game.Outcome}, " +
                $"color={game.ChessBotColor}, tag={game.ResultTag}.");
        }

        // 7. Termination text vs result tag
        if (!string.IsNullOrEmpty(game.TerminationReason))
        {
            bool termOk = true;
            if (game.TerminationReason.Contains("White wins",
                    StringComparison.OrdinalIgnoreCase) && game.ResultTag == "0-1")
                termOk = false;
            if (game.TerminationReason.Contains("Black wins",
                    StringComparison.OrdinalIgnoreCase) && game.ResultTag == "1-0")
                termOk = false;

            if (!termOk)
                Warn(game,
                    $"Termination text '{game.TerminationReason}' contradicts result {game.ResultTag}.");
        }

        // 8. FEN color-to-move check (log files only, requires per-move data)
        if (game.Moves.Count >= 1)
            ValidateFenColors(game);
    }

    private static void ValidateFenColors(ParsedGame game)
    {
        foreach (var m in game.Moves)
        {
            if (string.IsNullOrEmpty(m.FenBefore)) continue;

            var parts = m.FenBefore.Split(' ');
            if (parts.Length < 2) continue;

            string colorInFen = parts[1];  // "w" or "b"
            bool fenSaysWhite = colorInFen == "w";

            if (fenSaysWhite != m.IsWhiteMove)
            {
                Warn(game,
                    $"FEN color-to-move mismatch at move {m.MoveNumber} " +
                    $"({(m.IsWhiteMove ? "White" : "Black")}): FEN says '{colorInFen}'.");
                break;  // Report only the first mismatch
            }
        }
    }

    // Fails = excluded from scoring
    private static void Fail(ParsedGame game, string msg)
    {
        game.ValidationErrors.Add(msg);
        game.IsValid = false;
    }

    // Warns = still counted, but flagged
    private static void Warn(ParsedGame game, string msg)
    {
        game.ValidationErrors.Add($"[warn] {msg}");
    }
}
