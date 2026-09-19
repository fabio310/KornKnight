using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// The tunable weight vector: that it round-trips through its file format, that every weight has
/// a distinct name, and that the evaluation really reads it — a vector nothing reads would pass
/// the fingerprint test and tune nothing.
/// </summary>
[Collection(SerialCollection.Name)]   // one test swaps the process-wide active vector
public class EvalParamsTests
{
    [Fact]
    public void EveryWeightHasADistinctName()
    {
        var names = Enumerable.Range(0, EvalParams.Count).Select(EvalParams.NameOf).ToList();

        Assert.Equal(EvalParams.Count, names.Distinct().Count());
        Assert.Contains("PstEg.Knight.e4", names);
        Assert.Contains("PassedMg.Rank7", names);
        Assert.Contains("RookSeventhEg", names);
    }

    [Fact]
    public void SaveThenLoad_ReturnsTheSameVector()
    {
        string path = Path.GetTempFileName();
        try
        {
            var tuned = EvalParams.Default.Clone();
            tuned.Values[EvalParams.BishopPairMg] = 31;
            tuned.Values[EvalParams.PstEg + 3 * 64 + 7] = -12;

            tuned.Save(path);
            var loaded = EvalParams.Load(path);

            Assert.Equal(tuned.Values, loaded.Values);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_RejectsAWeightTheBuildDoesNotKnow()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ \"BishopPairMg\": 30, \"KingSafetyShield\": 12 }");
            Assert.Throws<InvalidDataException>(() => EvalParams.Load(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Raising the evaluation's knight value must raise the score of a position a knight up, and
    /// restoring the default must restore the recorded fingerprint exactly. Checked on the full scan,
    /// which reads the vector directly (a board's incremental accumulators were summed under the
    /// vector active when it was set up).
    /// </summary>
    [Fact]
    public void TheEvaluationReadsTheActiveVector()
    {
        var engine = new ChessEngine(ChessEngine.MinHashSizeMb);
        engine.LoadFen("4k3/8/8/8/8/8/8/4KN2 w - - 0 1");
        var evaluator = new Evaluator();
        int before = evaluator.Evaluate(engine.GetBoardSnapshot());

        var tuned = EvalParams.Default.Clone();
        tuned.Values[EvalParams.MaterialMg + (int)PieceType.Knight - 1] += 100;
        tuned.Values[EvalParams.MaterialEg + (int)PieceType.Knight - 1] += 100;

        try
        {
            EvalParams.Active = tuned;
            int after = evaluator.Evaluate(engine.GetBoardSnapshot());
            Assert.Equal(before + 100, after);
        }
        finally
        {
            EvalParams.Active = EvalParams.Default;
        }

        Assert.Equal(before, evaluator.Evaluate(engine.GetBoardSnapshot()));
        Assert.Equal(EvalFingerprintTests.RecordedHash, EvalFingerprintTests.Fingerprint(out _, out int disagreements));
        Assert.Equal(0, disagreements);
    }

    /// <summary>
    /// The evaluation's material is not move ordering's: tuning the evaluation's knight must leave
    /// PieceType.MaterialValue — which ranks captures and scores exchanges — untouched.
    /// </summary>
    [Fact]
    public void TuningTheEvaluationsMaterialLeavesExchangeValuesAlone()
    {
        Assert.Equal(PieceType.Knight.MaterialValue(),
                     EvalParams.Default.Values[EvalParams.MaterialMg + (int)PieceType.Knight - 1]);

        var tuned = EvalParams.Default.Clone();
        tuned.Values[EvalParams.MaterialMg + (int)PieceType.Knight - 1] = 999;
        try
        {
            EvalParams.Active = tuned;
            Assert.Equal(320, PieceType.Knight.MaterialValue());
        }
        finally { EvalParams.Active = EvalParams.Default; }
    }
}
