using Wtba.Collector;

namespace Wtba.Collector.Tests;

internal sealed class ConstBattery : IBatteryReader
{
    private readonly Func<int> _pct;
    public ConstBattery(Func<int> pct) => _pct = pct;
    public BatteryReading? Read() => new(5000, 30000, _pct());
}

internal sealed class ThrowOnUseFactory : IEtwSessionFactory
{
    public IEtwSession Create(string n, string p) => throw new InvalidOperationException("service touched");
}

internal sealed class ThrowOnUseCatalog : IEtwSessionCatalog
{
    public IReadOnlyList<string> ActiveNames() => throw new InvalidOperationException("service touched");
    public void Stop(string name) => throw new InvalidOperationException("service touched");
}

internal sealed class Env : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "wtba-exp-" + Guid.NewGuid().ToString("N"));
    public readonly FakeFactory F = new();
    public readonly FakeCatalog C = new();
    public int Pct = 90;
    public Queue<int>? PctQueue;
    private int NextPct() { if (PctQueue is { Count: > 0 }) Pct = PctQueue.Count > 1 ? PctQueue.Dequeue() : PctQueue.Peek(); return Pct; }
    public int? SamplerPct;
    public bool Ac, Saver, Elevated = true, HumanToggles = true, BlockWorkload, CreateEtl;
    public readonly List<TimeSpan> Delays = new();
    public Func<int, EtlAggregate> ExtractHook = _ => new EtlAggregate(Array.Empty<ProcessStat>(), 0);
    public Action? OnDelay;
    private int _extractCalls;

    public PathGuard Guard => new(new[] { Root });
    public string DataRoot => Path.Combine(Root, "data");

    public CaptureServices Svc() => new(F, C, () => new ConstBattery(() => SamplerPct ?? Pct), () => Elevated,
        () => (NextPct(), Ac, Saver), _ => 100.0, p => { if (CreateEtl) File.WriteAllBytes(p, new byte[100]); return ExtractHook(++_extractCalls); }, Delay, DataRoot, () => 1);

    private Task Delay(TimeSpan t, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Delays.Add(t);
        OnDelay?.Invoke();
        if (t == TimeSpan.FromSeconds(2)) { if (HumanToggles) Saver = !Saver; return Task.CompletedTask; } // saver poll
        if (BlockWorkload && t == TimeSpan.FromSeconds(0.01)) return Task.Delay(Timeout.Infinite, ct);
        return Task.CompletedTask;
    }

    public ScenarioPlan Plan(int runs = 2, int warm = 1, int retries = 2, string extra = "") => PlanLoader.Parse($@"
scenario: t_idle
duration_s: 0.01
conditions:
  A: {{ label: off, battery_saver: off }}
  B: {{ label: on, battery_saver: on }}
runs_per_condition: {runs}
warmup_runs: {warm}
seed: 3
cooldown_s: 60
max_retries_per_slot: {retries}
{extra}");

    public void PrimeSaver(ScenarioPlan p) => Saver = p.For(RunOrder.Build(p.RunsPerCondition, p.WarmupRuns, p.Seed)[0].Condition).BatterySaver;

    public ExperimentRunner Runner(TextWriter? o = null, TextWriter? e = null, string input = "yes\n\n\n\n\n\n\n\n\n\n") =>
        new(Svc(), Guard, DataRoot, o ?? new StringWriter(), e ?? new StringWriter(), new StringReader(input));

    public string ExpDir() => Directory.GetDirectories(Path.Combine(DataRoot, "experiments")).Single();
    public Manifest Manifest() => ManifestStore.Load(ExpDir());
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

public class PlanLoaderTests
{
    private static string ScenariosDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "scenarios"))) d = d.Parent;
        return Path.Combine(d!.FullName, "scenarios");
    }

    [Theory]
    [InlineData("idle_10m.yaml", 600, false)]
    [InlineData("edge_video_5m.yaml", 300, true)]
    public void ShippedScenariosParse(string file, double dur, bool manual)
    {
        var p = PlanLoader.Load(Path.Combine(ScenariosDir(), file));
        Assert.Equal(dur, p.DurationS); Assert.Equal(manual, p.ManualWorkload);
        Assert.Equal(10, p.RunsPerCondition); Assert.Equal(1, p.WarmupRuns);
        Assert.False(p.A.BatterySaver); Assert.True(p.B.BatterySaver);
    }

    [Fact]
    public void DefaultsApply()
    {
        var p = PlanLoader.Parse("scenario: s\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n");
        Assert.Equal((10, 1, 60.0, 80, 25, 2), (p.RunsPerCondition, p.WarmupRuns, p.CooldownS, p.MinStartPct, p.AbortPct, p.MaxRetriesPerSlot));
    }

    private const string Good = "scenario: s\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n";

    [Theory]
    [InlineData("")]
    [InlineData("duration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n")]
    [InlineData("scenario: s\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n")]
    [InlineData("scenario: s\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n")]
    [InlineData("scenario: s\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: maybe}\n  B: {label: b, battery_saver: on}\n")]
    [InlineData("scenario: s\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: off}\n")]
    [InlineData("scenario: s\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: a, battery_saver: on}\n")]
    [InlineData("scenario: ../evil\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n")]
    [InlineData("scenario: s\nduration_s: abc\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n")]
    [InlineData("scenario: s\nduration_s: -5\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n")]
    [InlineData("scenario: s\nbogus_key: 1\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n")]
    [InlineData("scenario: [unclosed\n")]
    public void BadPlansAreRejected(string yaml) => Assert.Throws<PlanException>(() => PlanLoader.Parse(yaml));

    [Theory]
    [InlineData("abort_pct: 10")]
    [InlineData("abort_pct: 95")]
    [InlineData("min_start_pct: 20")]
    [InlineData("min_start_pct: 101")]
    [InlineData("runs_per_condition: 0")]
    [InlineData("warmup_runs: 9")]
    [InlineData("cooldown_s: -1")]
    [InlineData("max_retries_per_slot: 50")]
    public void OutOfRangeValuesAreRejected(string extra) => Assert.Throws<PlanException>(() => PlanLoader.Parse(Good + extra + "\n"));

    [Fact]
    public void ErrorMessageNamesTheProblem()
    {
        var ex = Assert.Throws<PlanException>(() => PlanLoader.Parse(Good.Replace("off", "maybe")));
        Assert.Contains("battery_saver", ex.Message);
    }
}

