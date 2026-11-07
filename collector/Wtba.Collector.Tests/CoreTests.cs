using System.Text.Json;
using Wtba.Collector;

namespace Wtba.Collector.Tests;

public class PathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wtba-root");
    private readonly PathGuard _g = new(new[] { Root });

    [Fact] public void AllowsRootItself() => Assert.True(_g.IsAllowed(Root));
    [Fact] public void AllowsChild() => Assert.True(_g.IsAllowed(Path.Combine(Root, "data", "runs", "x")));
    [Fact] public void RejectsDotDotTraversal() => Assert.False(_g.IsAllowed(Path.Combine(Root, "data", "..", "..", "evil")));
    [Fact] public void RejectsSiblingWithSamePrefix() => Assert.False(_g.IsAllowed(Root + "-other"));
    [Fact] public void RejectsParent() => Assert.False(_g.IsAllowed(Path.GetDirectoryName(Root)!));
    [Fact] public void RejectsEmpty() => Assert.False(_g.IsAllowed(""));

    [Fact]
    public void RejectsDifferentDrive()
    {
        var drive = char.ToUpperInvariant(Path.GetPathRoot(Root)![0]);
        var other = drive == 'C' ? 'D' : 'C';
        Assert.False(_g.IsAllowed($"{other}:\\wtba-root\\x"));
    }

    [Fact] public void ResolveThrowsOutside() =>
        Assert.Throws<UnauthorizedAccessException>(() => _g.Resolve(Path.Combine(Root, "..", "x")));
}

public class SessionNamesTests
{
    [Theory]
    [InlineData("WTBA-run1", true)]
    [InlineData("WTBA-", false)]
    [InlineData("wtba-run1", false)]
    [InlineData("NT Kernel Logger", false)]
    [InlineData("EventLog-System", false)]
    [InlineData("WTBA-a b", false)]
    [InlineData("XWTBA-1", false)]
    [InlineData(null, false)]
    public void Eligibility(string? name, bool expected) => Assert.Equal(expected, SessionNames.IsEligible(name));

    [Fact]
    public void CleanupNeverTargetsOthers()
    {
        var fake = new[] { "NT Kernel Logger", "WTBA-1", "Circular Kernel Context Logger", "WTBA-2", "UserNotPresentTraceSession" };
        Assert.Equal(new[] { "WTBA-1", "WTBA-2" }, SessionNames.FilterEligible(fake));
    }

    [Fact]
    public void ParsesLogmanOutput()
    {
        var text = "Data Collector Set                      Type                          Status\r\n-------------------------------------------------------------------------------\r\nWTBA-run1                               Trace                         Running\r\nEventLog-System                         Trace                         Running\r\n\r\nThe command completed successfully.\r\n";
        Assert.Equal(new[] { "WTBA-run1", "EventLog-System" }, SessionNames.ParseLogmanQuery(text));
    }
}

public class SafetyGatesTests
{
    private static SystemSnapshot Good => new(true, 50, 90, false, false, false);

    [Fact] public void GoodPasses() => Assert.True(SafetyGates.CheckStart(Good).Passed);
    [Fact] public void NotElevatedFails() => Assert.Contains("not elevated", SafetyGates.CheckStart(Good with { Elevated = false }).Reasons);
    [Fact] public void LowDiskFails() => Assert.False(SafetyGates.CheckStart(Good with { FreeDiskGb = 4.9 }).Passed);
    [Fact] public void DiskExactlyFiveOk() => Assert.True(SafetyGates.CheckStart(Good with { FreeDiskGb = 5.0 }).Passed);
    [Fact] public void LowBatteryFails() => Assert.False(SafetyGates.CheckStart(Good with { BatteryPct = 79 }).Passed);
    [Fact] public void BatteryAtThresholdOk() => Assert.True(SafetyGates.CheckStart(Good with { BatteryPct = 80 }).Passed);
    [Fact] public void LowBatteryIgnoredWhenNotBatteryScenario() => Assert.True(SafetyGates.CheckStart(Good with { BatteryPct = 10, OnAc = true }, batteryScenario: false).Passed);
    [Fact] public void OnAcFailsBatteryScenario() => Assert.False(SafetyGates.CheckStart(Good with { OnAc = true }).Passed);
    [Fact] public void SaverMismatchFails() => Assert.False(SafetyGates.CheckStart(Good with { BatterySaver = true }).Passed);
    [Fact] public void SaverMatchOnPasses() => Assert.True(SafetyGates.CheckStart(Good with { BatterySaver = true, DeclaredBatterySaver = true }).Passed);

    [Fact]
    public void MultipleReasonsReported()
    {
        var r = SafetyGates.CheckStart(new SystemSnapshot(false, 1, 10, true, true, false));
        Assert.Equal(5, r.Reasons.Count);
    }

    [Theory]
    [InlineData(26, true)]
    [InlineData(25, false)]
    [InlineData(10, false)]
    public void AbortGate(int pct, bool ok) => Assert.Equal(ok, SafetyGates.CheckAbort(pct).Passed);
}

public class SimulatorAndSchemaTests
{
    [Fact]
    public void DeterministicForSameSeed() =>
        Assert.Equal(BundleWriter.Serialize(Simulator.Generate("idle_10m", "A", "x", 7)),
                     BundleWriter.Serialize(Simulator.Generate("idle_10m", "A", "x", 7)));

    [Fact]
    public void DifferentSeedDiffers() =>
        Assert.NotEqual(BundleWriter.Serialize(Simulator.Generate("idle_10m", "A", "x", 7)),
                        BundleWriter.Serialize(Simulator.Generate("idle_10m", "A", "x", 8)));

