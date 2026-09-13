namespace ChessBot.MatchRunner;

using System.Security.Cryptography;
using System.Text;
using ChessBot.Engine;
using ChessBot.Engine.Types;

/// <summary>One start position a game is played from, with a name for reports.</summary>
public sealed record Opening(string Name, string Fen);

/// <summary>
/// The set of start positions a run plays from, and the schedule that assigns them to games.
///
/// Every game used to start from the initial position. Against an external engine both sides
/// are near-deterministic there, so two ten-game runs produced only four distinct openings
/// across ten games: ten games were four samples, and the confidence interval computed from
/// them was wrong by construction. A start-position set fixes the sample size to the number of
/// games actually played.
/// </summary>
public sealed class OpeningSet
{
    public required string Source { get; init; }
    public required string Format { get; init; }

    /// <summary>Plies played out of the book before the engines take over. 0 for a raw FEN/EPD list.</summary>
    public int Plies { get; init; }

    public required IReadOnlyList<Opening> Openings { get; init; }

    /// <summary>
    /// SHA-256 over the start positions in order, lowercase hex. Positions only, not names: a
    /// renamed opening is the same experiment, and two runs are comparable exactly when the
    /// positions they played from match.
    /// </summary>
    public string Sha256 => _sha ??= HashOf(Openings);
    private string? _sha;

    public int Count => Openings.Count;

    /// <summary>
    /// Which opening a game plays and which colour ChessBot (or arm A) takes.
    ///
    /// Games are paired: game 2k and 2k+1 play opening k with the colours swapped, and the
    /// openings cycle round-robin once every pair is used. That keeps a run reproducible from
    /// the manifest alone — game n always means the same position and the same colour — and
    /// keeps colours balanced at every even prefix of the run, which matters when a run is
    /// stopped early.
    /// </summary>
    public (Opening opening, bool firstPlayerIsWhite) ScheduleFor(int gameIndex)
    {
        int pair = gameIndex / 2;
        return (Openings[pair % Openings.Count], gameIndex % 2 == 0);
    }

    /// <summary>How many games this set covers before a position is replayed with the same colour.</summary>
    public int GamesBeforeRepeat => Openings.Count * 2;

    public static string HashOf(IReadOnlyList<Opening> openings)
    {
        var sb = new StringBuilder();
        foreach (var o in openings) sb.Append(o.Fen).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>One line for the console; the manifest records the fields individually.</summary>
    public override string ToString() =>
        $"{Count} openings from {Source} ({Format}" + (Plies > 0 ? $", {Plies} plies" : "") +
        $"), sha256 {Sha256[..12]}…";
}

/// <summary>
/// Loads start positions from an EPD/FEN list or a PGN file, and carries the fixed built-in set
/// the rating anchor is defined against.
/// </summary>
public static class OpeningBook
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>Default number of book plies played out of a PGN or the built-in lines.</summary>
    public const int DefaultPlies = 8;

