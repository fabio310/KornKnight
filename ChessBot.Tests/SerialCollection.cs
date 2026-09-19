using Xunit;

namespace ChessBot.Tests;

/// <summary>
/// Tests that measure wall-clock time or process memory, run on their own. xunit runs a
/// collection with parallelization disabled after every parallel test has finished, and runs
/// its members one at a time.
///
/// Needed because the rest of the suite saturates the machine: every class runs in parallel,
/// perft among them, and the UCI tests each hold a thread-pool thread for their search. Under
/// that load a 100 ms search that answers in 96 ms alone was measured at 104-159 ms, and a
/// CancelAfter(100) fired late enough for a search to reach depth 11 — measurements of the test
/// host, not of the engine. The heap measurement of the soak test is the whole process's, and
/// is only the engine's when nothing else is running.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial: timing and memory";
}
