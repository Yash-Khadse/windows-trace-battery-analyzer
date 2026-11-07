using Wtba.Collector;

namespace Wtba.Collector.Tests;

// Fakes only: nothing here touches TraceEventSession, WMI or any real ETW.
internal sealed class FakeSession : IEtwSession
{
    public string Name { get; }
    public int Starts, Stops, Disposes;
    public long Size;
    public bool ThrowSize;
    public long MaxFileBytes { get; set; }
    public FakeSession(string name) => Name = name;
    public void Start() => Starts++;
    public void Stop() => Stops++;
    public long FileSizeBytes() => ThrowSize ? throw new InvalidOperationException("probe failed") : Size;
    public void Dispose() => Disposes++;
}

internal sealed class FakeFactory : IEtwSessionFactory
{
    public FakeSession? Last;
    public int Created;
    public IEtwSession Create(string sessionName, string etlPath) { Created++; return Last = new FakeSession(sessionName); }
}

internal sealed class FakeCatalog : IEtwSessionCatalog
{
    public List<string> Names = new();
    public List<string> StoppedNames = new();
    public IReadOnlyList<string> ActiveNames() => Names;
    public void Stop(string name) => StoppedNames.Add(name);
}

internal sealed class FakeBattery : IBatteryReader
{
    public Queue<BatteryReading?> Readings = new();
    public BatteryReading? Read() => Readings.Count > 0 ? Readings.Dequeue() : null;
}

