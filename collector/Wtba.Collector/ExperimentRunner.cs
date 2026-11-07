using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wtba.Collector;

public static class RunGates
{
    public const double MaxSamplerGapS = 15.0;
    public const int MaxEndDropPct = 10;

    /// <summary>Per-run quality gates. Empty list means the run is accepted.</summary>
    public static IReadOnlyList<string> Evaluate(RunOutcome oc, bool declaredSaver, int minStartPct, bool postOnAc, bool postSaver, bool samplerOn = true)
    {
        var r = new List<string>();
        if (oc.Rec is null || oc.Bundle is null) { r.Add(oc.Error ?? "no bundle"); return r; }
        if (oc.Rec.Reason != StopReason.Completed) r.Add($"session stopped early: {oc.Rec.Reason}");
        var b = oc.Bundle;
        if (!b.Quality.GatesPassed) r.Add("bundle quality.gates_passed is false");
        if (b.Quality.DroppedEvents != 0) r.Add($"dropped_events={b.Quality.DroppedEvents}");
        if (b.Quality.SamplerGapsS > MaxSamplerGapS) r.Add($"sampler gap {b.Quality.SamplerGapsS:0.#}s > {MaxSamplerGapS:0}s");
        if (samplerOn)
        {
            if (b.BatterySamples.Count == 0) r.Add("no battery samples recorded while the sampler was on");
            else if (b.DurationS - b.BatterySamples[^1].TS > MaxSamplerGapS)
                r.Add($"battery sampler silent for the last {b.DurationS - b.BatterySamples[^1].TS:0.#}s of the run (> {MaxSamplerGapS:0}s)");
        }
        if (postOnAc) r.Add("on AC power after run");
        if (b.Power.OnAc) r.Add("bundle reports AC power");
        if (postSaver != declaredSaver || b.Power.BatterySaver != declaredSaver) r.Add("battery saver differs from declared condition");
        if (b.Power.StartPct < minStartPct) r.Add($"start charge {b.Power.StartPct}% < {minStartPct}%");
        if (b.Power.StartPct > 100 || b.Power.EndPct > 100 || b.Power.EndPct < 0) r.Add("charge reading out of range");
        else if (b.Power.EndPct > b.Power.StartPct) r.Add($"end charge {b.Power.EndPct}% above start {b.Power.StartPct}% (charging?)");
        else if (b.Power.StartPct - b.Power.EndPct > MaxEndDropPct) r.Add($"charge dropped {b.Power.StartPct - b.Power.EndPct} points in one run");
        return r;
    }
}

public sealed class ExperimentRunner
{
    private readonly CaptureServices _svc;
    private readonly PathGuard _guard;
    private readonly string _dataRoot;
    private readonly TextWriter _o, _e;
    private readonly TextReader _in;
    public IFileOps Fs { get; init; } = new RealFileOps();
    public TimeSpan SaverPoll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SaverTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public ExperimentRunner(CaptureServices svc, PathGuard guard, string dataRoot, TextWriter o, TextWriter e, TextReader input)
    {
        _svc = svc; _guard = guard; _dataRoot = dataRoot; _o = o; _e = e; _in = input;
    }

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string Version => typeof(ExperimentRunner).Assembly.GetName().Version?.ToString() ?? "0";

    public static double EstimateHours(ScenarioPlan p, int runs) => runs * (p.DurationS + p.CooldownS + p.SettleS) / 3600.0;

    /// <summary>Pure printing; touches no services.</summary>
    public static void PrintPlan(ScenarioPlan p, IReadOnlyList<PlannedRun> order, TextWriter o)
    {
        o.WriteLine($"scenario {p.Scenario}: {order.Count} runs ({p.WarmupRuns} warm-up), seed {p.Seed}");
        o.WriteLine($"estimated duration: {EstimateHours(p, order.Count):0.0} h; needs battery >= {p.MinStartPct}% on DC power");
        foreach (var r in order)
            o.WriteLine($"  {r.Index + 1,3}. {r.Condition} ({p.For(r.Condition).Label}, saver {(p.For(r.Condition).BatterySaver ? "on" : "off")}){(r.Warmup ? " [warm-up]" : "")}");
    }