public class RunOrderTests
{
    [Fact]
    public void BalancedAndWarmupFirst()
    {
        var o = RunOrder.Build(10, 1, 7);
        Assert.Equal(21, o.Count);
        Assert.True(o[0].Warmup); Assert.All(o.Skip(1), r => Assert.False(r.Warmup));
        Assert.Equal(10, o.Count(r => !r.Warmup && r.Condition == "A"));
        Assert.Equal(10, o.Count(r => !r.Warmup && r.Condition == "B"));
        Assert.Equal(Enumerable.Range(0, 21), o.Select(r => r.Index));
    }

    [Fact]
    public void NeverMoreThanTwoInARowForManySeeds()
    {
        for (int seed = 0; seed < 300; seed++)
            foreach (var warm in new[] { 0, 1, 2, 3 })
            {
                var o = RunOrder.Build(10, warm, seed);
                for (int i = 2; i < o.Count; i++)
                    Assert.False(o[i].Condition == o[i - 1].Condition && o[i].Condition == o[i - 2].Condition, $"seed {seed} warm {warm} idx {i}");
            }
    }

    [Fact]
    public void DeterministicPerSeedAndVariesAcrossSeeds()
    {
        string S(int seed) => string.Concat(RunOrder.Build(10, 1, seed).Select(r => r.Condition));
        Assert.Equal(S(42), S(42));
        Assert.Contains(Enumerable.Range(0, 20), s => S(s) != S(42));
    }

    [Fact]
    public void FirstPositionIsRandomizedAcrossSeeds()
    {
        var firsts = Enumerable.Range(0, 50).Select(s => RunOrder.Build(5, 0, s)[0].Condition).Distinct().ToList();
        Assert.Equal(2, firsts.Count);
    }
}

