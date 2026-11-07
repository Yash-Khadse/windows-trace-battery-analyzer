using Wtba.Collector;

namespace Wtba.Collector.Tests;

// Item 1: reparse points and handle-based delete

public class ReparsePathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wtba-rp-root");

    [Fact]
    public void ResolveRefusesAnyReparseComponentBelowRoot()
    {
        var bad = Path.Combine(Root, "data");
        var g = new PathGuard(new[] { Root }, p => string.Equals(p, bad, StringComparison.OrdinalIgnoreCase));
        var ex = Assert.Throws<UnauthorizedAccessException>(() => g.Resolve(Path.Combine(Root, "data", "runs", "x")));
        Assert.Contains("reparse", ex.Message);
        Assert.Equal(bad, g.FindReparsePoint(Path.Combine(Root, "data", "runs", "x")), ignoreCase: true);
    }

    [Fact]
    public void RootItselfIsNotJudgedButEveryChildIs()
    {
        var g = new PathGuard(new[] { Root }, p => string.Equals(p, Root, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "data")), g.Resolve(Path.Combine(Root, "data")));
        var g2 = new PathGuard(new[] { Root }, p => p.EndsWith("runs", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<UnauthorizedAccessException>(() => g2.Resolve(Path.Combine(Root, "data", "runs")));
        Assert.Throws<UnauthorizedAccessException>(() => g2.Resolve(Path.Combine(Root, "data", "runs", "a", "b")));
    }

    [Fact]
    public void IsAllowedStaysTextOnly()
    {
        var g = new PathGuard(new[] { Root }, _ => true);
        Assert.True(g.IsAllowed(Path.Combine(Root, "data")));
        Assert.Throws<UnauthorizedAccessException>(() => g.Resolve(Path.Combine(Root, "data")));
    }

    [Fact]
    public void RealSymlinkDirectoryIsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "wtba-rp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Path.Combine(root, "elsewhere"); Directory.CreateDirectory(target);
            var link = Path.Combine(root, "data");
            try { Directory.CreateSymbolicLink(link, target); } catch (Exception) { return; } // no privilege: nothing to test
            var g = new PathGuard(new[] { root });
            Assert.Throws<UnauthorizedAccessException>(() => g.Resolve(Path.Combine(link, "runs", "x")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void RealPlainTempDirectoriesPass()
    {
        var root = Path.Combine(Path.GetTempPath(), "wtba-rp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data", "runs"));
            Assert.NotNull(new PathGuard(new[] { root }).Resolve(Path.Combine(root, "data", "runs", "x")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}

public class VerifiedDeleteTests
{
    [Fact]
    public void ReparseDataFolderRefusedByCleaner()
    {
        var fs = new FakeFs(); var p = fs.AddRun();
        fs.Reparse.Add(Path.GetFullPath(Path.GetDirectoryName(FakeFs.Root)!)); // ...\data is a junction
        var c = new EtlCleaner(fs, FakeFs.Root);
        Assert.Contains("reparse", c.Refusal(p));
        Assert.Empty(c.Clean(new[] { p }).DeletedPaths); Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void SwapBetweenCheckAndDeleteIsCaught()
    {
        var fs = new FakeFs(); var p = fs.AddRun();
        // the check passes, then a junction is swapped in: the opened handle resolves somewhere else
        fs.BeforeDelete = path => fs.ActualFinal[Path.GetFullPath(path)] = Path.Combine(Path.GetTempPath(), "evil", "x.etl");
        var r = new EtlCleaner(fs, FakeFs.Root).Clean(new[] { p });
        Assert.Empty(fs.Deleted);
        Assert.Equal(0, r.FreedBytes);
        Assert.Contains("delete failed", r.Items.Single().Reason);
    }

    [Fact]
    public void UnresolvableRunsRootRefusesEverything()
    {
        var fs = new FakeFs();
        var p = Path.Combine(FakeFs.Root, Guid.NewGuid().ToString()); // runs root never created => cannot be resolved
        Assert.NotNull(new EtlCleaner(fs, FakeFs.Root).Refusal(Path.Combine(p, Path.GetFileName(p) + ".etl")));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "wtba-vd-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RealHandleDeleteWithWrongExpectedPathDeletesNothing()
    {
        var root = NewRoot(); Directory.CreateDirectory(root);
        try
        {
            var f = Path.Combine(root, "a.bin"); File.WriteAllBytes(f, new byte[4]);
            Assert.Throws<UnauthorizedAccessException>(() => NativeFs.DeleteVerified(f, Path.Combine(root, "elsewhere", "a.bin")));
            Assert.True(File.Exists(f));
            var real = NativeFs.RealDirectoryPath(root)!;
            NativeFs.DeleteVerified(f, Path.Combine(real, "a.bin"));
            Assert.False(File.Exists(f));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void RealHandleDeleteRefusesDirectories()
    {
        var root = NewRoot(); Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            var real = NativeFs.RealDirectoryPath(root)!;
            var ex = Record.Exception(() => NativeFs.DeleteVerified(Path.Combine(root, "sub"), Path.Combine(real, "sub")));
            Assert.NotNull(ex);
            Assert.True(Directory.Exists(Path.Combine(root, "sub")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void RealSymlinkFileIsNotFollowedOrDeleted()
    {
        var root = NewRoot(); Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target.bin"); File.WriteAllBytes(target, new byte[4]);
            var link = Path.Combine(root, "link.bin");
            try { File.CreateSymbolicLink(link, target); } catch (Exception) { return; }
            var real = NativeFs.RealDirectoryPath(root)!;
            Assert.ThrowsAny<Exception>(() => NativeFs.DeleteVerified(link, Path.Combine(real, "link.bin")));
            Assert.True(File.Exists(target));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}

// Item 2: independent power watchdog and sampler quality gates

public class PowerWatchdogTests
{
    private static PowerWatchdog Wd(Queue<int> q, List<string> aborts, int abort = 25) =>
        new(() => (q.Count > 1 ? q.Dequeue() : q.Peek(), false, false), aborts.Add, abort);

    [Fact] public void AbortsAtThreshold() { var a = new List<string>(); Wd(new Queue<int>(new[] { 26, 25 }), a).Let(w => { w.PollOnce(); Assert.Empty(a); w.PollOnce(); Assert.Single(a); }); }
    [Fact] public void FiresOnlyOnce() { var a = new List<string>(); var w = Wd(new Queue<int>(new[] { 10 }), a); w.PollOnce(); w.PollOnce(); w.PollOnce(); Assert.Single(a); }

    [Fact]
    public void ThreeConsecutiveUnknownReadsAbort()
    {
        var a = new List<string>(); var w = Wd(new Queue<int>(new[] { 255 }), a);
        w.PollOnce(); w.PollOnce(); Assert.Empty(a);
        w.PollOnce(); Assert.Single(a); Assert.Contains("unknown", a[0]);
    }

    [Fact]
    public void KnownReadResetsUnknownCounter()
    {
        var a = new List<string>(); var q = new Queue<int>(new[] { 255, 255, 80, 255, 255, 80 });
        var w = Wd(q, a); for (int i = 0; i < 6; i++) w.PollOnce();
        Assert.Empty(a);
    }

    [Fact]
    public void ThrowingReaderCountsAsUnknown()
    {
        var a = new List<string>();
        var w = new PowerWatchdog(() => throw new InvalidOperationException(), a.Add);
        for (int i = 0; i < 3; i++) w.PollOnce();
        Assert.Single(a);
    }

    [Fact]
    public async Task BackgroundLoopPollsUntilCancelled()
    {
        var a = new List<string>(); int reads = 0;
        using var cts = new CancellationTokenSource();
        var w = new PowerWatchdog(() => (50 + ++reads, false, false), a.Add, 25, TimeSpan.FromSeconds(5),
            (_, ct) => { if (reads >= 4) cts.Cancel(); return Task.CompletedTask; });
        await w.Start(cts.Token);
        Assert.True(reads >= 4);
        Assert.Empty(a);
    }

    [Fact]
    public void RunAbortsFromGetSystemPowerStatusEvenWhenWmiIsDead()
    {
        using var env = new Env { BlockWorkload = true };
        env.PctQueue = new Queue<int>(new[] { 90, 20 }); // start gate reads 90, the watchdog's first poll reads 20
        var svc = env.Svc() with { Battery = () => new NullBattery() };
        var oc = Cli.RunOnce(new RunRequest(Guid.NewGuid().ToString(), "t", "A", "l", false, 0.01, 80, 25, env.DataRoot), new StringWriter(), env.Guard, svc, default);
        Assert.Equal(StopReason.Aborted, oc.Rec!.Reason);
        Assert.Equal(1, env.F.Last!.Stops);
    }

    private sealed class NullBattery : IBatteryReader { public BatteryReading? Read() => null; }
}

internal static class Ext { public static void Let<T>(this T t, Action<T> a) => a(t); }

public class SamplerGateTests
{
    private static RunOutcome Oc(double duration, params double[] sampleTimes)
    {
        var b = new Bundle(1, "r", "s", "A", "l", "t", duration, new MachineInfo("h", "o", "c", 1),
            new PowerInfo(false, false, "g", 90, 89), new QualityInfo(0, 0, true), Array.Empty<ProcessStat>(),
            sampleTimes.Select(t => new BatterySample(t, 1, 1)).ToArray());
        return new RunOutcome(0, null, "r", new RecordResult("WTBA-x", "p", StopReason.Completed, null), b, "p", 90, 89);
    }

    [Fact] public void NoSamplesRejectedWhenSamplerWasOn() => Assert.Contains(RunGates.Evaluate(Oc(100), false, 80, false, false), x => x.Contains("no battery samples"));
    [Fact] public void NoSamplesAcceptedWhenSamplerOff() => Assert.Empty(RunGates.Evaluate(Oc(100), false, 80, false, false, samplerOn: false));
    [Fact] public void SilentFinalStretchRejected() => Assert.Contains(RunGates.Evaluate(Oc(100, 0, 5, 80), false, 80, false, false), x => x.Contains("silent"));
    [Fact] public void FinalGapWithinLimitAccepted() => Assert.Empty(RunGates.Evaluate(Oc(100, 0, 90, 95), false, 80, false, false));
}

// Item 3: session lifecycle

public class SessionLifecycleTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wtba-life-" + Guid.NewGuid().ToString("N"));
    private const string RunId = "0123abcd-0000-0000-0000-000000000000";
    private static RecorderOptions Opts(long max = long.MaxValue) =>
        new(RunId, TimeSpan.FromSeconds(60), Path.Combine(Root, "runs", RunId), max, 5.0, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(60));

    [Fact]
    public async Task SessionLevelSizeCapIsPassedToTheSession()
    {
        var f = new FakeFactory();
        await new EtwRecorder(f, new PathGuard(new[] { Root }), () => 100).RecordAsync(Opts(12345), _ => Task.CompletedTask, default);
        Assert.Equal(12345, f.Last!.MaxFileBytes);
    }

    [Fact]
    public async Task GuardLoopFailureStopsSessionOnAnyException()
    {
        var f = new FakeFactory();
        var rec = new EtwRecorder(f, new PathGuard(new[] { Root }), () => 100);
        var r = await rec.RecordAsync(Opts(), async ct =>
        {
            f.Last!.ThrowSize = true; // the guard loop's probe now throws a non-cancellation exception
            await Task.Delay(Timeout.Infinite, ct);
        }, default);
        Assert.Equal(StopReason.Watchdog, r.Reason);
        Assert.Contains("guard loop failed", r.Detail);
        Assert.True(f.Last!.Stops >= 1);
        Assert.Equal(1, f.Last.Disposes);
    }

    [Fact]
    public async Task ShutdownSignalStopsTheSessionExactlyOnce()
    {
        var f = new FakeFactory(); var reg = new ShutdownRegistry();
        var rec = new EtwRecorder(f, new PathGuard(new[] { Root }), () => 100, reg);
        var r = await rec.RecordAsync(Opts(), async ct =>
        {
            Assert.Equal(1, reg.Count);
            reg.RunAll(); // console close / logoff / shutdown / SIGTERM / ProcessExit
            Assert.Equal(1, f.Last!.Stops);
            await Task.CompletedTask;
        }, default);
        Assert.Equal(1, f.Last!.Stops); // finally does not stop it a second time
        Assert.Equal(0, reg.Count);    // hook removed after the recording
    }

    [Fact]
    public void RegistrySwallowsFailingCallbacksAndRunsAllOnce()
    {
        var reg = new ShutdownRegistry(); int n = 0;
        reg.Register(() => throw new InvalidOperationException());
        reg.Register(() => n++);
        reg.RunAll(); reg.RunAll();
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task SizeCapPollerStopsOnceWhenOverCap()
    {
        int stops = 0;
        using var p = new SizeCapPoller(() => 100, 10, () => stops++, TimeSpan.FromMilliseconds(10));
        await Task.Delay(200);
        Assert.Equal(1, stops); Assert.True(p.Fired);
    }

    [Fact]
    public async Task SizeCapPollerStopsWhenProbeFails()
    {
        int stops = 0;
        using var p = new SizeCapPoller(() => throw new IOException(), 10, () => stops++, TimeSpan.FromMilliseconds(10));
        await Task.Delay(200);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task SizeCapPollerStaysQuietUnderCap()
    {
        int stops = 0;
        using var p = new SizeCapPoller(() => 1, 10, () => stops++, TimeSpan.FromMilliseconds(10));
        await Task.Delay(100);
        Assert.Equal(0, stops);
    }
}

public class StaleSessionAndDurationTests
{
    private static string[] RunArgs(params string[] extra) =>
        new[] { "run", "--scenario", "idle_10m", "--condition", "A", "--battery-saver", "off" }.Concat(extra).ToArray();

    [Fact]
    public void RunRefusesWithStaleSessionAndPrintsCleanupCommand()
    {
        using var env = new Env(); env.C.Names.Add("WTBA-deadbeef");
        var e = new StringWriter();
        Assert.Equal(3, Cli.Run(RunArgs(), new StringWriter(), e, env.Guard, env.Svc(), new StringReader("yes\n")));
        Assert.Contains("wtba cleanup", e.ToString()); Assert.Contains("WTBA-deadbeef", e.ToString());
        Assert.Equal(0, env.F.Created);
    }

    [Fact]
    public void NonWtbaSessionsDoNotBlock()
    {
        using var env = new Env(); env.C.Names.Add("NT Kernel Logger");
        Assert.Null(Cli.StaleSessionProblem(env.Svc()));
    }

    [Fact]
    public void UnlistableSessionsFailClosed()
    {
        var svc = new Env().Svc() with { Catalog = new ThrowOnUseCatalog() };
        Assert.Contains("cannot list", Cli.StaleSessionProblem(svc));
    }

    [Fact]
    public void ExperimentRefusesWithStaleSession()
    {
        using var env = new Env(); env.C.Names.Add("WTBA-deadbeef");
        var plan = env.Plan(); env.PrimeSaver(plan);
        var e = new StringWriter();
        Assert.Equal(3, env.Runner(e: e).Execute(plan, "sha", null, false, default));
        Assert.Contains("wtba cleanup", e.ToString());
        Assert.Equal(0, env.F.Created);
    }

    [Fact]
    public void RunOnceRefusesWithStaleSession()
    {
        using var env = new Env(); env.C.Names.Add("WTBA-deadbeef");
        var oc = Cli.RunOnce(new RunRequest(Guid.NewGuid().ToString(), "t", "A", "l", false, 1, 80, 25, env.DataRoot), new StringWriter(), env.Guard, env.Svc(), default);
        Assert.Equal(3, oc.Code); Assert.Equal(0, env.F.Created);
    }

    [Theory]
    [InlineData("7200")]
    [InlineData("3601")]
    [InlineData("0")]
    [InlineData("-5")]
    public void RunCapsDuration(string d)
    {
        using var env = new Env(); var e = new StringWriter();
        Assert.Equal(1, Cli.Run(RunArgs("--duration-s", d), new StringWriter(), e, env.Guard, env.Svc(), new StringReader("yes\n")));
        Assert.Contains("--duration-s must be between 1 and 3600", e.ToString());
        Assert.Equal(0, env.F.Created);
    }

    [Fact]
    public void RunOnceCapsDuration()
    {
        using var env = new Env();
        var oc = Cli.RunOnce(new RunRequest(Guid.NewGuid().ToString(), "t", "A", "l", false, 7200, 80, 25, env.DataRoot), new StringWriter(), env.Guard, env.Svc(), default);
        Assert.Equal(1, oc.Code); Assert.Equal(0, env.F.Created);
    }

    [Fact]
    public void PlanDurationAbove3600Rejected() =>
        Assert.Throws<PlanException>(() => PlanLoader.Parse("scenario: s\nduration_s: 4000\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n"));

    [Fact]
    public void SamplerOffLimitsDurationAndStartCharge()
    {
        using var env = new Env(); var e = new StringWriter();
        Assert.Equal(1, Cli.Run(RunArgs("--battery-sampler", "off", "--duration-s", "121"), new StringWriter(), e, env.Guard, env.Svc(), new StringReader("yes\n")));
        env.Pct = 45;
        Assert.Equal(3, Cli.Run(RunArgs("--battery-sampler", "off", "--duration-s", "60"), new StringWriter(), new StringWriter(), env.Guard, env.Svc(), new StringReader("yes\n")));
        env.Pct = 60;
        var o = new StringWriter();
        Assert.Equal(0, Cli.Run(RunArgs("--battery-sampler", "off", "--duration-s", "60"), o, new StringWriter(), env.Guard, env.Svc(), new StringReader("yes\n")));
        Assert.Contains("ETL:", o.ToString());
    }
}

// Item 4: unknown power state fails closed

public class FailClosedPowerTests
{
    [Theory]
    [InlineData(255, 0, 50)]   // AC state unknown
    [InlineData(2, 0, 50)]     // AC state not 0/1
    [InlineData(0, 0, 255)]    // percentage unknown
    [InlineData(0, 0, 101)]    // percentage out of range
    [InlineData(0, 128, 50)]   // no system battery
    [InlineData(0, 255, 50)]   // battery state unknown
    public void UnknownPowerIsReportedAs255(byte ac, byte flag, byte pct)
    {
        var r = SystemStatus.Interpret(ac, flag, pct, 0, null);
        Assert.Equal(255, r.Pct);
    }

    [Fact]
    public void ExplicitReadsPassThrough()
    {
        Assert.Equal((80, false, false), SystemStatus.Interpret(0, 1, 80, 0, null));
        Assert.Equal((100, true, false), SystemStatus.Interpret(1, 8, 100, 0, null));
        Assert.Equal((0, false, true), SystemStatus.Interpret(0, 4, 0, 1, null));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(101)]
    [InlineData(-1)]
    public void StartGateRefusesUnknownLevel(int pct)
    {
        var g = SafetyGates.CheckStart(new SystemSnapshot(true, 100, pct, false, false, false));
        Assert.False(g.Passed);
        Assert.Contains(g.Reasons, x => x.Contains("unknown"));
    }

    [Fact]
    public void StartGateStillPassesOnAHealthyReading() =>
        Assert.True(SafetyGates.CheckStart(new SystemSnapshot(true, 100, 90, false, false, false)).Passed);

    [Fact] public void AbortCheckTreatsUnknownAsFailure() => Assert.False(SafetyGates.CheckAbort(255).Passed);

    [Fact]
    public void RunVerbRefusesUnknownCharge()
    {
        using var env = new Env { Pct = 255 };
        var e = new StringWriter();
        Assert.Equal(3, Cli.Run(new[] { "run", "--scenario", "idle_10m", "--condition", "A", "--battery-saver", "off" }, new StringWriter(), e, env.Guard, env.Svc(), new StringReader("yes\n")));
        Assert.Contains("unknown", e.ToString());
        Assert.Equal(0, env.F.Created);
    }

    [Fact]
    public void ExperimentPausesOnUnknownCharge()
    {
        using var env = new Env { Pct = 255 };
        var plan = env.Plan(); env.PrimeSaver(plan);
        Assert.Equal(5, env.Runner().Execute(plan, "sha", null, false, default));
        Assert.Equal(0, env.F.Created);
        Assert.Contains("unknown", env.Manifest().PauseReason);
    }
}
