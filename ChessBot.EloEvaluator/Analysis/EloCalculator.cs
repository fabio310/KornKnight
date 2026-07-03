namespace ChessBot.EloEvaluator.Analysis;

/// <summary>
/// Computed Elo statistics for a set of games.
/// </summary>
public sealed class EloStats
{
    public int     N                   { get; set; }
    public int     Wins                { get; set; }
    public int     Draws               { get; set; }
    public int     Losses              { get; set; }
    public double  ScoreRate           { get; set; }
    public double  ScoreSE             { get; set; }   // standard error of score rate
    public double  EloDiff             { get; set; }
    public double? EstimatedElo        { get; set; }
    public double  ConfidenceInterval95 { get; set; }
    public double  LOS                 { get; set; }   // likelihood of superiority [0,1]
    public bool    IsReliable          { get; set; }
    public string  ReliabilityNote     { get; set; } = string.Empty;
}

/// <summary>
/// Elo mathematics: score rate → Elo diff, confidence interval, LOS.
///
/// Formulas
/// ─────────
/// Score rate:  s  = (W + 0.5·D) / N
/// Elo diff:    Δ  = −400 · log₁₀(1/s − 1)
/// SE(s):       sqrt[(W·(1−s)² + D·(0.5−s)² + L·s²) / (N·(N−1))]
/// 95% CI Δ:    1.96 · SE(s) · (400 / (ln10 · s · (1−s)))   [delta method]
/// LOS:         Φ[(W − L) / √(W + L)]                        [normal approx]
///
/// Reliability thresholds (no fake Elo)
/// ──────────────────────────────────────
///   N &lt; 5              → "Insufficient data"
///   N &lt; 20 OR CI &gt; 400 → "Low confidence"
///   N &lt; 50 OR CI &gt; 200 → "Moderate confidence"
///   otherwise          → "Adequate confidence"
/// </summary>
public static class EloCalculator
{
    private const double Ln10 = 2.302585092994046;

    public static EloStats Calculate(int wins, int draws, int losses,
                                     double? referenceElo = null)
    {
        int n = wins + draws + losses;
        var s = new EloStats { N = n, Wins = wins, Draws = draws, Losses = losses };

        if (n == 0)
        {
            s.IsReliable     = false;
            s.ReliabilityNote = "No games — cannot evaluate.";
            return s;
        }

        double score = (wins + 0.5 * draws) / n;
        s.ScoreRate = score;

        // ── Elo difference ───────────────────────────────────────────────────
        double sc = Math.Clamp(score, 0.0005, 0.9995);
        s.EloDiff = -400.0 * Math.Log10(1.0 / sc - 1.0);

        if (referenceElo.HasValue)
            s.EstimatedElo = referenceElo.Value + s.EloDiff;

        // ── Standard error of score rate ─────────────────────────────────────
        double se = 0;
        if (n > 1)
        {
            double varSum = wins   * Math.Pow(1.0 - score, 2) +
                            draws  * Math.Pow(0.5 - score, 2) +
                            losses * Math.Pow(0.0 - score, 2);
            se = Math.Sqrt(varSum / ((double)n * (n - 1)));
        }
        s.ScoreSE = se;

        // ── 95% CI on Elo via delta method ───────────────────────────────────
        double ci = 0;
        if (se > 0 && sc > 0.001 && sc < 0.999)
        {
            double dEloDs = 400.0 / (Ln10 * sc * (1.0 - sc));
            ci = 1.96 * se * dEloDs;
        }
        s.ConfidenceInterval95 = ci;

        // ── Likelihood of Superiority (normal approximation) ─────────────────
        double los = 0.5;
        if (wins + losses > 0)
        {
            double z = (wins - losses) / Math.Sqrt(wins + losses);
            los = NormalCdf(z);
        }
        s.LOS = los;

        // ── Reliability verdict ───────────────────────────────────────────────
        if (n < 5)
        {
            s.IsReliable     = false;
            s.ReliabilityNote = $"Insufficient confidence — only {n} game(s). At least 5 required.";
        }
        else if (n < 20 || ci > 400)
        {
            s.IsReliable     = false;
            s.ReliabilityNote = $"Low confidence — {n} game(s), CI ±{ci:F0} Elo.";
        }
        else if (n < 50 || ci > 200)
        {
            s.IsReliable     = false;
            s.ReliabilityNote = $"Moderate confidence — {n} game(s), CI ±{ci:F0} Elo.";
        }
        else
        {
            s.IsReliable     = true;
            s.ReliabilityNote = $"Adequate confidence — {n} game(s), CI ±{ci:F0} Elo.";
        }

        return s;
    }

    // Abramowitz & Stegun (26.2.17) rational approximation; max error ≈ 7.5×10⁻⁸
    private static double NormalCdf(double z)
    {
        const double p  = 0.2316419;
        const double b1 =  0.319381530;
        const double b2 = -0.356563782;
        const double b3 =  1.781477937;
        const double b4 = -1.821255978;
        const double b5 =  1.330274429;

        double t   = 1.0 / (1.0 + p * Math.Abs(z));
        double poly = t * (b1 + t * (b2 + t * (b3 + t * (b4 + t * b5))));
        double pdf  = Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);
        double cdf  = 1.0 - pdf * poly;
        return z >= 0 ? cdf : 1.0 - cdf;
    }
}
