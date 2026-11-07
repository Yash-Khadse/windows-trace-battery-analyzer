using System.Globalization;

namespace Wtba.Collector;

public sealed record RunRequest(string RunId, string Scenario, string Condition, string Label, bool DeclaredSaver,
    double DurationS, int MinStartPct, int AbortPct, string DataRoot, bool SamplerOn = true);

public sealed record RunOutcome(int Code, string? Error, string RunId, RecordResult? Rec, Bundle? Bundle, string? BundlePath,
    int StartPct, int EndPct);

public static partial class Cli
{
    private static int RunVerb(Dictionary<string, string> opt, TextWriter o, TextWriter e, PathGuard guard,
        CaptureServices svc, TextReader input, CancellationToken ct)
    {
        if (!svc.IsElevated()) { e.WriteLine(NotElevatedMessage); return 3; }
        var scenario = ScenarioName(opt.GetValueOrDefault("scenario", ""));
        if (scenario.Length == 0) { e.WriteLine("--scenario required"); return 1; }
        var cond = opt.GetValueOrDefault("condition", "").ToUpperInvariant();
        if (cond != "A" && cond != "B") { e.WriteLine("--condition must be A or B"); return 1; }
        var bs = opt.GetValueOrDefault("battery-saver", "");
        if (bs != "on" && bs != "off") { e.WriteLine("--battery-saver on|off is required (declare the state you set by hand)"); return 1; }
        bool declared = bs == "on";
        var label = opt.GetValueOrDefault("label", cond == "B" ? "battery-saver-on" : "battery-saver-off");
        var durS = opt.TryGetValue("duration-s", out var ds) && double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
            ? dv : Simulator.ScenarioDuration(scenario);

        if (!(durS >= 1 && durS <= MaxRunDurationS)) { e.WriteLine($"--duration-s must be between 1 and {MaxRunDurationS:0}"); return 1; }
        var samplerOn = !string.Equals(opt.GetValueOrDefault("battery-sampler", "on"), "off", StringComparison.OrdinalIgnoreCase);
        int minStart = SafetyGates.DefaultStartPct;
        if (!samplerOn)
        {   // smoke-test limits when the WMI sampler is off: short runs only, charge must still be healthy
            if (durS > SamplerOffMaxDurationS) { e.WriteLine($"--battery-sampler off limits runs to {SamplerOffMaxDurationS:0} s"); return 1; }
            minStart = SamplerOffMinStartPct;
        }
        var stale = StaleSessionProblem(svc);
        if (stale is not null) { e.WriteLine(stale); return 3; }

        var (pct, ac, saver) = svc.ReadPower();
        var snap = new SystemSnapshot(true, svc.FreeDiskGb(ProjectRoot()), pct, ac, saver, declared);
        var gate = SafetyGates.CheckStart(snap, true, minStart);
        if (!gate.Passed) { foreach (var r in gate.Reasons) e.WriteLine("gate failed: " + r); return 3; }

        o.WriteLine($"Battery Saver declared {bs}, verified {(saver ? "on" : "off")}; battery {pct}%, scenario {scenario} ({durS:0}s). Type 'yes' to start (Ctrl+C aborts):");
        if (!string.Equals(input.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) { e.WriteLine("not confirmed; nothing started"); return 3; }

        var req = new RunRequest(Guid.NewGuid().ToString(), scenario, cond, label, declared, durS,
            minStart, SafetyGates.AbortPct, svc.DataRootOrDefault(ProjectRoot()), samplerOn);
        var oc = RunOnce(req, o, guard, svc, ct);
        if (oc.Error is not null && oc.Rec is null) e.WriteLine(oc.Error);
        if (oc.Rec is not null)
        {
            long sz = 0; try { sz = new FileInfo(oc.Rec.EtlPath).Length; } catch (IOException) { }
            o.WriteLine($"ETL: {oc.Rec.EtlPath} ({sz / 1048576.0:0.0} MB) kept; reclaim later with: etl-clean --apply");
        }
        return oc.Code;
    }

    public const double MaxRunDurationS = 3600;
    public const double SamplerOffMaxDurationS = 120;
    public const int SamplerOffMinStartPct = 50;

    /// <summary>S3: null if no WTBA-* session is active; else the refusal message (also when sessions cannot be listed: fail closed).</summary>
    public static string? StaleSessionProblem(CaptureServices svc)
    {
        try
        {
            var stale = SessionNames.FilterEligible(svc.Catalog.ActiveNames());
            return stale.Count == 0 ? null
                : $"stale WTBA-* ETW session(s) already running: {string.Join(", ", stale)}. Stop them first with: wtba cleanup  (elevated shell)";
        }
        catch (Exception ex) { return $"cannot list ETW sessions ({ex.GetType().Name}); refusing to start. Check with: wtba status"; }
    }

    /// <summary>
    /// One already-confirmed capture: start gates, record, extract, write bundle. No prompts.
    /// Code: 0 ok, 1 bad path, 3 gate failure, 4 stopped early (abort/cancel/watchdog/cap).
    /// </summary>
    public static RunOutcome RunOnce(RunRequest r, TextWriter o, PathGuard guard, CaptureServices svc, CancellationToken ct)
    {
        RunOutcome Fail(int code, string msg) => new(code, msg, r.RunId, null, null, null, 0, 0);
        if (!(r.DurationS > 0 && r.DurationS <= MaxRunDurationS)) return Fail(1, $"duration must be 0..{MaxRunDurationS:0} s");
        var staleMsg = StaleSessionProblem(svc);
        if (staleMsg is not null) return Fail(3, staleMsg);

        var (pct, ac, saver) = svc.ReadPower();
        var snap = new SystemSnapshot(true, svc.FreeDiskGb(ProjectRoot()), pct, ac, saver, r.DeclaredSaver);
        var gate = SafetyGates.CheckStart(snap, true, r.MinStartPct);
        if (!gate.Passed) return Fail(3, "gate failed: " + string.Join("; ", gate.Reasons));

        string outDir;
        try { outDir = guard.Resolve(Path.Combine(r.DataRoot, "runs", r.RunId)); }
        catch (UnauthorizedAccessException ex) { return Fail(1, ex.Message); }

        var started = DateTime.UtcNow;
        var recorder = new EtwRecorder(svc.Factory, guard, () => svc.FreeDiskGb(ProjectRoot()), svc.Shutdown);
        var sampler = new BatterySampler(svc.Battery(), msg => recorder.RequestStop(StopReason.Aborted, msg), abortPct: r.AbortPct);
        // S5 independent of WMI: GetSystemPowerStatus polled every 5 s (3 unknown reads in a row also abort).
        var watchdog = new PowerWatchdog(svc.ReadPower, msg => recorder.RequestStop(StopReason.Aborted, msg), r.AbortPct);
        using var samplerCts = new CancellationTokenSource();
        var samplerTask = Task.WhenAll(r.SamplerOn ? sampler.Start(samplerCts.Token) : Task.CompletedTask, watchdog.Start(samplerCts.Token));
        RecordResult rec;
        try
        {
            // The Edge workload launcher is not implemented: for manual-workload scenarios the human starts it by hand.
            rec = recorder.RecordAsync(new RecorderOptions(r.RunId, TimeSpan.FromSeconds(r.DurationS), outDir),
                t => svc.DelayFn(TimeSpan.FromSeconds(r.DurationS), t), ct).GetAwaiter().GetResult();
        }
        finally
        {
            samplerCts.Cancel();
            try { samplerTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        o.WriteLine($"session {rec.SessionName} stopped: {rec.Reason}{(rec.Detail is null ? "" : " (" + rec.Detail + ")")}");
        var good = rec.Reason == StopReason.Completed;
        var endPct = sampler.LastPct == 255 ? pct : sampler.LastPct;
        var meta = new RunMetadata(r.RunId, r.Scenario, r.Condition, r.Label, started, (DateTime.UtcNow - started).TotalSeconds,
            saver, pct, endPct, good);
        var bundle = EtlExtractor.BuildBundle(meta, svc.ExtractFn(rec.EtlPath), sampler.Samples, sampler.GapSeconds);
        outDir = guard.Resolve(outDir); // re-verify (no reparse point appeared) immediately before the elevated write
        var path = BundleWriter.Write(bundle, outDir);
        o.WriteLine($"wrote {path}");
        return new RunOutcome(good ? 0 : 4, good ? null : rec.Reason + (rec.Detail is null ? "" : ": " + rec.Detail), r.RunId, rec, bundle, path, pct, endPct);
    }

    private static int ExperimentVerb(Dictionary<string, string> opt, TextWriter o, TextWriter e, PathGuard guard,
        CaptureServices? svc, TextReader input, CancellationToken ct)
    {
        if (!opt.TryGetValue("plan", out var planArg) || planArg == "true") { e.WriteLine("usage: wtba experiment --plan <file.yaml> [--resume <id>] [--dry-run] [--no-prompt-workload]"); return 1; }
        string planPath, yaml;
        ScenarioPlan plan;
        try
        {
            planPath = guard.Resolve(Path.IsPathRooted(planArg) ? planArg : Path.Combine(ProjectRoot(), planArg));
            if (!File.Exists(planPath)) { e.WriteLine($"plan not found: {planPath}"); return 1; }
            yaml = File.ReadAllText(planPath);
            plan = PlanLoader.Parse(yaml);
        }
        catch (UnauthorizedAccessException ex) { e.WriteLine(ex.Message); return 1; }
        catch (PlanException ex) { e.WriteLine("plan error: " + ex.Message); return 1; }

        if (opt.ContainsKey("dry-run"))
        {
            ExperimentRunner.PrintPlan(plan, RunOrder.Build(plan.RunsPerCondition, plan.WarmupRuns, plan.Seed), o);
            return 0;
        }
        var s = svc ?? CaptureServices.Real();
        var runner = new ExperimentRunner(s, guard, s.DataRootOrDefault(ProjectRoot()), o, e, input) { PlanPath = planArg };
        var resume = opt.TryGetValue("resume", out var rid) && rid != "true" ? rid : null;
        try { return runner.Execute(plan, ExperimentRunner.Sha(yaml), resume, opt.ContainsKey("no-prompt-workload"), ct); }
        catch (PlanException ex) { e.WriteLine("experiment error: " + ex.Message); return 1; }
    }
}

/// <summary>Injectable environment so verbs can be tested with fakes.</summary>
public sealed record CaptureServices(
    IEtwSessionFactory Factory,
    IEtwSessionCatalog Catalog,
    Func<IBatteryReader> Battery,
    Func<bool> IsElevated,
    Func<(int Pct, bool OnAc, bool BatterySaver)> ReadPower,
    Func<string, double> FreeDiskGb,
    Func<string, EtlAggregate>? Extract = null,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    string? DataRoot = null,
    Func<int>? RawFlag = null,
    IShutdownRegistry? Shutdown = null)
{
    public Func<string, EtlAggregate> ExtractFn => Extract ?? EtlExtractor.Read;
    public Func<TimeSpan, CancellationToken, Task> DelayFn => Delay ?? ((t, ct) => Task.Delay(t, ct));
    public Func<int> RawFlagFn => RawFlag ?? SystemStatus.ReadRawFlag;
    public string DataRootOrDefault(string projectRoot) => DataRoot ?? Path.Combine(projectRoot, "data");

    public static CaptureServices Real() => new(new TraceEventSessionFactory(), new TraceEventSessionCatalog(),
        () => new WmiBatteryReader(), Elevation.IsElevated, SystemStatus.ReadPower, SystemStatus.FreeDiskGb, Shutdown: ShutdownGuard.Instance);
}
