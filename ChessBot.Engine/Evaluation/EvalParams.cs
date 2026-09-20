namespace ChessBot.Engine.Evaluation;

using System.Text.Json;
using ChessBot.Engine.Types;

/// <summary>
/// Every evaluation weight, as one flat vector of ints with named offsets.
///
/// The weights used to be compile-time constants — <c>const int DoubledMidgame = 15</c> and so on,
/// plus the piece-square tables — and a <c>const</c> is baked into the IL of every method that
/// reads it: nothing can perturb it at runtime, so no tuner could touch a single weight. Here they
/// are data. <see cref="Active"/> is the vector the evaluation reads; <see cref="Default"/> is built
/// from the original literals, so with the default active every score is bit-identical to the
/// constants it replaced (EvalFingerprintTests pins that over 10,000 positions).
///
/// Material here is the EVALUATION's material, midgame and endgame. It is deliberately separate
/// from <see cref="PieceTypeExtensions.MaterialValue"/>, which move ordering and static exchange
/// evaluation also read: a tuner that moves the evaluation's idea of a knight must not silently
/// change how captures are ranked or which exchanges count as winning.
///
/// Changing <see cref="Active"/> does not reach a board's incremental accumulators, which were
/// summed under the vector active when the pieces were placed. Set it before loading positions,
/// or re-load them — the tuner evaluates with the full scan, which reads the vector directly.
/// </summary>
public sealed class EvalParams
{
    // ── Layout ────────────────────────────────────────────────────────────────
    // Piece-indexed blocks are indexed by (PieceType - 1): pawn 0 .. king 5. Piece-square blocks
    // hold 64 White-perspective squares per piece, a1 = 0 .. h8 = 63; Black reads square 63 - index.

    public const int PieceTypes = 6;

    public const int MaterialMg     = 0;
    public const int MaterialEg     = MaterialMg + PieceTypes;
    public const int PstMg          = MaterialEg + PieceTypes;
    public const int PstEg          = PstMg + PieceTypes * 64;
    public const int DoubledMg      = PstEg + PieceTypes * 64;
    public const int DoubledEg      = DoubledMg + 1;
    public const int IsolatedMg     = DoubledEg + 1;
    public const int IsolatedEg     = IsolatedMg + 1;
    public const int PassedMg       = IsolatedEg + 1;          // 8, by relative rank
    public const int PassedEg       = PassedMg + 8;            // 8, by relative rank
    public const int ProtectedMg    = PassedEg + 8;
    public const int ProtectedEg    = ProtectedMg + 1;
    public const int ConnectedMg    = ProtectedEg + 1;
    public const int ConnectedEg    = ConnectedMg + 1;
    public const int BishopPairMg   = ConnectedEg + 1;
    public const int BishopPairEg   = BishopPairMg + 1;
    public const int RookOpenMg     = BishopPairEg + 1;
    public const int RookOpenEg     = RookOpenMg + 1;
    public const int RookHalfOpenMg = RookOpenEg + 1;
    public const int RookHalfOpenEg = RookHalfOpenMg + 1;
    public const int RookSeventhMg  = RookHalfOpenEg + 1;
    public const int RookSeventhEg  = RookSeventhMg + 1;
    public const int KingShelterMg  = RookSeventhEg + 1;    // 4, by distance of the nearest own pawn ahead
    public const int KingShelterEg  = KingShelterMg + 4;    // 4
    public const int KingStormMg    = KingShelterEg + 4;    // 4, by distance of the nearest enemy pawn
    public const int KingStormEg    = KingStormMg + 4;      // 4

    /// <summary>Number of weights.</summary>
    public const int Count = KingStormEg + 4;

    /// <summary>The weights. Indexed by the offsets above.</summary>
    public int[] Values { get; }

    public EvalParams(int[] values)
    {
        if (values.Length != Count)
            throw new ArgumentException($"expected {Count} weights, got {values.Length}", nameof(values));
        Values = values;
    }

    /// <summary>A copy, for a tuner to perturb without touching the original.</summary>
    public EvalParams Clone() => new((int[])Values.Clone());

    // ── The active vector ────────────────────────────────────────────────────

