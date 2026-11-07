namespace Wtba.Collector;

/// <summary>
/// Pure aggregation of already-decoded events (S9: no command lines are ever accepted).
/// cpu_ms = samples x interval; context switches counted by incoming (new) thread's process;
/// wakeup = switch whose OLD thread is the idle thread (tid 0, pid 0), attributed to the new process.
/// Events whose pid is unknown (-1) are buffered per thread id and resolved lazily in Result()
/// from the tid->pid map (thread start/rundown events, which may arrive at the end of the trace).
/// </summary>
public sealed class TraceAggregator
{
    public const double DefaultIntervalMs = 1.0;
    public const string UnattributedName = "(unattributed)";
    public const int UnattributedPid = -1;

    private sealed class Acc { public long Cpu, Cs, Wake; }
    private readonly Dictionary<int, string> _names = new();
    private readonly Dictionary<int, Acc> _acc = new();
    private readonly Dictionary<int, Acc> _pendingByTid = new();
    private readonly Dictionary<int, int> _tidToPid = new();
    public double IntervalMs { get; private set; } = DefaultIntervalMs;

    public void SetSampleIntervalMs(double ms) { if (ms > 0) IntervalMs = ms; }
    public void OnProcess(int pid, string imageName) { if (!string.IsNullOrEmpty(imageName)) _names[pid] = imageName; }

    /// <summary>Thread start / rundown event: thread tid belongs to process pid (last write wins).</summary>
    public void OnThread(int tid, int pid) { if (pid > 0) _tidToPid[tid] = pid; }

    private static Acc Get(Dictionary<int, Acc> d, int key) { if (!d.TryGetValue(key, out var a)) d[key] = a = new Acc(); return a; }

    private Acc Target(int pid, int tid) => pid < 0 ? Get(_pendingByTid, tid) : Get(_acc, pid);

    public void OnCpuSample(int pid, int tid = 0) { if (pid != 0) Target(pid, tid).Cpu++; }

    public void OnContextSwitch(int oldPid, int oldTid, int newPid, int newTid)
    {
        if (newTid == 0 && newPid <= 0) return; // switching to idle
        var a = Target(newPid, newTid);
        a.Cs++;
        if (oldTid == 0 && oldPid <= 0) a.Wake++;
    }

    public IReadOnlyList<ProcessStat> Result()
    {
        // Copy so Result() is repeatable and does not mutate state.
        var merged = _acc.ToDictionary(kv => kv.Key, kv => new Acc { Cpu = kv.Value.Cpu, Cs = kv.Value.Cs, Wake = kv.Value.Wake });
        var unattr = new Acc();
        foreach (var (tid, p) in _pendingByTid)
        {
            var dst = _tidToPid.TryGetValue(tid, out var pid) ? Get(merged, pid) : unattr;
            dst.Cpu += p.Cpu; dst.Cs += p.Cs; dst.Wake += p.Wake;
        }
        if (unattr.Cpu + unattr.Cs + unattr.Wake > 0) merged[UnattributedPid] = unattr;

        return merged.Where(kv => kv.Key != 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new ProcessStat(kv.Key == UnattributedPid ? UnattributedName : _names.GetValueOrDefault(kv.Key, "unknown"),
                kv.Key == UnattributedPid ? 0 : kv.Key, kv.Value.Cpu, kv.Value.Cpu * IntervalMs, kv.Value.Cs, kv.Value.Wake))
            .ToList();
    }
}