public class ExperimentRunnerTests
{
    [Fact]
    public void DryRunPrintsOrderAndTouchesNothing()
    {
        using var env = new Env();
        Directory.CreateDirectory(env.Root);
        var yaml = Path.Combine(env.Root, "p.yaml");
        File.WriteAllText(yaml, "scenario: s\nduration_s: 600\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\nseed: 5\n");
        var bomb = new CaptureServices(new ThrowOnUseFactory(), new ThrowOnUseCatalog(),
            () => throw new InvalidOperationException("service touched"), () => throw new InvalidOperationException("service touched"),
            () => throw new InvalidOperationException("service touched"), _ => throw new InvalidOperationException("service touched"));
        var o = new StringWriter();
        var rc = Cli.Run(new[] { "experiment", "--plan", yaml, "--dry-run" }, o, new StringWriter(), env.Guard, bomb);
        Assert.Equal(0, rc);
        Assert.Contains("21 runs", o.ToString());
        Assert.Contains("[warm-up]", o.ToString());
        Assert.Contains("estimated duration", o.ToString());
        Assert.False(Directory.Exists(env.DataRoot));
    }

    [Fact]
    public void BadPlanFileExitsWithClearError()
    {
        using var env = new Env();
        Directory.CreateDirectory(env.Root);
        var yaml = Path.Combine(env.Root, "bad.yaml");
        File.WriteAllText(yaml, "scenario: s\n");
        var e = new StringWriter();
        Assert.Equal(1, Cli.Run(new[] { "experiment", "--plan", yaml, "--dry-run" }, new StringWriter(), e, env.Guard));
        Assert.Contains("plan error", e.ToString());
    }

