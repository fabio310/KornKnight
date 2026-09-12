namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;

/// <summary>
/// Single source of truth for Piece-Square Table (PST) values, shared by the static
/// <see cref="Evaluator"/> (full scan) and <see cref="ChessBot.Engine.Board.Board"/>'s incremental
/// make/unmake eval state. Keeping one copy guarantees the incrementally maintained PST term is
/// byte-for-byte identical to a from-scratch scan, so <see cref="Evaluator.EvaluateFast"/> matches
/// <see cref="Evaluator.Evaluate"/> exactly.
///
/// Tables are from White's perspective: index [rank][file] with rank 0 = rank 1 (White's side),
/// rank 7 = rank 8 (Black's side), file 0 = a-file.
/// </summary>
internal static class PieceSquareTables
{
    // Pawn PST (pawns advance forward, prefer central files)
    private static readonly int[][] Pawn =
    {
        new[] { 0, 0, 0, 0, 0, 0, 0, 0 },
        new[] { 5, 5, 5, 10, 10, 5, 5, 5 },
        new[] { 5, 5, 10, 20, 20, 10, 5, 5 },
        new[] { 5, 5, 10, 25, 25, 10, 5, 5 },
        new[] { 5, 5, 10, 20, 20, 10, 5, 5 },
        new[] { 5, 5, 5, 10, 10, 5, 5, 5 },
        new[] { 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { 0, 0, 0, 0, 0, 0, 0, 0 }
    };

    // Knight PST (centralize knights)
    private static readonly int[][] Knight =
    {
        new[] { -50, -40, -30, -30, -30, -30, -40, -50 },
        new[] { -40, -20, 0, 5, 5, 0, -20, -40 },
        new[] { -30, 5, 10, 15, 15, 10, 5, -30 },
        new[] { -30, 5, 15, 20, 20, 15, 5, -30 },
        new[] { -30, 5, 15, 20, 20, 15, 5, -30 },
        new[] { -30, 5, 10, 15, 15, 10, 5, -30 },
        new[] { -40, -20, 0, 5, 5, 0, -20, -40 },
        new[] { -50, -40, -30, -30, -30, -30, -40, -50 }
    };

    // Bishop PST (control center and long diagonals)
    private static readonly int[][] Bishop =
    {
        new[] { -20, -10, -10, -10, -10, -10, -10, -20 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -10, 5, 10, 10, 10, 10, 5, -10 },
        new[] { -10, 5, 10, 15, 15, 10, 5, -10 },
        new[] { -10, 5, 10, 15, 15, 10, 5, -10 },
        new[] { -10, 5, 10, 10, 10, 10, 5, -10 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -20, -10, -10, -10, -10, -10, -10, -20 }
    };

    // Rook PST (control files, especially open files)
    private static readonly int[][] Rook =
    {
        new[] { 0, 0, 0, 5, 5, 0, 0, 0 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 0, 0, 0, 5, 5, 0, 0, 0 }
    };

    // Queen PST (centralize slightly)
    private static readonly int[][] Queen =
    {
        new[] { -20, -10, -10, -5, -5, -10, -10, -20 },
        new[] { -10, 0, 5, 5, 5, 5, 0, -10 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -5, 5, 5, 5, 5, 5, 5, -5 },
        new[] { -5, 5, 5, 5, 5, 5, 5, -5 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -10, 0, 5, 5, 5, 5, 0, -10 },
        new[] { -20, -10, -10, -5, -5, -10, -10, -20 }
    };

    /// <summary>
    /// Midgame king table: shelter behind the castled pawns, and a penalty that deepens the
    /// further the king walks up the board.
    ///
    /// Like every table here it is White-perspective and mirrored for Black, so it must fall
    /// away monotonically from rank 1 rather than being symmetric about the middle. It was
    /// previously written by mirroring the bottom four ranks into the top four, which scored a
    /// White king on g8 the same +30 as a White king castled on g1 — an invitation to march the
    /// king into the enemy camp with queens still on. That was harmless only because kings were
    /// excluded from the piece-square accumulators entirely and this table was never read.
    /// </summary>
    private static readonly int[][] King =
    {
        new[] { 20, 30, 10, 0, 0, 10, 30, 20 },
        new[] { 20, 20, 0, 0, 0, 0, 20, 20 },
        new[] { -10, -20, -20, -20, -20, -20, -20, -10 },
        new[] { -20, -30, -30, -40, -40, -30, -30, -20 },
        new[] { -30, -40, -40, -50, -50, -40, -40, -30 },
        new[] { -30, -40, -40, -50, -50, -40, -40, -30 },
        new[] { -30, -40, -40, -50, -50, -40, -40, -30 },
        new[] { -30, -40, -40, -50, -50, -40, -40, -30 }
    };

    /// <summary>
    /// Returns the jagged PST for a piece type (White-perspective, [rank][file]).
    /// Pawn table is the fallback for unexpected types, matching the Evaluator's historic behavior.
    /// </summary>
    public static int[][] TableFor(PieceType type) => type switch
    {
        PieceType.Pawn => Pawn,
        PieceType.Knight => Knight,
        PieceType.Bishop => Bishop,
        PieceType.Rook => Rook,
        PieceType.Queen => Queen,
        PieceType.King => King,
        _ => Pawn
    };

    /// <summary>
    /// Returns the PST value for a piece of the given color/type on the given square.
    /// Black pieces are mirrored (rotated 180°) so the same White-perspective table applies.
    /// </summary>
    public static int Value(Color color, PieceType type, Square square)
    {
        int rank = color == Color.White ? square.Rank : 7 - square.Rank;
        int file = color == Color.White ? square.File : 7 - square.File;
        return TableFor(type)[rank][file];
    }

    // ── Endgame tables and values (tapered evaluation) ────────────────────────
    //
    // One table set for the whole game asks a single number to describe two different games. A
    // knight on the rim is bad in both, but a pawn on the sixth rank is a detail in the opening
    // and close to decisive in a pawn endgame, and a rook's value rises as the position opens.
    // The tables above are the midgame set; these are what the same pieces are worth once the
    // board has emptied. Evaluator interpolates between them on the 24-point material phase, so
    // the transition is continuous rather than a threshold the search can see itself crossing.
    //
    // Only the endgame side is new: the midgame tables and values are exactly the ones the
    // engine already used, so at full phase a tapered evaluation reproduces the untapered score
    // to the centipawn. That is deliberate — it makes the change a pure addition at the opening
    // end, and confines any measured difference to positions where material has actually left
    // the board.

    /// <summary>
    /// Endgame pawn table: the advance gradient is much steeper than in the midgame, because a
    /// passed pawn two squares from promotion is a concrete threat rather than a small
    /// positional plus, and central files no longer matter more than the wings.
    /// </summary>
    private static readonly int[][] PawnEndgame =
    {
        new[] {   0,   0,   0,   0,   0,   0,   0,   0 },
        new[] {  10,  10,  10,  10,  10,  10,  10,  10 },
        new[] {  15,  15,  15,  15,  15,  15,  15,  15 },
        new[] {  25,  25,  25,  25,  25,  25,  25,  25 },
        new[] {  40,  40,  40,  40,  40,  40,  40,  40 },
        new[] {  60,  60,  60,  60,  60,  60,  60,  60 },
        new[] {  90,  90,  90,  90,  90,  90,  90,  90 },
        new[] {   0,   0,   0,   0,   0,   0,   0,   0 }
    };

    /// <summary>
    /// Endgame knight table: the same centralisation shape, flattened. A knight still hates the
    /// rim, but with few pieces left the difference between a good and a bad square is smaller
    /// than the difference between having the knight and not.
    /// </summary>
    private static readonly int[][] KnightEndgame =
    {
        new[] { -40, -30, -20, -20, -20, -20, -30, -40 },
        new[] { -30, -10,   0,   5,   5,   0, -10, -30 },
        new[] { -20,   0,  10,  10,  10,  10,   0, -20 },
        new[] { -20,   5,  10,  15,  15,  10,   5, -20 },
        new[] { -20,   5,  10,  15,  15,  10,   5, -20 },
        new[] { -20,   0,  10,  10,  10,  10,   0, -20 },
        new[] { -30, -10,   0,   5,   5,   0, -10, -30 },
        new[] { -40, -30, -20, -20, -20, -20, -30, -40 }
    };

    /// <summary>
    /// Endgame bishop table: nearly flat. An open endgame board gives a bishop long diagonals
    /// from almost anywhere, so where it stands matters much less than in a blocked middlegame.
    /// </summary>
    private static readonly int[][] BishopEndgame =
    {
        new[] { -10,  -5,  -5,  -5,  -5,  -5,  -5, -10 },
        new[] {  -5,   5,   5,   5,   5,   5,   5,  -5 },
        new[] {  -5,   5,  10,  10,  10,  10,   5,  -5 },
        new[] {  -5,   5,  10,  10,  10,  10,   5,  -5 },
        new[] {  -5,   5,  10,  10,  10,  10,   5,  -5 },
        new[] {  -5,   5,  10,  10,  10,  10,   5,  -5 },
        new[] {  -5,   5,   5,   5,   5,   5,   5,  -5 },
        new[] { -10,  -5,  -5,  -5,  -5,  -5,  -5, -10 }
    };

    /// <summary>
    /// Endgame rook table: flat. The midgame table's preference for the second and seventh ranks
    /// is about attacking a castled king, which is not what a rook does once the kings are out
    /// in the open; activity is better expressed by the mobility the search finds for itself.
    /// </summary>
    private static readonly int[][] RookEndgame =
    {
        new[] {   5,   5,   5,   5,   5,   5,   5,   5 },
        new[] {   5,   5,   5,   5,   5,   5,   5,   5 },
        new[] {   5,   5,   5,   5,   5,   5,   5,   5 },
        new[] {   5,   5,   5,   5,   5,   5,   5,   5 },
        new[] {   5,   5,   5,   5,   5,   5,   5,   5 },
        new[] {   5,   5,   5,   5,   5,   5,   5,   5 },
        new[] {  10,  10,  10,  10,  10,  10,  10,  10 },
        new[] {   5,   5,   5,   5,   5,   5,   5,   5 }
    };

    /// <summary>
    /// Endgame queen table: centralisation matters more than in the midgame, where the queen is
    /// kept back from an early attack. With few pieces left she is simply strongest in the middle.
    /// </summary>
    private static readonly int[][] QueenEndgame =
    {
        new[] { -20, -10, -10,  -5,  -5, -10, -10, -20 },
        new[] { -10,   0,   5,   5,   5,   5,   0, -10 },
        new[] { -10,   5,  10,  10,  10,  10,   5, -10 },
        new[] {  -5,   5,  10,  15,  15,  10,   5,  -5 },
        new[] {  -5,   5,  10,  15,  15,  10,   5,  -5 },
        new[] { -10,   5,  10,  10,  10,  10,   5, -10 },
        new[] { -10,   0,   5,   5,   5,   5,   0, -10 },
        new[] { -20, -10, -10,  -5,  -5, -10, -10, -20 }
    };

    /// <summary>
    /// Endgame king table: the king is a piece again once the queens are off, so it wants the
    /// middle of the board and the corners are close to lost.
    ///
    /// This replaces the evaluator's separate centralisation term, which applied a 0-9 point
    /// bonus per king behind a hard "non-king material below 1,000 centipawns" threshold. The
    /// threshold was a step the search could see itself crossing — one capture flipped the king's
    /// score by the full amount — and it described the same idea twice, in a shape the taper
    /// already expresses continuously and with the weight the transition actually deserves.
    /// </summary>
    private static readonly int[][] KingEndgame =
    {
        new[] { -50, -30, -30, -30, -30, -30, -30, -50 },
        new[] { -30, -10,   0,   0,   0,   0, -10, -30 },
        new[] { -30,   0,  20,  30,  30,  20,   0, -30 },
        new[] { -30,   0,  30,  40,  40,  30,   0, -30 },
        new[] { -30,   0,  30,  40,  40,  30,   0, -30 },
        new[] { -30,   0,  20,  30,  30,  20,   0, -30 },
        new[] { -30, -10,   0,   0,   0,   0, -10, -30 },
        new[] { -50, -30, -30, -30, -30, -30, -30, -50 }
    };

    /// <summary>
    /// Returns the endgame PST for a piece type, in the same [rank][file] White-perspective
    /// layout as <see cref="TableFor"/>.
    /// </summary>
    public static int[][] EndgameTableFor(PieceType type) => type switch
    {
        PieceType.Pawn   => PawnEndgame,
        PieceType.Knight => KnightEndgame,
        PieceType.Bishop => BishopEndgame,
        PieceType.Rook   => RookEndgame,
        PieceType.Queen  => QueenEndgame,
        PieceType.King   => KingEndgame,
        _                => PawnEndgame
    };

    /// <summary>
    /// Endgame PST value for a piece of the given colour/type on the given square, mirrored for
    /// Black exactly as <see cref="Value"/> does.
    /// </summary>
    public static int EndgameValue(Color color, PieceType type, Square square)
    {
        int rank = color == Color.White ? square.Rank : 7 - square.Rank;
        int file = color == Color.White ? square.File : 7 - square.File;
        return EndgameTableFor(type)[rank][file];
    }

    /// <summary>
    /// Endgame material value in centipawns. Pawns are worth more once promotion is a real
    /// prospect; a knight loses value as the board opens and it can no longer reach both wings,
    /// while a rook gains it. Bishops gain slightly, and the queen is close to flat.
    ///
    /// The midgame values remain <see cref="PieceTypeExtensions.MaterialValue"/>, unchanged, so
    /// a tapered evaluation at full phase equals the untapered one exactly.
    /// </summary>
    public static int EndgameMaterialValue(PieceType type) => type switch
    {
        PieceType.Pawn   => 120,
        PieceType.Knight => 310,
        PieceType.Bishop => 340,
        PieceType.Rook   => 530,
        PieceType.Queen  => 930,
        PieceType.King   => 0,
        _                => 0
    };

    /// <summary>
    /// Blends a midgame and an endgame score on the 24-point material phase: the midgame value
    /// with a full starting array, the endgame value once the pieces are gone, and a straight
    /// line between.
    ///
    /// Integer division truncates towards zero, so negating both inputs negates the result
    /// exactly — the evaluation stays colour-symmetric.
    /// </summary>
    public static int Interpolate(int midgameScore, int endgameScore, int phase)
    {
        int p = GamePhase.Clamp(phase);
        return (midgameScore * p + endgameScore * (GamePhase.Max - p)) / GamePhase.Max;
    }
}