    /// <summary>The weights built from the original constants and tables.</summary>
    public static EvalParams Default { get; } = BuildDefault();

    /// <summary>
    /// The vector the evaluation reads. A static field rather than a property on the hot path:
    /// every evaluation term reads it, and the reads should be one load and an index.
    /// </summary>
    internal static int[] V = Default.Values;

    private static EvalParams _active = Default;

    /// <summary>
    /// The active weights. Setting it affects every evaluation made afterwards, process-wide;
    /// see the class remarks for what it does not reach.
    /// </summary>
    public static EvalParams Active
    {
        get => _active;
        set { _active = value ?? Default; V = _active.Values; }
    }

    // ── Hot-path accessors ───────────────────────────────────────────────────

    /// <summary>Midgame piece-square value, White-perspective table mirrored for Black.</summary>
    internal static int PstMidgame(Color color, PieceType type, int squareIndex) =>
        V[PstMg + ((int)type - 1) * 64 + (color == Color.White ? squareIndex : 63 - squareIndex)];

    /// <summary>Endgame piece-square value, mirrored for Black as <see cref="PstMidgame"/> is.</summary>
    internal static int PstEndgame(Color color, PieceType type, int squareIndex) =>
        V[PstEg + ((int)type - 1) * 64 + (color == Color.White ? squareIndex : 63 - squareIndex)];

    /// <summary>The evaluation's midgame material value; 0 for the king and for no piece.</summary>
    internal static int MaterialMidgame(PieceType type) =>
        type is >= PieceType.Pawn and <= PieceType.King ? V[MaterialMg + (int)type - 1] : 0;

    /// <summary>The evaluation's endgame material value; 0 for the king and for no piece.</summary>
    internal static int MaterialEndgame(PieceType type) =>
        type is >= PieceType.Pawn and <= PieceType.King ? V[MaterialEg + (int)type - 1] : 0;

    // ── Defaults ─────────────────────────────────────────────────────────────

    private static EvalParams BuildDefault()
    {
        var v = new int[Count];

        for (int t = 0; t < PieceTypes; t++)
        {
            var type = (PieceType)(t + 1);
            v[MaterialMg + t] = type.MaterialValue();
            v[MaterialEg + t] = PieceSquareTables.DefaultEndgameMaterialValue(type);

            int[][] mg = PieceSquareTables.TableFor(type);
            int[][] eg = PieceSquareTables.EndgameTableFor(type);
            for (int rank = 0; rank < 8; rank++)
                for (int file = 0; file < 8; file++)
                {
                    v[PstMg + t * 64 + rank * 8 + file] = mg[rank][file];
                    v[PstEg + t * 64 + rank * 8 + file] = eg[rank][file];
                }
        }

        // Each default lives beside the comment that justifies it; this only collects them.
        v[DoubledMg]      = Evaluator.DefaultDoubledMidgame;     v[DoubledEg]      = Evaluator.DefaultDoubledEndgame;
        v[IsolatedMg]     = Evaluator.DefaultIsolatedMidgame;    v[IsolatedEg]     = Evaluator.DefaultIsolatedEndgame;
        for (int r = 0; r < 8; r++)
        {
            v[PassedMg + r] = PassedPawns.DefaultMidgameByRank[r];
            v[PassedEg + r] = PassedPawns.DefaultEndgameByRank[r];
        }
        v[ProtectedMg]    = PassedPawns.DefaultProtectedMidgame; v[ProtectedEg]    = PassedPawns.DefaultProtectedEndgame;
        v[ConnectedMg]    = PassedPawns.DefaultConnectedMidgame; v[ConnectedEg]    = PassedPawns.DefaultConnectedEndgame;
        v[BishopPairMg]   = Evaluator.DefaultBishopPairMidgame;  v[BishopPairEg]   = Evaluator.DefaultBishopPairEndgame;
        v[RookOpenMg]     = RookFiles.DefaultOpenMidgame;        v[RookOpenEg]     = RookFiles.DefaultOpenEndgame;
        v[RookHalfOpenMg] = RookFiles.DefaultHalfOpenMidgame;    v[RookHalfOpenEg] = RookFiles.DefaultHalfOpenEndgame;
        v[RookSeventhMg]  = RookFiles.DefaultSeventhMidgame;     v[RookSeventhEg]  = RookFiles.DefaultSeventhEndgame;

        for (int d = 0; d < 4; d++)
        {
            v[KingShelterMg + d] = KingSafety.DefaultShelterMidgame[d];
            v[KingStormMg   + d] = KingSafety.DefaultStormMidgame[d];
            // The endgame scale stays 0: a king that has left its shield behind in an endgame is
            // doing the right thing, and the taper is what makes that so.
        }

        return new EvalParams(v);
    }