    /// <summary>
    /// The fixed set the rating anchor is quoted against: sixteen mainline openings, four moves
    /// deep, spanning both first moves and the usual defences to each. Stored as move lists and
    /// replayed through the engine rather than pasted in as FENs, so a position in this file
    /// cannot be subtly illegal — the move generator has to accept every ply.
    ///
    /// Four moves is deep enough that the two sides are out of their shared opening knowledge
    /// and shallow enough that the position is still balanced, so the games measure play rather
    /// than which arm drew the better start.
    /// </summary>
    private static readonly (string Name, string Moves)[] StandardLines =
    {
        ("Ruy Lopez",         "e2e4 e7e5 g1f3 b8c6 f1b5 a7a6 b5a4 g8f6"),
        ("Italian Game",      "e2e4 e7e5 g1f3 b8c6 f1c4 f8c5 c2c3 g8f6"),
        ("Sicilian Open",     "e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6"),
        ("Sicilian Classical","e2e4 c7c5 g1f3 b8c6 d2d4 c5d4 f3d4 g8f6"),
        ("French Defence",    "e2e4 e7e6 d2d4 d7d5 b1c3 g8f6 c1g5 f8e7"),
        ("Caro-Kann",         "e2e4 c7c6 d2d4 d7d5 b1c3 d5e4 c3e4 c8f5"),
        ("Scandinavian",      "e2e4 d7d5 e4d5 d8d5 b1c3 d5a5 d2d4 g8f6"),
        ("Pirc Defence",      "e2e4 d7d6 d2d4 g8f6 b1c3 g7g6 g1f3 f8g7"),
        ("Queen's Gambit Declined", "d2d4 d7d5 c2c4 e7e6 b1c3 g8f6 c1g5 f8e7"),
        ("Semi-Slav",         "d2d4 d7d5 c2c4 c7c6 g1f3 g8f6 b1c3 e7e6"),
        ("Nimzo-Indian",      "d2d4 g8f6 c2c4 e7e6 b1c3 f8b4 d1c2 e8g8"),
        ("King's Indian",     "d2d4 g8f6 c2c4 g7g6 b1c3 f8g7 e2e4 d7d6"),
        ("Gruenfeld",         "d2d4 g8f6 c2c4 g7g6 b1c3 d7d5 c4d5 f6d5"),
        ("Queen's Indian",    "d2d4 g8f6 c2c4 e7e6 g1f3 b7b6 g2g3 c8b7"),
        ("English Opening",   "c2c4 e7e5 b1c3 g8f6 g1f3 b8c6 g2g3 f8b4"),
        ("Reti Opening",      "g1f3 d7d5 c2c4 e7e6 g2g3 g8f6 f1g2 f8e7"),
    };

    private static OpeningSet? _standard;

    /// <summary>The fixed built-in set. Identical on every machine and every run.</summary>
    public static OpeningSet Standard => _standard ??= BuildStandard();

    private static OpeningSet BuildStandard()
    {
        var openings = new List<Opening>(StandardLines.Length);
        foreach (var (name, moves) in StandardLines)
            openings.Add(new Opening(name, ReplayUci(moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))));

