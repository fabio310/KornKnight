namespace ChessBot.MatchRunner;

/// <summary>
/// Configuration for a match between ChessBot and an external UCI engine.
/// </summary>
public class MatchConfig
{
    /// <summary>Path to the external UCI engine executable.</summary>
    public string ExternalEnginePath { get; set; } = string.Empty;

    /// <summary>Milliseconds per move for both engines.</summary>
    public int MoveTimeMs { get; set; } = 1000;

    /// <summary>Number of games to play (each side plays both colors).</summary>
    public int GamesPerSide { get; set; } = 1;

    /// <summary>Directory to save PGN files.</summary>
    public string PgnOutputDir { get; set; } = "pgns";

    /// <summary>Centipawn swing that counts as a blunder.</summary>
    public int BlunderThresholdCp { get; set; } = 200;

    /// <summary>Minimum engine score difference for first-blunder detection.</summary>
    public int FirstSwingThresholdCp { get; set; } = 150;

    /// <summary>Whether to emit verbose move-by-move output to console.</summary>
    public bool Verbose { get; set; } = true;

    /// <summary>
    /// Parse command-line arguments into a MatchConfig.
    /// Supported flags:
    ///   --engine &lt;path&gt;
    ///   --time &lt;ms&gt;
    ///   --games &lt;n&gt;
    ///   --pgn-dir &lt;dir&gt;
    ///   --blunder &lt;cp&gt;
    ///   --quiet
    /// </summary>
    public static MatchConfig Parse(string[] args)
    {
        var cfg = new MatchConfig();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine" when i + 1 < args.Length:
                    cfg.ExternalEnginePath = args[++i];
                    break;
                case "--time" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int ms)) cfg.MoveTimeMs = ms;
                    break;
                case "--games" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int g)) cfg.GamesPerSide = g;
                    break;
                case "--pgn-dir" when i + 1 < args.Length:
                    cfg.PgnOutputDir = args[++i];
                    break;
                case "--blunder" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int b)) cfg.BlunderThresholdCp = b;
                    break;
                case "--quiet":
                    cfg.Verbose = false;
                    break;
            }
        }
        return cfg;
    }
}
