namespace ChessBot.MatchRunner;

using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// How much of the machine somebody else is using.
///
/// A run sizes itself from the cores it can have, not from the cores that exist. Those are the
/// same number only on an idle box, and a measurement rig that assumes an idle box produces its
/// worst numbers exactly when the operator is doing something else — which is when they are least
/// likely to notice.
///
/// "Somebody else" is everything that is not this run: this process, and every engine process it
/// started. Engines are registered by process id rather than by name, because another A/B run on
/// the same machine is external load and must be yielded to, even though its processes are called
/// the same thing as ours.
///
/// Sampling walks the process table, which is cheap at the seconds-scale interval the governor
/// uses and would not be at a faster one. Per-process CPU deltas are kept rather than one global
/// total, because a process that exits between samples takes its accumulated time with it and a
/// naive total would go backwards.
/// </summary>
public static class MachineLoad
{
    private static readonly ConcurrentDictionary<int, byte> OurPids = new();
    private static readonly Dictionary<int, TimeSpan> LastCpu = new();
    private static readonly object SampleLock = new();
    private static long _lastSampleTicks;

    /// <summary>
    /// Counts a process as part of this run, so its CPU is not read as somebody else's. Called
    /// when an engine starts.
    /// </summary>
    public static void RegisterOurs(int pid) => OurPids[pid] = 0;

    /// <summary>Stops counting a process as ours. Called when an engine exits.</summary>
    public static void ForgetOurs(int pid) => OurPids.TryRemove(pid, out _);

    /// <summary>
    /// Cores' worth of CPU consumed by everything that is not this run, since the previous call.
    /// The first call has no previous sample to difference against and returns 0.
    ///
    /// Not thread-safe by accident: the governor is the only caller and calls it on one timer.
    /// </summary>
    public static double SampleExternalCores()
    {
        lock (SampleLock)
        {
            long now = Stopwatch.GetTimestamp();
            var current = new Dictionary<int, TimeSpan>();
            int ourPid = Environment.ProcessId;

            TimeSpan externalTotal = TimeSpan.Zero;

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    int pid = process.Id;
                    TimeSpan cpu = process.TotalProcessorTime;
                    current[pid] = cpu;

                    if (pid == ourPid || OurPids.ContainsKey(pid)) continue;

                    // Only the increase counts. A process first seen this sample contributes
                    // nothing: its lifetime total is not work done during the interval.
                    if (LastCpu.TryGetValue(pid, out TimeSpan previous) && cpu > previous)
                        externalTotal += cpu - previous;
                }
                catch
                {
                    // Protected and already-exited processes cannot be read. Skipping one
                    // understates external load slightly, which is the safe direction: it makes
                    // the governor less eager to give the machine away than it should be, never
                    // more eager to take it.
                }
                finally
                {
                    process.Dispose();
                }
            }

            double elapsedSeconds = _lastSampleTicks == 0
                ? 0
                : (now - _lastSampleTicks) / (double)Stopwatch.Frequency;

            LastCpu.Clear();
            foreach (var entry in current) LastCpu[entry.Key] = entry.Value;
            _lastSampleTicks = now;

            if (elapsedSeconds <= 0) return 0;

            return externalTotal.TotalSeconds / elapsedSeconds;
        }
    }

    /// <summary>Forgets the sampling history, so the next sample starts a fresh interval.</summary>
    internal static void Reset()
    {
        lock (SampleLock)
        {
            LastCpu.Clear();
            _lastSampleTicks = 0;
        }
    }
}