    [Fact]
    public void HappyPathRunsAllSlotsAndAsksHumanToToggleSaver()
    {
        using var env = new Env();
        var plan = env.Plan();
        env.PrimeSaver(plan);
        var o = new StringWriter();
        string? bad = null;
        env.OnDelay = () =>
        {   // manifest must always be a complete, parseable document (atomic replace)
            var dir = Path.Combine(env.DataRoot, "experiments");
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir))
            {
                try { ManifestStore.Load(d); } catch (Exception ex) { bad = ex.Message; }
            }
        };
        var rc = env.Runner(o).Execute(plan, "sha", null, false, default);
        Assert.Equal(0, rc);
        Assert.Null(bad);
        var m = env.Manifest();
        Assert.Equal("completed", m.State);
        Assert.Equal(5, m.Slots.Count);
        Assert.All(m.Slots, s => { Assert.Equal("done", s.Status); Assert.True(File.Exists(s.BundlePath)); Assert.False(string.IsNullOrEmpty(s.RunId)); });
        Assert.True(m.Slots[0].Warmup);
        Assert.Equal(5, env.F.Created);
        Assert.Empty(Directory.GetFiles(env.ExpDir(), "*.tmp"));
        Assert.Contains("ACTION NEEDED", o.ToString());
        Assert.Equal(plan.Seed, m.Seed); Assert.False(string.IsNullOrEmpty(m.CollectorVersion));
        Assert.Contains(env.Delays, d => d == TimeSpan.FromSeconds(60)); // cooldown between runs
    }

    [Fact]
    public void NotElevatedOrNotConfirmedStartsNothing()
    {
        using var env = new Env { Elevated = false };
        var plan = env.Plan();
        Assert.Equal(3, env.Runner().Execute(plan, "sha", null, false, default));
        env.Elevated = true;
        Assert.Equal(3, env.Runner(input: "no\n").Execute(plan, "sha", null, false, default));
        Assert.Equal(0, env.F.Created);
        Assert.False(Directory.Exists(Path.Combine(env.DataRoot, "experiments")) && Directory.GetDirectories(Path.Combine(env.DataRoot, "experiments")).Length > 0);
    }

    [Fact]
    public void RejectedRunIsRetriedAndThenAccepted()
    {
        using var env = new Env();
        var plan = env.Plan(runs: 1, warm: 0);
        env.PrimeSaver(plan);
        env.ExtractHook = n => new EtlAggregate(Array.Empty<ProcessStat>(), n == 1 ? 7 : 0);
        Assert.Equal(0, env.Runner().Execute(plan, "sha", null, false, default));
        var s = env.Manifest().Slots[0];
        Assert.Equal("done", s.Status);
        Assert.Equal(2, s.Attempts.Count);
        Assert.Equal("rejected", s.Attempts[0].Status);
        Assert.Contains("dropped_events=7", s.Attempts[0].Reason);
        Assert.Equal(3, env.F.Created);
    }

    [Fact]
    public void RetriesExhaustedMarksSlotRejectedAndContinues()
    {
        using var env = new Env();
        var plan = env.Plan(runs: 1, warm: 0, retries: 1);
        env.PrimeSaver(plan);
        env.ExtractHook = n => new EtlAggregate(Array.Empty<ProcessStat>(), n <= 2 ? 1 : 0);
        Assert.Equal(0, env.Runner().Execute(plan, "sha", null, false, default));
        var m = env.Manifest();
        Assert.Equal("rejected", m.Slots[0].Status);
        Assert.Equal(2, m.Slots[0].Attempts.Count);
        Assert.Equal("done", m.Slots[1].Status);
        Assert.Equal("completed", m.State);
    }

    [Fact]
    public void PausesOnLowChargeAndResumeSkipsDoneSlots()
    {
        using var env = new Env();
        var plan = env.Plan(runs: 1, warm: 0);
        env.PrimeSaver(plan);
        env.ExtractHook = n => { env.Pct = 79; return new EtlAggregate(Array.Empty<ProcessStat>(), 0); }; // charge sags after run 1
        var e = new StringWriter();
        Assert.Equal(5, env.Runner(e: e).Execute(plan, "sha", null, false, default));
        var m = env.Manifest();
        Assert.Equal("paused", m.State);
        Assert.Contains("recharge", m.PauseReason);
        Assert.Equal("done", m.Slots[0].Status); Assert.Equal("planned", m.Slots[1].Status);
        Assert.Equal(1, env.F.Created);
        var firstRunId = m.Slots[0].RunId;

        env.Pct = 90; env.ExtractHook = _ => new EtlAggregate(Array.Empty<ProcessStat>(), 0);
        Assert.Equal(0, env.Runner().Execute(plan, "sha", m.ExperimentId, false, default));
        var m2 = env.Manifest();
        Assert.Equal("completed", m2.State);
        Assert.Equal(firstRunId, m2.Slots[0].RunId);
        Assert.Equal(2, env.F.Created); // slot 1 was never re-run
    }

    [Fact]
    public void PausesOnAcPower()
    {
        using var env = new Env { Ac = true };
        var plan = env.Plan(); env.PrimeSaver(plan);
        Assert.Equal(5, env.Runner().Execute(plan, "sha", null, false, default));
        Assert.Contains("unplug", env.Manifest().PauseReason);
        Assert.Equal(0, env.F.Created);
    }

    [Fact]
    public void PausesWhenBatterySaverNeverMatches()
    {
        using var env = new Env { HumanToggles = false };
        var plan = env.Plan(); env.PrimeSaver(plan); env.Saver = !env.Saver;
        var e = new StringWriter();
        Assert.Equal(5, env.Runner(e: e).Execute(plan, "sha", null, false, default));
        Assert.Equal("paused", env.Manifest().State);
        Assert.Equal(150, env.Delays.Count(d => d == TimeSpan.FromSeconds(2))); // 5 min at 2 s
        Assert.Equal(0, env.F.Created);
        Assert.Contains("PAUSED", e.ToString());
    }

    [Fact]
    public void CtrlCDuringRunStopsSessionAndMarksInterrupted()
    {
        using var env = new Env { BlockWorkload = true };
        var plan = env.Plan(); env.PrimeSaver(plan);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var rc = env.Runner().Execute(plan, "sha", null, false, cts.Token);
        Assert.Equal(130, rc);
        var m = env.Manifest();
        Assert.Equal("interrupted", m.State);
        Assert.Equal("interrupted", m.Slots[0].Status);
        Assert.Equal(1, env.F.Last!.Stops); Assert.Equal(1, env.F.Last.Disposes);
        Assert.All(m.Slots.Skip(1), s => Assert.Equal("planned", s.Status));
    }

    [Fact]
    public void CtrlCBetweenRunsExitsCleanly()
    {
        using var env = new Env();
        var plan = env.Plan(); env.PrimeSaver(plan);
        using var cts = new CancellationTokenSource();
        env.OnDelay = () => { if (env.Delays.Any(d => d == TimeSpan.FromSeconds(60))) cts.Cancel(); };
        Assert.Equal(130, env.Runner().Execute(plan, "sha", null, false, cts.Token));
        Assert.Equal("interrupted", env.Manifest().State);
        Assert.Equal("done", env.Manifest().Slots[0].Status);
    }

    [Fact]
    public void BatteryAbortStopsExperimentAndIsResumable()
    {
        using var env = new Env { BlockWorkload = true, SamplerPct = 20 };
        var plan = env.Plan(); env.PrimeSaver(plan);
        var rc = env.Runner().Execute(plan, "sha", null, false, default);
        Assert.Equal(4, rc);
        var m = env.Manifest();
        Assert.Equal("aborted", m.State);
        Assert.Equal("interrupted", m.Slots[0].Status);
        Assert.Equal(1, env.F.Created);
        Assert.Equal(1, env.F.Last!.Stops);
    }

    [Fact]
    public void ResumeRefusesChangedPlan()
    {
        using var env = new Env();
        var plan = env.Plan(); env.PrimeSaver(plan); env.Ac = true;
        env.Runner().Execute(plan, "sha1", null, false, default);
        var id = env.Manifest().ExperimentId;
        Assert.Throws<PlanException>(() => env.Runner().Execute(plan, "sha2", id, false, default));
    }

    [Fact]
    public void ResumeRejectsMaliciousId()
    {
        using var env = new Env();
        Assert.Throws<PlanException>(() => env.Runner().Execute(env.Plan(), "sha", "..\\..\\x", false, default));
    }

    [Fact]
    public void ManualWorkloadPromptsPerRunUnlessSuppressed()
    {
        using var env = new Env();
        var plan = env.Plan(runs: 1, warm: 0, extra: "manual_workload: true"); env.PrimeSaver(plan);
        var o = new StringWriter();
        Assert.Equal(0, env.Runner(o).Execute(plan, "sha", null, false, default));
        Assert.Equal(2, o.ToString().Split("start the workload now").Length - 1);

        using var env2 = new Env();
        var plan2 = env2.Plan(runs: 1, warm: 0, extra: "manual_workload: true"); env2.PrimeSaver(plan2);
        var o2 = new StringWriter();
        Assert.Equal(0, env2.Runner(o2).Execute(plan2, "sha", null, true, default));
        Assert.DoesNotContain("start the workload now", o2.ToString());
    }

    [Fact]
    public void IdleScenarioNeverPromptsForWorkload()
    {
        using var env = new Env();
        var plan = env.Plan(runs: 1, warm: 0); env.PrimeSaver(plan);
        var o = new StringWriter();
        env.Runner(o).Execute(plan, "sha", null, false, default);
        Assert.DoesNotContain("start the workload now", o.ToString());
    }
}