public class EtwRecorderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wtba-cap-" + Guid.NewGuid().ToString("N"));
    private static PathGuard Guard => new(new[] { Root });
    private const string RunId = "0123abcd-0000-0000-0000-000000000000";

    private static RecorderOptions Opts(double durS = 5, TimeSpan? slack = null, long max = long.MaxValue) =>
        new(RunId, TimeSpan.FromSeconds(durS), Path.Combine(Root, "runs", RunId), max, 5.0,
            TimeSpan.FromMilliseconds(10), slack ?? TimeSpan.FromMilliseconds(50));

    [Fact]
    public void SessionNameUsesPrefixAndEightChars() => Assert.Equal("WTBA-0123abcd", EtwRecorder.SessionNameFor(RunId));

    [Fact]
    public async Task CompletesAndStopsSession()
    {
        var f = new FakeFactory();
        var r = await new EtwRecorder(f, Guard, () => 100).RecordAsync(Opts(), _ => Task.CompletedTask, default);
        Assert.Equal(StopReason.Completed, r.Reason);
        Assert.Equal(1, f.Last!.Starts); Assert.Equal(1, f.Last.Stops); Assert.Equal(1, f.Last.Disposes);
        Assert.StartsWith("WTBA-", f.Last.Name);
    }

    [Fact]
    public async Task StopsSessionWhenWorkloadThrows()
    {
        var f = new FakeFactory();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EtwRecorder(f, Guard, () => 100).RecordAsync(Opts(), _ => throw new InvalidOperationException("boom"), default));
        Assert.Equal(1, f.Last!.Stops); Assert.Equal(1, f.Last.Disposes);
    }

    [Fact]
    public async Task StopsSessionWhenStartThrows()
    {
        var f = new ThrowingStartFactory();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EtwRecorder(f, Guard, () => 100).RecordAsync(Opts(), _ => Task.CompletedTask, default));
        Assert.Equal(1, f.S.Stops); Assert.Equal(1, f.S.Disposes);
    }

    private sealed class ThrowingStartFactory : IEtwSessionFactory
    {
        public readonly ThrowingSession S = new("WTBA-0123abcd");
        public IEtwSession Create(string n, string p) => S;
    }

    private sealed class ThrowingSession : IEtwSession
    {
        public string Name { get; }
        public int Stops, Disposes;
        public ThrowingSession(string n) => Name = n;
        public void Start() => throw new InvalidOperationException("start failed");
        public void Stop() => Stops++;
        public long FileSizeBytes() => 0;
        public void Dispose() => Disposes++;
    }

    [Fact]
    public async Task StopsSessionOnCancel()
    {
        var f = new FakeFactory();
        using var cts = new CancellationTokenSource();
        var rec = new EtwRecorder(f, Guard, () => 100);
        var t = rec.RecordAsync(Opts(60, TimeSpan.FromSeconds(60)), ct => Task.Delay(Timeout.Infinite, ct), cts.Token);
        cts.CancelAfter(50);
        var r = await t;
        Assert.Equal(StopReason.Cancelled, r.Reason);
        Assert.Equal(1, f.Last!.Stops); Assert.Equal(1, f.Last.Disposes);
    }

    [Fact]
    public async Task WatchdogStopsHungWorkload()
    {
        var f = new FakeFactory();
        var o = Opts(0, TimeSpan.FromMilliseconds(80));
        var r = await new EtwRecorder(f, Guard, () => 100).RecordAsync(o, ct => Task.Delay(Timeout.Infinite, ct), default);
        Assert.Equal(StopReason.Watchdog, r.Reason);
        Assert.Equal(1, f.Last!.Stops); Assert.Equal(1, f.Last.Disposes);
    }

    [Fact]
    public async Task SizeCapStopsSession()
    {
        var f = new FakeFactory();
        var r = await new EtwRecorder(f, Guard, () => 100).RecordAsync(Opts(60, TimeSpan.FromSeconds(60), max: 10),
            async ct => { f.Last!.Size = 1000; await Task.Delay(Timeout.Infinite, ct); }, default);
        Assert.Equal(StopReason.SizeCap, r.Reason);
        Assert.Equal(1, f.Last!.Stops);
    }

    [Fact]
    public async Task LowDiskDuringRunStopsSession()
    {
        var f = new FakeFactory();
        int calls = 0;
        var r = await new EtwRecorder(f, Guard, () => ++calls == 1 ? 100 : 1).RecordAsync(Opts(60, TimeSpan.FromSeconds(60)),
            ct => Task.Delay(Timeout.Infinite, ct), default);
        Assert.Equal(StopReason.DiskLow, r.Reason);
        Assert.Equal(1, f.Last!.Stops);
    }

    [Fact]
    public async Task LowDiskAtStartNeverCreatesSession()
    {
        var f = new FakeFactory();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EtwRecorder(f, Guard, () => 1).RecordAsync(Opts(), _ => Task.CompletedTask, default));
        Assert.Equal(0, f.Created);
    }

    [Fact]
    public async Task OutDirOutsideRootIsRejectedBeforeSession()
    {
        var f = new FakeFactory();
        var o = Opts() with { OutDir = Path.Combine(Root, "..", "elsewhere") };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new EtwRecorder(f, Guard, () => 100).RecordAsync(o, _ => Task.CompletedTask, default));
        Assert.Equal(0, f.Created);
    }

    [Fact]
    public async Task BatteryAbortStopsSession()
    {
        var f = new FakeFactory();
        var rec = new EtwRecorder(f, Guard, () => 100);
        var bat = new FakeBattery();
        bat.Readings.Enqueue(new BatteryReading(5000, 1000, 25));
        var sampler = new BatterySampler(bat, m => rec.RequestStop(StopReason.Aborted, m));
        var r = await rec.RecordAsync(Opts(60, TimeSpan.FromSeconds(60)), ct => { sampler.SampleOnce(); return Task.Delay(Timeout.Infinite, ct); }, default);
        Assert.Equal(StopReason.Aborted, r.Reason);
        Assert.Equal(1, f.Last!.Stops);
    }
}

public class SessionCleanupTests
{
    [Fact]
    public void StopsOnlyWtbaSessions()
    {
        var c = new FakeCatalog { Names = { "NT Kernel Logger", "WTBA-aaaa1111", "EventLog-System", "wtba-lower", "WTBA-", "WTBA-bbbb2222" } };
        var stopped = SessionCleanup.Cleanup(c);
        Assert.Equal(new[] { "WTBA-aaaa1111", "WTBA-bbbb2222" }, c.StoppedNames);
        Assert.Equal(c.StoppedNames, stopped);
    }