    private string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private void Delay(TimeSpan t, CancellationToken ct) => _svc.DelayFn(t, ct).GetAwaiter().GetResult();

    /// <summary>Exit codes: 0 complete, 1 bad input, 3 refused, 4 battery abort, 5 paused (resumable), 130 interrupted.</summary>
    public int Execute(ScenarioPlan plan, string planSha, string? resumeId, bool noPromptWorkload, CancellationToken ct)
    {
        if (!_svc.IsElevated()) { _e.WriteLine(Cli.NotElevatedMessage); return 3; }

        var staleMsg = Cli.StaleSessionProblem(_svc);
        if (staleMsg is not null) { _e.WriteLine(staleMsg); return 3; }

        Manifest m;
        string dir;
        if (resumeId is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(resumeId, "^exp-[A-Za-z0-9-]{1,64}$")) throw new PlanException("bad experiment id");
            dir = _guard.Resolve(Path.Combine(_dataRoot, "experiments", resumeId));
            m = ManifestStore.Load(dir);
            if (m.PlanSha256 != planSha) throw new PlanException("plan file changed since the experiment was created; refusing to resume");
        }
        else
        {
            var id = $"exp-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            dir = _guard.Resolve(Path.Combine(_dataRoot, "experiments", id));
            m = new Manifest
            {
                ExperimentId = id, Scenario = plan.Scenario, Seed = plan.Seed, PlanSha256 = planSha,
                CollectorVersion = Version, CreatedUtc = Now(),
                Slots = RunOrder.Build(plan.RunsPerCondition, plan.WarmupRuns, plan.Seed)
                    .Select(r => new SlotRecord { Index = r.Index, Condition = r.Condition, Warmup = r.Warmup }).ToList(),
            };
        }

        var pending = m.Slots.Where(s => s.Status is "planned" or "running" or "interrupted").ToList();
        if (pending.Count == 0) { _o.WriteLine("nothing left to run"); return 0; }