        return new OpeningSet
        {
            Source   = "built-in:standard-16",
            Format   = "built-in",
            Plies    = DefaultPlies,
            Openings = openings,
        };
    }

    /// <summary>
    /// The single-position "set" that reproduces the old behaviour: every game from the initial
    /// position. Kept so a run can still ask for it deliberately, which is not the same thing as
    /// getting it because no alternative existed.
    /// </summary>
    public static OpeningSet StartPositionOnly { get; } = new()
    {
        Source   = "built-in:start-position",
        Format   = "built-in",
        Plies    = 0,
        Openings = new[] { new Opening("Start position", StartFen) },
    };

    /// <summary>
    /// Builds a set of balanced start positions from a seeded random walk, for runs that need
    /// more openings than a hand-written list can carry.
    ///
    /// A walk of random legal moves, kept only when material is still level and the position is
    /// still playable. Not book lines — these are positions no opening theory would reach, which
    /// is a fair trade for a calibration or a large A/B run: what such a run needs is many
    /// independent, unbiased starts, and sixteen mainlines replayed sixty times each are neither.
    /// For the rating anchor, where the question is how the engine plays real chess, the built-in
    /// mainline set is the better instrument.
    ///
    /// The same seed always produces the same set, so a run stays reproducible from its manifest
    /// without the openings file having to be kept.
    /// </summary>
    public static OpeningSet Generate(int count, int seed = 20260912, int plies = DefaultPlies)
    {
        var openings = new List<Opening>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rng = new Random(seed);

        // Bounded so an impossible request fails instead of spinning: at eight plies there are
        // far more than a few thousand balanced positions, so exhausting this means the caller
        // asked for more than exist.
        int attempts = 0;
        int maxAttempts = Math.Max(10_000, count * 400);

        // One engine, reset per attempt. A fresh ChessEngine allocates a 64 MB transposition
        // table, so constructing one per attempt spent tens of gigabytes of allocation on a
        // walk that never searches anything — enough to make an impossible request look like a
        // hang rather than an error.
        var engine = new ChessEngine();

        while (openings.Count < count && attempts < maxAttempts)
        {
            attempts++;
            engine.LoadFen(StartFen);

            bool usable = true;
            for (int ply = 0; ply < plies; ply++)
            {
                var legal = engine.GetLegalMoves();
                if (legal.Count == 0) { usable = false; break; }
                engine.MakeMove(legal[rng.Next(legal.Count)]);
            }

            if (!usable) continue;
            if (engine.GetLegalMoves().Count == 0) continue;                  // already terminal
            if (engine.Evaluate().MaterialBalance.Imbalance != 0) continue;   // one side already ahead

            string fen = engine.ExportFen();
            if (seen.Add(fen))
                openings.Add(new Opening($"gen-{openings.Count + 1:D4}", fen));
        }

        if (openings.Count < count)
            throw new InvalidOperationException(
                $"Could only generate {openings.Count} of {count} balanced openings in {attempts} attempts.");

        return new OpeningSet
        {
            Source   = $"generated:seed={seed},plies={plies},count={count}",
            Format   = "generated",
            Plies    = plies,
            Openings = openings,
        };
    }

    /// <summary>
    /// Writes a set as an EPD file that <see cref="Load"/> reads back unchanged. The header
    /// records how the set was produced, so a file found on disk a month later still says what
    /// it is and how to regenerate it.
    /// </summary>
    public static void WriteEpd(OpeningSet set, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

        using var w = new StreamWriter(path, append: false);
        w.WriteLine($"# {set.Count} openings, source {set.Source}");
        w.WriteLine($"# sha256 {set.Sha256}");
        w.WriteLine($"# written {DateTime.UtcNow:o}");

        foreach (var opening in set.Openings)
            w.WriteLine($"{opening.Fen} ; id \"{opening.Name}\"");
    }

    /// <summary>
    /// Reads an opening set from disk. The format follows the extension: <c>.pgn</c> is parsed as
    /// PGN and replayed <paramref name="plies"/> plies deep, anything else as one EPD or FEN per
    /// line. Duplicate positions are dropped — an opening file that lists a position twice would
    /// weight it double in a round-robin without saying so.
    /// </summary>
    public static OpeningSet Load(string path, int plies = DefaultPlies)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Opening file not found: {path}", path);

        bool isPgn = Path.GetExtension(path).Equals(".pgn", StringComparison.OrdinalIgnoreCase);
        var openings = isPgn
            ? ParsePgn(File.ReadAllText(path), plies)
            : ParseEpd(File.ReadAllLines(path));

        var deduped = new List<Opening>(openings.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in openings)
            if (seen.Add(o.Fen)) deduped.Add(o);

        if (deduped.Count == 0)
            throw new InvalidDataException($"Opening file contained no usable positions: {path}");

        return new OpeningSet
        {
            Source   = path,
            Format   = isPgn ? "pgn" : "epd/fen",
            Plies    = isPgn ? plies : 0,
            Openings = deduped,
        };
    }

    // ── EPD / FEN lists ──────────────────────────────────────────────────────

    /// <summary>
    /// One position per line. A six-field line is a FEN and is taken as it stands; a shorter line
    /// is EPD, whose trailing operations are dropped and whose missing halfmove and fullmove
    /// counters are supplied. An <c>id "..."</c> operation names the opening when present.
    /// </summary>
    private static List<Opening> ParseEpd(IEnumerable<string> lines)
    {
        var result = new List<Opening>();
        int lineNumber = 0;

        foreach (string raw in lines)
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string? name = ExtractEpdId(line);

            int semi = line.IndexOf(';');
            string body = (semi >= 0 ? line[..semi] : line).Trim();

            var fields = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4)
                throw new InvalidDataException($"Line {lineNumber} is not a FEN or EPD position: {raw}");

            bool isFen = fields.Length >= 6 &&
                         int.TryParse(fields[4], out _) && int.TryParse(fields[5], out _);

            string fen = isFen
                ? string.Join(' ', fields.Take(6))
                : string.Join(' ', fields.Take(4)) + " 0 1";

            // Loading it is the check that it is a real position: a start position the engine
            // cannot parse would otherwise surface as a crash mid-run, games in.
            var engine = new ChessEngine();
            engine.LoadFen(fen);
            if (engine.GetLegalMoves().Count == 0)
                throw new InvalidDataException($"Line {lineNumber} is a terminal position, unplayable as an opening: {fen}");

            result.Add(new Opening(name ?? $"line {lineNumber}", fen));
        }

        return result;
    }

    private static string? ExtractEpdId(string line)
    {
        int idIdx = line.IndexOf("id ", StringComparison.Ordinal);
        if (idIdx < 0) return null;

        int open = line.IndexOf('"', idIdx);
        if (open < 0) return null;
        int close = line.IndexOf('"', open + 1);
        return close > open ? line[(open + 1)..close] : null;
    }

    // ── PGN ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the first <paramref name="plies"/> plies of every game in the file as one opening.
    /// Movetext may be SAN, as any published opening suite is, or the long algebraic this
    /// project's own PGN writer emits; both are resolved against the legal moves of the position
    /// they are played in, so an unparseable token is a hard error rather than a skipped move.
    /// </summary>
    private static List<Opening> ParsePgn(string text, int plies)
    {
        var result = new List<Opening>();

        foreach (var (tags, movetext) in SplitPgnGames(text))
        {
            string startFen = tags.TryGetValue("FEN", out string? fen) && !string.IsNullOrWhiteSpace(fen)
                ? fen
                : StartFen;

            var engine = new ChessEngine();
            engine.LoadFen(startFen);

            int played = 0;
            foreach (string token in TokenizeMovetext(movetext))
            {
                if (played >= plies) break;
                var move = ResolveMove(engine, token);
                engine.MakeMove(move);
                played++;
            }

            if (played < plies) continue;                       // too short to be an opening
            if (engine.GetLegalMoves().Count == 0) continue;    // book line ended the game

            result.Add(new Opening(NameOf(tags, result.Count + 1), engine.ExportFen()));
        }

        return result;
    }

    private static string NameOf(Dictionary<string, string> tags, int ordinal)
    {
        if (tags.TryGetValue("Opening", out string? opening) && !string.IsNullOrWhiteSpace(opening))
            return opening;
        if (tags.TryGetValue("ECO", out string? eco) && !string.IsNullOrWhiteSpace(eco))
            return eco;
        if (tags.TryGetValue("Event", out string? ev) && !string.IsNullOrWhiteSpace(ev) && ev != "?")
            return ev;
        return $"pgn game {ordinal}";
    }

    private static IEnumerable<(Dictionary<string, string> tags, string movetext)> SplitPgnGames(string text)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var movetext = new StringBuilder();
        bool inMovetext = false;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith('['))
            {
                // A tag after movetext starts the next game, so the current one is complete.
                if (inMovetext)
                {
                    yield return (tags, movetext.ToString());
                    tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    movetext.Clear();
                    inMovetext = false;
                }

                int space = line.IndexOf(' ');
                int open  = line.IndexOf('"');
                int close = line.LastIndexOf('"');
                if (space > 1 && open > space && close > open)
                    tags[line[1..space]] = line[(open + 1)..close];
                continue;
            }

            if (line.Length == 0) continue;

            inMovetext = true;
            movetext.Append(line).Append(' ');
        }

        if (inMovetext) yield return (tags, movetext.ToString());
    }

    /// <summary>
    /// Strips everything that is not a move: comments, variations, NAGs, move numbers and the
    /// result token. Variations are skipped whole, so a game with analysis in it yields the moves
    /// actually played rather than a mix of the two.
    /// </summary>
    private static IEnumerable<string> TokenizeMovetext(string movetext)
    {
        var token = new StringBuilder();
        int braceDepth = 0;
        int parenDepth = 0;

        foreach (char c in movetext)
        {
            if (braceDepth > 0)
            {
                if (c == '}') braceDepth--;
                continue;
            }
            if (parenDepth > 0)
            {
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                continue;
            }

            switch (c)
            {
                case '{': braceDepth++; continue;
                case '(': parenDepth++; continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    string t = token.ToString();
                    token.Clear();
                    if (IsMoveToken(t)) yield return t;
                }
                continue;
            }

            token.Append(c);
        }

        if (token.Length > 0 && IsMoveToken(token.ToString()))
            yield return token.ToString();
    }

    private static bool IsMoveToken(string token)
    {
        if (token.Length == 0) return false;
        if (token[0] == '$') return false;                       // NAG
        if (token is "1-0" or "0-1" or "1/2-1/2" or "*") return false;
        if (char.IsDigit(token[0]) && token.Contains('.')) return false;   // "12." / "12..."
        if (token.All(c => c == '.')) return false;
        return true;
    }

    // ── Move resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves one movetext token against the legal moves of the current position. Accepts long
    /// algebraic ("e2e4", "e7e8q") and SAN ("Nf3", "exd5", "O-O", "R1a3", "e8=Q+").
    /// </summary>
    private static Move ResolveMove(ChessEngine engine, string token)
    {
        var legal = engine.GetLegalMoves();

        // Long algebraic first: it is unambiguous, and this project's own PGNs are written in it.
        foreach (var m in legal)
            if (m.ToString().Equals(token, StringComparison.OrdinalIgnoreCase))
                return m;

        return ResolveSan(engine, legal, token);
    }

    private static Move ResolveSan(ChessEngine engine, IReadOnlyList<Move> legal, string token)
    {
        string san = token.TrimEnd('+', '#', '!', '?');
        if (san.EndsWith("e.p.", StringComparison.OrdinalIgnoreCase)) san = san[..^4];

        // Castling: named by side, so it is matched on the king's destination file rather than
        // by parsing a destination square out of the token.
        if (san is "O-O" or "0-0" or "O-O-O" or "0-0-0")
        {
            bool queenside = san.Length > 3;
            foreach (var m in legal)
                if (m.MoveType == MoveType.Castling && m.To.File == (queenside ? 2 : 6))
                    return m;
            throw new InvalidDataException($"Castling move '{token}' is not legal in {engine.ExportFen()}");
        }

        PieceType promotion = PieceType.None;
        int eq = san.IndexOf('=');
        if (eq >= 0)
        {
            promotion = PieceFromLetter(san[(eq + 1)..].TrimStart()[0]);
            san = san[..eq];
        }
        else if (san.Length >= 3 && "QRBN".Contains(san[^1]))
        {
            // "e8Q": the equals sign is optional in some writers.
            promotion = PieceFromLetter(san[^1]);
            san = san[..^1];
        }

        PieceType piece = PieceType.Pawn;
        if (san.Length > 0 && "KQRBN".Contains(san[0]))
        {
            piece = PieceFromLetter(san[0]);
            san = san[1..];
        }

        san = san.Replace("x", string.Empty);

        if (san.Length < 2)
            throw new InvalidDataException($"Unparseable move token '{token}' in {engine.ExportFen()}");

        Square to = Square.FromAlgebraic(san[^2..]);
        string disambiguation = san[..^2];

        int? fromFile = null, fromRank = null;
        foreach (char c in disambiguation)
        {
            if (c is >= 'a' and <= 'h') fromFile = c - 'a';
            else if (c is >= '1' and <= '8') fromRank = c - '1';
        }

        var board = engine.GetBoardSnapshot();
        Move? found = null;

        foreach (var m in legal)
        {
            if (m.To != to) continue;
            if (board.GetPiece(m.From).Type != piece) continue;
            if (m.PromotionType != promotion) continue;
            if (fromFile is int f && m.From.File != f) continue;
            if (fromRank is int r && m.From.Rank != r) continue;

            if (found is not null)
                throw new InvalidDataException($"Ambiguous move token '{token}' in {engine.ExportFen()}");
            found = m;
        }

        return found ?? throw new InvalidDataException(
            $"Move token '{token}' is not legal in {engine.ExportFen()}");
    }

    private static PieceType PieceFromLetter(char c) => char.ToUpperInvariant(c) switch
    {
        'K' => PieceType.King,
        'Q' => PieceType.Queen,
        'R' => PieceType.Rook,
        'B' => PieceType.Bishop,
        'N' => PieceType.Knight,
        'P' => PieceType.Pawn,
        _   => throw new InvalidDataException($"Unknown piece letter '{c}'"),
    };

    /// <summary>Plays a long-algebraic move list from the initial position and returns the FEN reached.</summary>
    private static string ReplayUci(IReadOnlyList<string> moves)
    {
        var engine = new ChessEngine();
        engine.LoadFen(StartFen);

        foreach (string uci in moves)
            engine.MakeMove(ResolveMove(engine, uci));

        return engine.ExportFen();
    }
}