    [Fact]
    public void NothingToStop()
    {
        var c = new FakeCatalog { Names = { "NT Kernel Logger" } };
        Assert.Empty(SessionCleanup.Cleanup(c));
        Assert.Empty(c.StoppedNames);
    }
}

public class BatterySamplerTests
{
    [Fact]
    public void AbortsOnceAtOrBelow25()
    {
        var bat = new FakeBattery();
        foreach (var p in new[] { 40, 26, 25, 20 }) bat.Readings.Enqueue(new BatteryReading(5000, 1000, p));
        var aborts = new List<string>();
        var s = new BatterySampler(bat, aborts.Add);
        for (int i = 0; i < 4; i++) s.SampleOnce();
        Assert.Single(aborts);
        Assert.Equal(4, s.Samples.Count);
    }

    [Fact]
    public void DoesNotAbortAt26()
    {
        var bat = new FakeBattery();
        bat.Readings.Enqueue(new BatteryReading(5000, 1000, 26));
        var aborts = new List<string>();
        new BatterySampler(bat, aborts.Add).SampleOnce();
        Assert.Empty(aborts);
    }

    [Fact]
    public void RecordsGapsWhenReadingsMissing()
    {
        var bat = new FakeBattery();
        double now = 0;
        var s = new BatterySampler(bat, _ => { }, TimeSpan.FromSeconds(5), () => now);
        bat.Readings.Enqueue(new BatteryReading(1, 1, 90)); s.SampleOnce();
        now = 5; bat.Readings.Enqueue(null); s.SampleOnce();      // missed
        now = 10; bat.Readings.Enqueue(null); s.SampleOnce();     // missed
        now = 15; bat.Readings.Enqueue(new BatteryReading(1, 1, 90)); s.SampleOnce();
        Assert.Equal(2, s.Samples.Count);
        Assert.Equal(10.0, s.GapSeconds, 3);
    }

    [Fact]
    public void ReaderExceptionIsAGapNotACrash()
    {
        var s = new BatterySampler(new ThrowingReader(), _ => { });
        s.SampleOnce();
        Assert.Empty(s.Samples);
    }

    private sealed class ThrowingReader : IBatteryReader { public BatteryReading? Read() => throw new InvalidOperationException(); }

    [Fact]
    public async Task BackgroundLoopSamplesUntilCancelled()
    {
        var bat = new FakeBattery();
        for (int i = 0; i < 100; i++) bat.Readings.Enqueue(new BatteryReading(1, 1, 90));
        using var cts = new CancellationTokenSource();
        int ticks = 0;
        var s = new BatterySampler(bat, _ => { }, TimeSpan.FromSeconds(5), null,
            (_, ct) => { if (++ticks >= 3) cts.Cancel(); return Task.CompletedTask; });
        await s.Start(cts.Token);
        Assert.Equal(3, s.Samples.Count);
    }
}

public class TraceAggregatorTests
{
    [Fact]
    public void CpuMsUsesInterval()
    {
        var a = new TraceAggregator();
        a.OnProcess(10, "a.exe");
        for (int i = 0; i < 4; i++) a.OnCpuSample(10);
        a.SetSampleIntervalMs(0.5);
        var p = Assert.Single(a.Result());
        Assert.Equal(("a.exe", 10, 4L, 2.0), (p.Name, p.Pid, p.CpuSamples, p.CpuMs));
    }

    [Fact]
    public void UnattributedPidMinusOneIsReportedAsPidZeroNamedUnattributed()
    {
        var a = new TraceAggregator();
        a.OnCpuSample(-1);
        var p = Assert.Single(a.Result());
        Assert.Equal(("(unattributed)", 0, 1L), (p.Name, p.Pid, p.CpuSamples));
    }