    [Fact]
    public void ConditionBHasLowerCpuAndDischarge()
    {
        var a = Simulator.Generate("idle_10m", "A", "a", 3);
        var b = Simulator.Generate("idle_10m", "B", "b", 3);
        Assert.True(b.Processes.Sum(p => p.CpuMs) < a.Processes.Sum(p => p.CpuMs));
        Assert.True(b.BatterySamples.Average(s => s.DischargeMw) < a.BatterySamples.Average(s => s.DischargeMw));
        Assert.True(b.Power.BatterySaver);
        Assert.False(a.Power.BatterySaver);
    }

    [Fact] public void RejectsBadCondition() => Assert.Throws<ArgumentException>(() => Simulator.Generate("idle_10m", "C", "x", 1));

    [Theory]
    [InlineData("idle_10m", "A")]
    [InlineData("idle_10m", "B")]
    [InlineData("edge_video_5m", "A")]
    [InlineData("edge_video_5m", "B")]
    public void BundleSatisfiesSchema(string scenario, string cond)
    {
        var json = BundleWriter.Serialize(Simulator.Generate(scenario, cond, "lbl", 42));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Obj(r, new[] { "schema", "run_id", "scenario", "condition", "label", "started_utc", "duration_s", "machine", "power", "quality", "processes", "battery_samples" });
        Assert.Equal(1, r.GetProperty("schema").GetInt32());
        Assert.True(Guid.TryParse(r.GetProperty("run_id").GetString(), out _));
        Assert.False(string.IsNullOrEmpty(r.GetProperty("scenario").GetString()));
        Assert.Contains(r.GetProperty("condition").GetString(), new[] { "A", "B" });
        Assert.Equal(JsonValueKind.String, r.GetProperty("label").ValueKind);
        Assert.True(DateTimeOffset.TryParse(r.GetProperty("started_utc").GetString(), out _));
        Assert.True(r.GetProperty("duration_s").GetDouble() > 0);

        var m = r.GetProperty("machine");
        Obj(m, new[] { "id_hash", "os_build", "cpu", "logical_cpus" });
        Assert.True(m.GetProperty("id_hash").GetString()!.Length >= 8);
        Assert.True(m.GetProperty("logical_cpus").GetInt32() >= 1);

        var p = r.GetProperty("power");
        Obj(p, new[] { "on_ac", "battery_saver", "plan_guid", "start_pct", "end_pct" });
        Assert.True(p.GetProperty("on_ac").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(p.GetProperty("battery_saver").ValueKind is JsonValueKind.True or JsonValueKind.False);
        foreach (var k in new[] { "start_pct", "end_pct" }) Assert.InRange(p.GetProperty(k).GetInt32(), 0, 100);

        var q = r.GetProperty("quality");
        Obj(q, new[] { "dropped_events", "sampler_gaps_s", "gates_passed" });
        Assert.True(q.GetProperty("dropped_events").GetInt32() >= 0);
        Assert.True(q.GetProperty("sampler_gaps_s").GetDouble() >= 0);

        var procs = r.GetProperty("processes");
        Assert.Equal(JsonValueKind.Array, procs.ValueKind);
        foreach (var pr in procs.EnumerateArray())
        {
            Obj(pr, new[] { "name", "pid", "cpu_samples", "cpu_ms", "context_switches", "wakeups" });
            Assert.False(string.IsNullOrEmpty(pr.GetProperty("name").GetString()));
            foreach (var k in new[] { "pid", "cpu_samples", "context_switches", "wakeups" }) Assert.True(pr.GetProperty(k).GetInt64() >= 0);
            Assert.True(pr.GetProperty("cpu_ms").GetDouble() >= 0);
        }

        var bs = r.GetProperty("battery_samples");
        Assert.Equal(JsonValueKind.Array, bs.ValueKind);
        Assert.NotEmpty(bs.EnumerateArray());
        foreach (var s in bs.EnumerateArray())
        {
            Obj(s, new[] { "t_s", "discharge_mw", "remaining_mwh" });
            Assert.True(s.GetProperty("t_s").GetDouble() >= 0);
            Assert.Equal(JsonValueKind.Number, s.GetProperty("discharge_mw").ValueKind);
            Assert.True(s.GetProperty("remaining_mwh").GetDouble() >= 0);
        }
    }

    // Exactly the required keys, no extras (additionalProperties: false).
    private static void Obj(JsonElement e, string[] keys)
    {
        Assert.Equal(JsonValueKind.Object, e.ValueKind);
        Assert.Equal(keys.OrderBy(x => x), e.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    }
}

public class CliTests
{
    [Fact]
    public void SimulateWritesBundleUnderAllowedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wtba-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rc = Cli.Run(new[] { "simulate", "--scenario", "idle_10m", "--condition", "B", "--label", "t", "--seed", "5", "--out", Path.Combine(root, "o") },
                new StringWriter(), new StringWriter(), new PathGuard(new[] { root }));
            Assert.Equal(0, rc);
            Assert.True(File.Exists(Path.Combine(root, "o", "bundle.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SimulateRefusesOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wtba-test-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "wtba-outside-" + Guid.NewGuid().ToString("N"));
        var rc = Cli.Run(new[] { "simulate", "--out", outside }, new StringWriter(), new StringWriter(), new PathGuard(new[] { root }));
        Assert.Equal(1, rc);
        Assert.False(Directory.Exists(outside));
    }
}
