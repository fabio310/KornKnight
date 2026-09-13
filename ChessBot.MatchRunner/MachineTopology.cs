namespace ChessBot.MatchRunner;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// What the machine actually offers, and how a timed run takes hold of it.
///
/// Two facts the harness previously guessed at live here. The first is how many cores there
/// really are: <see cref="Environment.ProcessorCount"/> counts hardware threads, and two
/// hyperthreads on one core do not run two searches at full speed, so sizing a run by it
/// oversubscribes the machine by a factor of two. The second is where a worker runs: without
/// pinning, the scheduler migrates a search between cores and its transposition table and
/// history tables have to be pulled into a cold L2/L3 each time. One past timed run varied
/// between 367k and 2,373k NPS within itself for that reason alone, which is a six-fold spread
/// in a number the run existed to measure.
/// </summary>
public static class MachineTopology
{
    /// <summary>
    /// Physical cores, counted from the OS topology rather than inferred. Falls back to half the
    /// logical processor count (the usual hyperthreading ratio) and finally to the logical count
    /// itself; <see cref="CoreCountSource"/> says which of those actually happened, because a
    /// fallback that looks like a measurement is exactly what the old <c>ProcessorCount / 2</c>
    /// was.
    /// </summary>
    public static int PhysicalCoreCount { get; }

    /// <summary>Hardware threads, i.e. <see cref="Environment.ProcessorCount"/>.</summary>
    public static int LogicalProcessorCount => Environment.ProcessorCount;

    /// <summary>How <see cref="PhysicalCoreCount"/> was obtained; recorded in every manifest.</summary>
    public static string CoreCountSource { get; }

    /// <summary>True when this platform can pin a worker thread to one core.</summary>
    public static bool SupportsCorePinning =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    static MachineTopology()
    {
        var (cores, source) = DetectPhysicalCores();
        PhysicalCoreCount = cores;
        CoreCountSource   = source;
    }

    // ── Core counting ────────────────────────────────────────────────────────