    [Fact]
    public void DefaultIntervalIsOneMs()
    {
        var a = new TraceAggregator();
        a.OnCpuSample(7); a.OnCpuSample(7);
        Assert.Equal(2.0, a.Result()[0].CpuMs);
        Assert.Equal("unknown", a.Result()[0].Name);
    }

    [Fact]
    public void ContextSwitchesCountedByNewProcessAndWakeupsFromIdle()
    {
        var a = new TraceAggregator();
        a.OnProcess(10, "a.exe"); a.OnProcess(20, "b.exe");
        a.OnContextSwitch(0, 0, 10, 100);    // idle -> a : cs + wakeup for a
        a.OnContextSwitch(10, 100, 20, 200); // a -> b    : cs for b only
        a.OnContextSwitch(20, 200, 10, 101); // b -> a    : cs for a only
        a.OnContextSwitch(10, 101, 0, 0);    // a -> idle : not counted
        a.OnContextSwitch(0, 0, 20, 201);    // idle -> b : cs + wakeup for b
        a.OnContextSwitch(0, 0, 10, 100);    // idle -> a : cs + wakeup for a
        var r = a.Result().ToDictionary(x => x.Name);
        Assert.Equal(3, r["a.exe"].ContextSwitches); Assert.Equal(2, r["a.exe"].Wakeups);
        Assert.Equal(2, r["b.exe"].ContextSwitches); Assert.Equal(1, r["b.exe"].Wakeups);
    }

    [Fact]
    public void IdleProcessIsExcluded()
    {
        var a = new TraceAggregator();
        a.OnProcess(0, "Idle");
        a.OnCpuSample(0);
        a.OnContextSwitch(5, 5, 0, 0);
        Assert.Empty(a.Result());
    }

    [Fact]
    public void BundleBuildsAndHasNoCommandLines()
    {
        var a = new TraceAggregator();
        a.OnProcess(10, "a.exe"); a.OnCpuSample(10);
        var meta = new RunMetadata("r", "idle_10m", "A", "l", DateTime.UtcNow, 60, false, 90, 89, true);
        var b = EtlExtractor.BuildBundle(meta, new EtlAggregate(a.Result(), 3), Array.Empty<BatterySample>(), 1.5);
        Assert.Equal(3, b.Quality.DroppedEvents);
        Assert.Equal(1.5, b.Quality.SamplerGapsS);
        Assert.DoesNotContain("command", BundleWriter.Serialize(b), StringComparison.OrdinalIgnoreCase);
    }
}

public class CliCaptureTests
{
    private static CaptureServices Svc(FakeFactory f, FakeCatalog c, bool elevated, int pct = 90, bool ac = false, bool saver = false) =>
        new(f, c, () => new FakeBattery(), () => elevated, () => (pct, ac, saver), _ => 100.0);

    private static PathGuard G => new(new[] { Path.GetTempPath() });

    [Fact]
    public void RunFailsFastWhenNotElevated()
    {
        var f = new FakeFactory(); var e = new StringWriter();
        var rc = Cli.Run(new[] { "run", "--scenario", "idle_10m", "--condition", "A", "--battery-saver", "off" },
            new StringWriter(), e, G, Svc(f, new FakeCatalog(), false), new StringReader("yes"));
        Assert.Equal(3, rc);
        Assert.Contains("elevated", e.ToString());
        Assert.Equal(0, f.Created);
    }

    [Fact]
    public void RunRefusesWhenBatterySaverMismatch()
    {
        var f = new FakeFactory(); var e = new StringWriter();
        var rc = Cli.Run(new[] { "run", "--scenario", "idle_10m", "--condition", "B", "--battery-saver", "on" },
            new StringWriter(), e, G, Svc(f, new FakeCatalog(), true, saver: false), new StringReader("yes"));
        Assert.Equal(3, rc);
        Assert.Contains("battery saver", e.ToString());
        Assert.Equal(0, f.Created);
    }