    // ── Names and JSON ───────────────────────────────────────────────────────

    private static readonly string[] PieceNames = { "Pawn", "Knight", "Bishop", "Rook", "Queen", "King" };

    /// <summary>A stable, human-readable name for each weight, e.g. "PstEg.Knight.e4".</summary>
    public static string NameOf(int index)
    {
        static string Sq(int s) => $"{(char)('a' + (s & 7))}{(s >> 3) + 1}";

        if (index < MaterialEg) return $"MaterialMg.{PieceNames[index - MaterialMg]}";
        if (index < PstMg)      return $"MaterialEg.{PieceNames[index - MaterialEg]}";
        if (index < PstEg)      return $"PstMg.{PieceNames[(index - PstMg) / 64]}.{Sq((index - PstMg) % 64)}";
        if (index < DoubledMg)  return $"PstEg.{PieceNames[(index - PstEg) / 64]}.{Sq((index - PstEg) % 64)}";
        if (index >= PassedMg && index < PassedEg)    return $"PassedMg.Rank{index - PassedMg + 1}";
        if (index >= PassedEg && index < ProtectedMg) return $"PassedEg.Rank{index - PassedEg + 1}";
        if (index >= KingShelterMg && index < KingShelterEg) return $"KingShelterMg.D{index - KingShelterMg}";
        if (index >= KingShelterEg && index < KingStormMg)   return $"KingShelterEg.D{index - KingShelterEg}";
        if (index >= KingStormMg   && index < KingStormEg)   return $"KingStormMg.D{index - KingStormMg}";
        if (index >= KingStormEg   && index < Count)         return $"KingStormEg.D{index - KingStormEg}";

        return index switch
        {
            DoubledMg => "DoubledMg", DoubledEg => "DoubledEg",
            IsolatedMg => "IsolatedMg", IsolatedEg => "IsolatedEg",
            ProtectedMg => "ProtectedMg", ProtectedEg => "ProtectedEg",
            ConnectedMg => "ConnectedMg", ConnectedEg => "ConnectedEg",
            BishopPairMg => "BishopPairMg", BishopPairEg => "BishopPairEg",
            RookOpenMg => "RookOpenMg", RookOpenEg => "RookOpenEg",
            RookHalfOpenMg => "RookHalfOpenMg", RookHalfOpenEg => "RookHalfOpenEg",
            RookSeventhMg => "RookSeventhMg", RookSeventhEg => "RookSeventhEg",
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    /// <summary>
    /// Saves as a JSON object of name → value, in index order. Named rather than positional so a
    /// file stays readable and survives a new weight being added: <see cref="Load"/> matches by name.
    /// </summary>
    public void Save(string path)
    {
        var named = new Dictionary<string, int>(Count);
        for (int i = 0; i < Count; i++) named[NameOf(i)] = Values[i];
        File.WriteAllText(path, JsonSerializer.Serialize(named, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Loads a file written by <see cref="Save"/>. Weights the file does not name keep their
    /// default; a name the vector does not know is an error, because it means a file and a build
    /// disagree about what the weights are.
    /// </summary>
    public static EvalParams Load(string path)
    {
        var named = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
                    ?? throw new InvalidDataException($"{path}: not a weight file");

        var byName = new Dictionary<string, int>(Count);
        for (int i = 0; i < Count; i++) byName[NameOf(i)] = i;

        var values = (int[])Default.Values.Clone();
        foreach (var (name, value) in named)
        {
            if (!byName.TryGetValue(name, out int index))
                throw new InvalidDataException($"{path}: unknown weight '{name}'");
            values[index] = value;
        }

        return new EvalParams(values);
    }
}