    private static (int cores, string source) DetectPhysicalCores()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                int n = CountWindowsProcessorCores();
                if (n > 0) return (n, "windows:GetLogicalProcessorInformationEx");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                int n = CountLinuxProcessorCores();
                if (n > 0) return (n, "linux:sysfs-thread_siblings_list");
            }
        }
        catch
        {
            // Any failure falls through to the estimate below. A wrong core count must not stop
            // a run; it must only stop the run claiming the number was measured.
        }

        int logical = Environment.ProcessorCount;
        return logical >= 2
            ? (logical / 2, "estimate:logical/2 (topology query unavailable)")
            : (Math.Max(1, logical), "estimate:logical (topology query unavailable)");
    }

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref int returnedLength);

    /// <summary>
    /// Counts SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records of type RelationProcessorCore.
    /// Only the leading relationship and size fields of each record are read: the remainder is a
    /// union whose layout would have to be mirrored here for no gain, and the record count under
    /// that filter already <em>is</em> the physical core count.
    /// </summary>
    private static int CountWindowsProcessorCores()
    {
        int length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || length <= 0) return 0;

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return 0;

            int cores = 0;
            int offset = 0;
            while (offset + 8 <= length)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size         = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;                    // malformed: stop rather than spin
                if (relationship == RelationProcessorCore) cores++;
                offset += size;
            }
            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Counts distinct sibling groups under /sys: every hyperthread on one core reports the same
    /// thread_siblings_list, so the number of distinct lists is the number of physical cores.
    /// </summary>
    private static int CountLinuxProcessorCores()
    {
        const string cpuRoot = "/sys/devices/system/cpu";
        if (!Directory.Exists(cpuRoot)) return 0;

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in Directory.EnumerateDirectories(cpuRoot, "cpu*"))
        {
            string siblings = Path.Combine(dir, "topology", "thread_siblings_list");
            if (File.Exists(siblings))
                groups.Add(File.ReadAllText(siblings).Trim());
        }
        return groups.Count;
    }

    // ── Process priority ─────────────────────────────────────────────────────

    /// <summary>
    /// Raises the process above Normal for a timed run and reports what the OS actually granted,
    /// not what was asked for: on Linux, lowering the nice value needs privileges this process
    /// usually does not have, so a request that silently failed would otherwise be recorded in
    /// the manifest as though it had worked.
    /// </summary>
    public static string RaiseProcessPriority()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.High;
            process.Refresh();
            return process.PriorityClass.ToString();
        }
        catch (Exception ex)
        {
            try   { return $"{Process.GetCurrentProcess().PriorityClass} (raise refused: {ex.GetType().Name})"; }
            catch { return $"unknown (raise refused: {ex.GetType().Name})"; }
        }
    }

    // ── Thread affinity ──────────────────────────────────────────────────────

    /// <summary>cpu_set_t is 1024 bits on glibc; sized here rather than assumed at the call site.</summary>
    private const int CpuSetBytes = 128;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr affinityMask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, IntPtr cpuSetSize, byte[] mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, IntPtr cpuSetSize, byte[] mask);

    /// <summary>
    /// Confines the calling OS thread to one logical processor until the returned handle is
    /// disposed, restoring the previous affinity on the way out — these run on pooled threads,
    /// which are handed to unrelated work afterwards.
    ///
    /// Only meaningful while the caller does no <c>await</c>: a continuation may resume on a
    /// different thread, which would leave the pin behind on the wrong one. Every caller here
    /// therefore runs a whole game synchronously inside the pin.
    ///
    /// Returns null when the platform or the OS refused, which is a run detail worth recording
    /// but never a reason to stop playing games.
    /// </summary>
    public static IDisposable? PinCurrentThreadToCore(int logicalProcessor)
    {
        if (logicalProcessor < 0 || logicalProcessor >= LogicalProcessorCount) return null;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Thread.BeginThreadAffinity();
                var previous = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << logicalProcessor));
                if (previous == UIntPtr.Zero)
                {
                    Thread.EndThreadAffinity();
                    return null;
                }
                return new WindowsPin(previous);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var previous = new byte[CpuSetBytes];
                if (sched_getaffinity(0, (IntPtr)CpuSetBytes, previous) != 0) return null;

                var mask = new byte[CpuSetBytes];
                mask[logicalProcessor / 8] = (byte)(1 << (logicalProcessor % 8));

                Thread.BeginThreadAffinity();
                if (sched_setaffinity(0, (IntPtr)CpuSetBytes, mask) != 0)
                {
                    Thread.EndThreadAffinity();
                    return null;
                }
                return new LinuxPin(previous);
            }
        }
        catch
        {
            // Pinning stabilises timings; it is never a precondition for playing a game.
        }

        return null;
    }

    private sealed class WindowsPin(UIntPtr previousMask) : IDisposable
    {
        public void Dispose()
        {
            SetThreadAffinityMask(GetCurrentThread(), previousMask);
            Thread.EndThreadAffinity();
        }
    }

    private sealed class LinuxPin(byte[] previousMask) : IDisposable
    {
        public void Dispose()
        {
            sched_setaffinity(0, (IntPtr)CpuSetBytes, previousMask);
            Thread.EndThreadAffinity();
        }
    }
}

/// <summary>
/// Hands each concurrently running game a logical processor of its own and takes it back when
/// the game ends, so N concurrent games occupy N distinct cores instead of landing wherever the
/// scheduler puts them. Cores are handed out lowest-first, so a run that never reaches full
/// concurrency stays on the low-numbered ones.
/// </summary>
public sealed class CoreSlotPool
{
    private readonly Stack<int> _free;
    private readonly object _gate = new();

    /// <param name="slots">How many cores to hand out, normally the run's concurrency.</param>
    /// <param name="stride">
    /// Spacing between handed-out logical processors. 2 on a hyperthreaded machine gives each
    /// game its own physical core instead of pairing two games onto the two threads of one core,
    /// where they would contend for the same L1 and L2 — the contention pinning exists to remove.
    /// </param>
    public CoreSlotPool(int slots, int stride = 1)
    {
        int logical = Math.Max(1, MachineTopology.LogicalProcessorCount);
        stride = Math.Max(1, stride);

        var cores = new List<int>(Math.Max(0, slots));
        for (int i = 0; i < slots; i++)
            cores.Add((i * stride) % logical);

        cores.Reverse();                     // so the stack pops them in ascending order again
        _free = new Stack<int>(cores);
    }

    /// <summary>Stride that gives each game its own physical core on this machine.</summary>
    public static int StrideForThisMachine =>
        MachineTopology.PhysicalCoreCount > 0 &&
        MachineTopology.LogicalProcessorCount / MachineTopology.PhysicalCoreCount >= 2 ? 2 : 1;

    /// <summary>Takes a core, or -1 when the pool is empty — a caller that then runs unpinned.</summary>
    public int Take()
    {
        lock (_gate) return _free.Count > 0 ? _free.Pop() : -1;
    }

    public void Return(int core)
    {
        if (core < 0) return;
        lock (_gate) _free.Push(core);
    }
}