public class RunGatesTests
{
    private static RunOutcome Oc(int gaps = 0, int dropped = 0, bool gates = true, int start = 90, int end = 89, bool ac = false, bool saver = false)
    {
        var b = new Bundle(1, "r", "s", "A", "l", "t", 1, new MachineInfo("h", "o", "c", 1),
            new PowerInfo(ac, saver, "g", start, end), new QualityInfo(dropped, gaps, gates), Array.Empty<ProcessStat>(), new[] { new BatterySample(0, 1, 1) });
        return new RunOutcome(0, null, "r", new RecordResult("WTBA-x", "p", StopReason.Completed, null), b, "p", start, end);
    }

    [Fact] public void CleanRunPasses() => Assert.Empty(RunGates.Evaluate(Oc(), false, 80, false, false));
    [Fact] public void GapOver15Fails() => Assert.NotEmpty(RunGates.Evaluate(Oc(gaps: 16), false, 80, false, false));
    [Fact] public void Gap15Passes() => Assert.Empty(RunGates.Evaluate(Oc(gaps: 15), false, 80, false, false));
    [Fact] public void DroppedFails() => Assert.NotEmpty(RunGates.Evaluate(Oc(dropped: 1), false, 80, false, false));
    [Fact] public void QualityFlagFails() => Assert.NotEmpty(RunGates.Evaluate(Oc(gates: false), false, 80, false, false));
    [Fact] public void AcAfterRunFails() => Assert.NotEmpty(RunGates.Evaluate(Oc(), false, 80, true, false));
    [Fact] public void SaverMismatchFails() => Assert.NotEmpty(RunGates.Evaluate(Oc(), false, 80, false, true));
    [Fact] public void LowStartFails() => Assert.NotEmpty(RunGates.Evaluate(Oc(start: 79, end: 78), false, 80, false, false));
    [Fact] public void ChargeRiseFails() => Assert.NotEmpty(RunGates.Evaluate(Oc(start: 85, end: 90), false, 80, false, false));
    [Fact] public void HugeDropFails() => Assert.NotEmpty(RunGates.Evaluate(Oc(start: 95, end: 60), false, 80, false, false));
}