        _o.WriteLine($"Experiment {m.ExperimentId}: {pending.Count} run(s) to execute, about {EstimateHours(plan, pending.Count):0.0} h.");
        _o.WriteLine($"Requires battery >= {plan.MinStartPct}% on DC power; aborts at {plan.AbortPct}%. You will be asked to toggle Battery Saver by hand between conditions.");
        _o.WriteLine("Type 'yes' to confirm the whole experiment (Ctrl+C stops safely):");
        if (!string.Equals(_in.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) { _e.WriteLine("not confirmed; nothing started"); return 3; }

        m.State = "running"; m.PauseReason = null;
        Save(dir, m);

        SlotRecord? current = null;
        AttemptRecord? attempt = null;
        bool firstRun = true;
        try
        {
            foreach (var slot in pending)
            {
                current = slot;
                var cond = plan.For(slot.Condition);
                while (slot.Attempts.Count(x => x.Status != "interrupted") < plan.MaxRetriesPerSlot + 1)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!firstRun && plan.CooldownS > 0) Delay(TimeSpan.FromSeconds(plan.CooldownS), ct);
                    firstRun = false;

                    if (!WaitForSaver(cond.BatterySaver, ct)) return Pause(dir, m, "timed out waiting for Battery Saver to match; toggle it and resume");
                    var (pct, ac, _) = _svc.ReadPower();
                    if (ac || pct < 0 || pct > 100 || pct < plan.MinStartPct)
                        return Pause(dir, m, ac
                            ? "on AC power; unplug the charger and resume"
                            : $"battery {(pct == 255 ? "unknown" : pct + "%")} < {plan.MinStartPct}%; recharge to >= {plan.MinStartPct}% (>= 80% recommended), unplug, and resume");

                    if (plan.SettleS > 0) Delay(TimeSpan.FromSeconds(plan.SettleS), ct);
                    if (plan.ManualWorkload && !noPromptWorkload)
                    {
                        _o.WriteLine("start the workload now, then press Enter to begin the run");
                        _in.ReadLine();
                    }

                    var runId = Guid.NewGuid().ToString();
                    attempt = new AttemptRecord { RunId = runId, Status = "running", StartedUtc = Now() };
                    slot.Attempts.Add(attempt);
                    slot.Status = "running"; slot.RunId = runId; slot.StartedUtc ??= attempt.StartedUtc;
                    Save(dir, m);

                    var req = new RunRequest(runId, plan.Scenario, slot.Condition, cond.Label, cond.BatterySaver, plan.DurationS,
                        plan.MinStartPct, plan.AbortPct, _dataRoot);
                    RunOutcome oc;
                    try { oc = Cli.RunOnce(req, _o, _guard, _svc, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { oc = new RunOutcome(1, ex.GetType().Name + ": " + ex.Message, runId, null, null, null, 0, 0); }

                    attempt.EndedUtc = Now();
                    attempt.BundlePath = oc.BundlePath;

                    if (ct.IsCancellationRequested || oc.Rec?.Reason == StopReason.Cancelled)
                    {
                        attempt.Status = "interrupted"; attempt.Reason = "Ctrl+C";
                        slot.Status = "interrupted"; slot.Reason = "Ctrl+C"; slot.EndedUtc = Now();
                        m.State = "interrupted"; Save(dir, m);
                        _o.WriteLine($"interrupted; resume with --resume {m.ExperimentId}");
                        return 130;
                    }
                    if (oc.Rec?.Reason == StopReason.Aborted)
                    {
                        attempt.Status = "rejected"; attempt.Reason = "battery abort: " + oc.Rec.Detail;
                        slot.Status = "interrupted"; slot.Reason = attempt.Reason; slot.EndedUtc = Now();
                        m.State = "aborted"; m.PauseReason = attempt.Reason; Save(dir, m);
                        _e.WriteLine($"ABORT: {attempt.Reason}. Recharge, then resume with --resume {m.ExperimentId}");
                        return 4;
                    }

                    var (_, postAc, postSaver) = _svc.ReadPower();
                    var reasons = RunGates.Evaluate(oc, cond.BatterySaver, plan.MinStartPct, postAc, postSaver);
                    if (reasons.Count == 0)
                    {
                        attempt.Status = "done"; slot.Status = "done"; slot.Reason = null;
                        slot.BundlePath = oc.BundlePath; slot.EndedUtc = Now();
                        Save(dir, m);
                        ApplyRetention(dir, m, plan);
                        _o.WriteLine($"slot {slot.Index + 1}/{m.Slots.Count} {slot.Condition}{(slot.Warmup ? " (warm-up)" : "")}: done");
                        break;
                    }
                    attempt.Status = "rejected"; attempt.Reason = string.Join("; ", reasons);
                    slot.Reason = attempt.Reason; slot.EndedUtc = Now();
                    slot.Status = slot.Attempts.Count(x => x.Status != "interrupted") >= plan.MaxRetriesPerSlot + 1 ? "rejected" : "planned";
                    Save(dir, m);
                    _o.WriteLine($"slot {slot.Index + 1}: run rejected ({attempt.Reason}){(slot.Status == "planned" ? "; retrying" : "; retries exhausted")}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (current is not null && current.Status == "running")
            {
                current.Status = "interrupted"; current.Reason = "Ctrl+C"; current.EndedUtc = Now();
                if (attempt is not null && attempt.Status == "running") { attempt.Status = "interrupted"; attempt.Reason = "Ctrl+C"; attempt.EndedUtc = Now(); }
            }
            m.State = "interrupted"; Save(dir, m);
            _o.WriteLine($"interrupted; resume with --resume {m.ExperimentId}");
            return 130;
        }

        ApplyRetention(dir, m, plan);
        m.State = "completed"; Save(dir, m);
        _o.WriteLine($"experiment complete: {m.Slots.Count(s => s.Status == "done")} done, {m.Slots.Count(s => s.Status == "rejected")} rejected");
        return 0;
    }

    /// <summary>Never throws and never fails the experiment; deletion itself is done only by EtlCleaner.</summary>
    private void ApplyRetention(string dir, Manifest m, ScenarioPlan plan)
    {
        try
        {
            m.EtlRetention = plan.Retention.Describe();
            var runsRoot = Path.Combine(_dataRoot, "runs");
            var entries = new List<RunEntry>();
            foreach (var slot in m.Slots)
                foreach (var a in slot.Attempts.Where(a => !string.IsNullOrEmpty(a.RunId)))
                {
                    var runDir = Path.Combine(runsRoot, a.RunId);
                    var etl = Path.Combine(runDir, a.RunId + ".etl");
                    if (!Fs.FileExists(etl)) continue;
                    var chk = BundleValidator.Check(Fs, runDir);
                    DateTime started = DateTime.TryParse(a.StartedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : chk.StartedUtc;
                    entries.Add(new RunEntry(a.RunId, etl, Fs.Length(etl), chk.Valid, started, a.Status != "done"));
                }
            var del = EtlRetention.Decide(entries, plan.Retention, includeRejected: false).Where(d => d.Delete).ToList();
            if (del.Count > 0)
            {
                var report = new EtlCleaner(Fs, runsRoot).Clean(del.Select(d => d.Entry.EtlPath));
                var deletedPaths = new HashSet<string>(report.DeletedPaths, StringComparer.OrdinalIgnoreCase);
                foreach (var d in del.Where(d => deletedPaths.Contains(Path.GetFullPath(d.Entry.EtlPath)))) m.EtlDeletedRunIds.Add(d.Entry.RunId);
                m.EtlFreedBytes += report.FreedBytes;
                foreach (var i in report.Items.Where(i => !i.Deleted)) _e.WriteLine($"etl cleanup: {i.Path}: {i.Reason}");
                _o.WriteLine($"etl cleanup ({plan.Retention.Describe()}): freed {report.FreedBytes / 1048576.0:0.0} MB");
            }
            Save(dir, m);
        }
        catch (Exception ex) { _e.WriteLine("etl cleanup failed (ignored): " + ex.GetType().Name + ": " + ex.Message); }
    }

    private void Save(string dir, Manifest m) { m.UpdatedUtc = Now(); ManifestStore.Save(_guard.Resolve(dir), m); } // re-verify no reparse point before every elevated write

    private int Pause(string dir, Manifest m, string reason)
    {
        m.State = "paused"; m.PauseReason = reason; Save(dir, m);
        _e.WriteLine($"PAUSED: {reason}");
        if (_lastReading.Length > 0 && reason.StartsWith("timed out")) _e.WriteLine("last reading: " + _lastReading);
        _o.WriteLine($"resume with: experiment --plan {PlanPath ?? "<file>"} --resume {m.ExperimentId}");
        return 5;
    }

    public static string DescribeWait(bool wanted, int pct, bool ac, bool saver) =>
        $"waiting: need Battery Saver {(wanted ? "ON" : "OFF")}; currently {(saver ? "ON" : "OFF")}, power source {(ac ? "AC (unplug the charger: Windows keeps Battery Saver off on AC)" : "battery (DC)")}, battery {(pct == 255 ? "unknown" : pct + "%")}";

    private string _lastReading = "";
    public TimeSpan SaverPrintEvery { get; init; } = TimeSpan.FromSeconds(5);
    public string? PlanPath { get; init; }

    /// <summary>Read-only wait: the human toggles Battery Saver; this code never changes any setting.</summary>
    private bool WaitForSaver(bool wanted, CancellationToken ct)
    {
        if (_svc.ReadPower().BatterySaver == wanted) return true;
        _o.WriteLine($"ACTION NEEDED: turn Battery Saver {(wanted ? "ON" : "OFF")} by hand now. Waiting up to {SaverTimeout.TotalMinutes:0} min...");
        var waited = TimeSpan.Zero;
        var lastPrint = TimeSpan.MinValue;
        (int, bool, bool)? lastState = null;
        while (true)
        {
            var st = _svc.ReadPower();
            if (st.BatterySaver == wanted) { _o.WriteLine("Battery Saver state matches."); return true; }
            _lastReading = DescribeWait(wanted, st.Pct, st.OnAc, st.BatterySaver);
            if (lastState is null || lastState != (st.Pct, st.OnAc, st.BatterySaver) || waited - lastPrint >= SaverPrintEvery)
            {
                _o.WriteLine(_lastReading);
                lastPrint = waited; lastState = (st.Pct, st.OnAc, st.BatterySaver);
            }
            if (waited >= SaverTimeout) return false;
            Delay(SaverPoll, ct);
            waited += SaverPoll;
        }
    }
}
