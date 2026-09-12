namespace ChessBot.MatchRunner;

/// <summary>What a sequential test has concluded so far.</summary>
public enum SprtVerdict
{
    /// <summary>Neither bound reached: the games so far do not settle the question.</summary>
    Continue,

    /// <summary>H1 accepted — the change is at least as strong as elo1.</summary>
    AcceptH1,

    /// <summary>H0 accepted — the change is no stronger than elo0.</summary>
    AcceptH0,
}

/// <summary>
/// The hypotheses and error rates of a sequential probability ratio test.
///
/// H0 is "B is elo0 stronger than A" and H1 is "B is elo1 stronger". The usual pair is
/// elo0 = 0, elo1 = 5: is this change worth anything at all? alpha is the chance of adopting a
/// change that is not an improvement, beta the chance of discarding one that is.
/// </summary>
public sealed class SprtSettings
{
    public double Elo0  { get; init; }
    public double Elo1  { get; init; } = 5;
    public double Alpha { get; init; } = 0.05;
    public double Beta  { get; init; } = 0.05;

    // There is deliberately no game cap here: --ab-games is the run's length, and an SPRT that
    // never reaches a bound simply plays it out and reports "inconclusive at n games", which is
    // a finding. A second cap would only be a second place for the run's length to be decided.

    /// <summary>Reject H0 at or above this LLR.</summary>
    public double UpperBound => Math.Log((1 - Beta) / Alpha);

    /// <summary>Accept H0 at or below this LLR.</summary>
    public double LowerBound => Math.Log(Beta / (1 - Alpha));

    /// <summary>
    /// Rejects parameters that are not a test, before a run is started on them.
    ///
    /// Alpha and beta outside (0, 1) are not error rates, and they make the bounds NaN rather
    /// than merely wrong — a NaN compares false against everything, so the run would play every
    /// game and report an SPRT that could never conclude. Failing here says which value was bad;
    /// failing later says nothing at all.
    /// </summary>
    public void Validate()
    {
        if (!(Alpha > 0 && Alpha < 1))
            throw new ArgumentException($"SPRT alpha must be between 0 and 1 (exclusive); got {Alpha}.");
        if (!(Beta > 0 && Beta < 1))
            throw new ArgumentException($"SPRT beta must be between 0 and 1 (exclusive); got {Beta}.");
        if (!double.IsFinite(Elo0) || !double.IsFinite(Elo1))
            throw new ArgumentException($"SPRT elo0 and elo1 must be finite; got {Elo0} and {Elo1}.");
        if (Elo0 >= Elo1)
            throw new ArgumentException(
                $"SPRT elo0 ({Elo0}) must be below elo1 ({Elo1}): H0 is the weaker hypothesis.");
    }

    public override string ToString() =>
        $"H0: elo0={Elo0:0.##}  H1: elo1={Elo1:0.##}  alpha={Alpha:0.###} beta={Beta:0.###}  " +
        $"bounds [{LowerBound:F3}, {UpperBound:F3}]";
}

/// <summary>The running state of the test after some number of games.</summary>
public readonly record struct SprtState(
    int Games, int WinsB, int Draws, int WinsA, double Llr, SprtVerdict Verdict, string Explanation)
{
    public bool IsConclusive => Verdict != SprtVerdict.Continue;
}

/// <summary>
/// Sequential test over the running score, so a run stops as soon as it is conclusive instead of
/// playing a fixed number of games and asking afterwards.
///
/// The likelihood ratio is the normal approximation over per-game scores: each game contributes
/// 1, 0.5 or 0, and the test compares the observed mean against the two hypothesised means using
/// the observed variance. This is the standard approximate LLR, not the exact trinomial one, and
/// it treats games as independent — which openings played as colour-reversed pairs are not
/// quite. Both simplifications make the test slightly conservative here rather than
/// over-eager: correlated pairs mean the true information per game is a little lower than
/// assumed, so a bound is reached no earlier than it should be. It is documented rather than
/// hidden because it is the difference between this and a pentanomial test.
/// </summary>
public static class Sprt
{
    /// <summary>Expected score for a given Elo advantage under the logistic model.</summary>
    public static double EloToScore(double elo) => 1.0 / (1.0 + Math.Pow(10, -elo / 400.0));

    /// <summary>
    /// Elo implied by a score rate. Undefined at 0 and 1, where the logistic estimate is
    /// infinite, so those return null rather than an enormous number that looks like a result.
    /// </summary>
    public static double? ScoreToElo(double score) =>
        score <= 0 || score >= 1 ? null : -400 * Math.Log10(1 / score - 1);

    /// <summary>
    /// The test's state after <paramref name="winsB"/>/<paramref name="draws"/>/<paramref name="winsA"/>,
    /// counted from B's point of view.
    /// </summary>
    public static SprtState Evaluate(SprtSettings settings, int winsB, int draws, int winsA)
    {
        int games = winsB + draws + winsA;
        if (games == 0)
            return new SprtState(0, 0, 0, 0, 0, SprtVerdict.Continue, "no games yet");

        double mean = (winsB + 0.5 * draws) / games;

        // Variance of the per-game scores. A run in which every game had the same result has
        // zero observed variance, which the ratio cannot divide by; until a second distinct
        // result appears there is no scale on which to measure a difference, so the test simply
        // continues.
        double variance =
            (winsB * Math.Pow(1 - mean, 2) + draws * Math.Pow(0.5 - mean, 2) + winsA * Math.Pow(0 - mean, 2))
            / games;

        if (variance <= 0)
            return new SprtState(games, winsB, draws, winsA, 0, SprtVerdict.Continue,
                "every game so far had the same result, so there is no variance to test against");

        double s0 = EloToScore(settings.Elo0);
        double s1 = EloToScore(settings.Elo1);

        double llr = games * (s1 - s0) * (2 * mean - s0 - s1) / (2 * variance);

        var verdict = llr >= settings.UpperBound ? SprtVerdict.AcceptH1
                    : llr <= settings.LowerBound ? SprtVerdict.AcceptH0
                    : SprtVerdict.Continue;

        string explanation = verdict switch
        {
            SprtVerdict.AcceptH1 =>
                $"LLR {llr:F3} reached the upper bound {settings.UpperBound:F3}: B is at least " +
                $"elo1={settings.Elo1:0.##} stronger, at alpha={settings.Alpha:0.###}",
            SprtVerdict.AcceptH0 =>
                $"LLR {llr:F3} reached the lower bound {settings.LowerBound:F3}: B is no more than " +
                $"elo0={settings.Elo0:0.##} stronger, at beta={settings.Beta:0.###}",
            _ =>
                $"LLR {llr:F3} is inside [{settings.LowerBound:F3}, {settings.UpperBound:F3}] " +
                $"after {games} games",
        };

        return new SprtState(games, winsB, draws, winsA, llr, verdict, explanation);
    }
}