public class SaverWaitMessageTests
{
    private static (int rc, string o, string e, Env env) Run(Action<Env> setup)
    {
        var env = new Env { HumanToggles = false };
        var plan = env.Plan(); env.PrimeSaver(plan); env.Saver = !env.Saver;
        setup(env);
        var o = new StringWriter(); var e = new StringWriter();
        var r = new ExperimentRunner(env.Svc(), env.Guard, env.DataRoot, o, e, new StringReader("yes\n")) { PlanPath = "scenarios/x.yaml" };
        return (r.Execute(plan, "sha", null, false, default), o.ToString(), e.ToString(), env);
    }

    [Fact]
    public void AcCaseTellsUserToUnplugAndTimeoutGivesResumeCommand()
    {
        var (rc, o, e, env) = Run(x => { x.Ac = true; x.Pct = 82; });
        using (env)
        {
            Assert.Equal(5, rc);
            Assert.Contains("unplug the charger", o);
            Assert.Contains("power source AC", o);
            Assert.Contains("battery 82%", o);
            Assert.Contains("last reading: waiting: need Battery Saver", e);
            Assert.Contains($"experiment --plan scenarios/x.yaml --resume {env.Manifest().ExperimentId}", o);
            // first poll immediately, then every 5 s over 300 s => 1 + 60 lines (state never changes)
            Assert.InRange(o.Split("waiting: need").Length - 1, 45, 61); // ~every 5 s at 2 s poll granularity
        }
    }

    [Fact]
    public void DcSaverOffCase()
    {
        var (_, o, _, env) = Run(x => { x.Ac = false; x.Pct = 90; });
        using (env)
        {
            var wanted = o.Contains("need Battery Saver ON") ? "ON" : "OFF";
            var current = wanted == "ON" ? "OFF" : "ON";
            Assert.Contains($"need Battery Saver {wanted}; currently {current}, power source battery (DC), battery 90%", o);
            Assert.DoesNotContain("unplug the charger", o);
        }
    }

    [Fact]
    public void ChangeOfStatePrintsImmediately()
    {
        var count = 0;
        var (_, o, _, env) = Run(x => x.OnDelay = () => { if (++count == 2) x.Pct = 85; });
        using (env)
        {
            var lines = o.Split('\n').Where(l => l.StartsWith("waiting:")).ToList();
            Assert.Contains("battery 90%", lines[0]);
            Assert.Contains("battery 85%", lines[1]); // printed right after the change, before the 5 s mark
            Assert.True(lines.Count >= 2);
        }
    }

    [Fact]
    public void StatusShowsRawFlag()
    {
        var o = new StringWriter();
        using var env = new Env();
        Cli.Run(new[] { "status" }, o, new StringWriter(), env.Guard, env.Svc());
        Assert.Contains("battery saver: off (SystemStatusFlag=1)", o.ToString());
    }
}