    [Fact]
    public void RunRequiresBatterySaverDeclaration()
    {
        var f = new FakeFactory();
        var rc = Cli.Run(new[] { "run", "--scenario", "idle_10m", "--condition", "A" },
            new StringWriter(), new StringWriter(), G, Svc(f, new FakeCatalog(), true), new StringReader("yes"));
        Assert.Equal(1, rc);
        Assert.Equal(0, f.Created);
    }

    [Fact]
    public void RunWithoutConfirmationStartsNothing()
    {
        var f = new FakeFactory();
        var rc = Cli.Run(new[] { "run", "--scenario", "idle_10m", "--condition", "A", "--battery-saver", "off" },
            new StringWriter(), new StringWriter(), G, Svc(f, new FakeCatalog(), true), new StringReader("no"));
        Assert.Equal(3, rc);
        Assert.Equal(0, f.Created);
    }

    [Fact]
    public void CleanupStopsOnlyWtba()
    {
        var c = new FakeCatalog { Names = { "NT Kernel Logger", "WTBA-1234abcd" } };
        var rc = Cli.Run(new[] { "cleanup" }, new StringWriter(), new StringWriter(), G, Svc(new FakeFactory(), c, true));
        Assert.Equal(0, rc);
        Assert.Equal(new[] { "WTBA-1234abcd" }, c.StoppedNames);
    }

    [Fact]
    public void CleanupRefusesWhenNotElevated()
    {
        var c = new FakeCatalog { Names = { "WTBA-1234abcd" } };
        var rc = Cli.Run(new[] { "cleanup" }, new StringWriter(), new StringWriter(), G, Svc(new FakeFactory(), c, false));
        Assert.Equal(3, rc);
        Assert.Empty(c.StoppedNames);
    }

    [Fact]
    public void StatusListsSessionsReadOnly()
    {
        var c = new FakeCatalog { Names = { "NT Kernel Logger", "WTBA-1234abcd" } };
        var o = new StringWriter();
        Assert.Equal(0, Cli.Run(new[] { "status" }, o, new StringWriter(), G, Svc(new FakeFactory(), c, true)));
        Assert.Contains("WTBA-1234abcd", o.ToString());
        Assert.Empty(c.StoppedNames);
    }
}

public class TraceAggregatorUnresolvedPidTests
{
    [Fact]
    public void UnresolvedTidResolvedByLateThreadEvent()
    {
        var a = new TraceAggregator();
        a.OnProcess(50, "dotnet.exe");
        a.OnCpuSample(50, 500); a.OnCpuSample(-1, 501); a.OnCpuSample(-1, 501);
        a.OnContextSwitch(0, 0, -1, 501);       // wakeup + cs, pid unknown
        a.OnContextSwitch(-1, 501, 50, 500);    // cs for 50
        a.OnThread(501, 50);                    // rundown arrives last
        var p = Assert.Single(a.Result());
        Assert.Equal(50, p.Pid);
        Assert.Equal(3, p.CpuSamples); Assert.Equal(2, p.ContextSwitches); Assert.Equal(1, p.Wakeups);
    }

    [Fact]
    public void StillUnresolvedStaysUnattributedAndTotalsConserved()
    {
        var a = new TraceAggregator();
        a.OnProcess(50, "x.exe");
        a.OnCpuSample(50, 1); a.OnCpuSample(-1, 7); a.OnCpuSample(-1, 8); a.OnCpuSample(-1, 8);
        a.OnContextSwitch(0, 0, -1, 8);
        int before = 4;
        a.OnThread(7, 50);
        var r = a.Result();
        Assert.Equal(before, r.Sum(x => x.CpuSamples));
        var u = r.Single(x => x.Name == "(unattributed)");
        Assert.Equal("(unattributed)", u.Name);
        Assert.Equal(2, u.CpuSamples); Assert.Equal(1, u.ContextSwitches); Assert.Equal(1, u.Wakeups);
        Assert.Equal(2, r.Single(x => x.Pid == 50).CpuSamples);
        Assert.Equal(r.Sum(x => x.CpuSamples), a.Result().Sum(x => x.CpuSamples)); // repeatable
    }
}
