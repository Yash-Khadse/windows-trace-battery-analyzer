using Wtba.Collector;

namespace Wtba.Collector.Tests;

internal sealed class FakeFs : IFileOps
{
    private static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    public readonly Dictionary<string, string> Files = new(C);
    public readonly Dictionary<string, long> Sizes = new(C);
    public readonly HashSet<string> Dirs = new(C), Reparse = new(C), DeleteFails = new(C);
    public readonly List<string> Deleted = new();

    private static string N(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
    public bool FileExists(string p) => Files.ContainsKey(N(p));
    public bool DirectoryExists(string p) => Dirs.Contains(N(p));
    public bool IsReparsePoint(string p) => Reparse.Contains(N(p));
    public long Length(string p) => Sizes.GetValueOrDefault(N(p), 0);
    public string ReadAllText(string p) => Files[N(p)];
    public Action<string>? BeforeDelete;
    public readonly Dictionary<string, string> ActualFinal = new(C);
    public string? RealPath(string d) => N(d);
    public void DeleteFileVerified(string p, string expectedRealPath)
    {
        BeforeDelete?.Invoke(p);
        if (DeleteFails.Contains(N(p))) throw new IOException("locked");
        var actual = ActualFinal.TryGetValue(N(p), out var a) ? a : N(p);
        if (!string.Equals(actual, N(expectedRealPath), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("real path differs");
        Files.Remove(N(p)); Deleted.Add(N(p));
    }
    public IReadOnlyList<string> SubDirectories(string p) =>
        Dirs.Where(d => string.Equals(Path.GetDirectoryName(d), N(p), StringComparison.OrdinalIgnoreCase)).ToList();

    public static string Root => Path.Combine(Path.GetTempPath(), "wtba-fake-root", "data", "runs");

    public string AddRun(string? id = null, bool bundle = true, string? bundleRunId = null, long bytes = 1000,
        DateTime? started = null, bool gates = true, string? folder = null, string? etlName = null)
    {
        id ??= Guid.NewGuid().ToString();
        var dir = Path.Combine(Root, folder ?? id);
        Dirs.Add(N(Root)); Dirs.Add(N(dir));
        var etl = Path.Combine(dir, etlName ?? id + ".etl");
        Files[N(etl)] = "etl"; Sizes[N(etl)] = bytes;
        if (bundle)
            Files[N(Path.Combine(dir, "bundle.json"))] =
                $"{{\"schema\":1,\"run_id\":\"{bundleRunId ?? id}\",\"started_utc\":\"{(started ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)):yyyy-MM-ddTHH:mm:ssZ}\",\"quality\":{{\"gates_passed\":{(gates ? "true" : "false")}}},\"processes\":[]}}";
        return etl;
    }
}

public class EtlRetentionPolicyTests
{
    private static RunEntry E(string id, int day, bool valid = true, bool rej = false) =>
        new(id, "p/" + id, 100, valid, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc), rej);

    private static string[] Del(IEnumerable<RunEntry> es, RetentionPolicy p, bool inc = false) =>
        EtlRetention.Decide(es, p, inc).Where(d => d.Delete).Select(d => d.Entry.RunId).OrderBy(x => x).ToArray();

    private static readonly RunEntry[] Five = { E("a", 1), E("b", 2), E("c", 3), E("d", 4), E("e", 5) };

    [Fact] public void KeepAllDeletesNothing() => Assert.Empty(Del(Five, new(RetentionMode.KeepAll, 0)));
    [Fact] public void DeleteAfterExtractDeletesAllValid() => Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Del(Five, new(RetentionMode.DeleteAfterExtract, 0)));
    [Fact] public void KeepLastKeepsNewestTwo() => Assert.Equal(new[] { "a", "b", "c" }, Del(Five, new(RetentionMode.KeepLast, 2)));
    [Fact] public void NewestKProtectedRegardlessOfInputOrder() => Assert.Equal(new[] { "a", "b", "c" }, Del(Five.Reverse(), new(RetentionMode.KeepLast, 2)));
    [Fact] public void KeepLargerThanCountDeletesNothing() => Assert.Empty(Del(Five, new(RetentionMode.KeepLast, 10)));
    [Fact] public void KeepZeroIsTreatedAsAtLeastOne() => Assert.DoesNotContain("e", Del(Five, new(RetentionMode.KeepLast, 0)));

    [Fact]
    public void InvalidBundleIsNeverDeleted()
    {
        var es = new[] { E("a", 1, valid: false), E("b", 2), E("c", 3), E("d", 4) };
        Assert.Equal(new[] { "b" }, Del(es, new(RetentionMode.KeepLast, 2)));
        Assert.Equal(new[] { "b", "c", "d" }, Del(es, new(RetentionMode.DeleteAfterExtract, 0)));
    }

    [Fact]
    public void RejectedKeptUnlessIncluded()
    {
        var es = new[] { E("a", 1, rej: true), E("b", 2), E("c", 3), E("d", 4) };
        Assert.Equal(new[] { "b" }, Del(es, new(RetentionMode.KeepLast, 2)));
        Assert.Equal(new[] { "a", "b" }, Del(es, new(RetentionMode.KeepLast, 2), inc: true));
        // even when included, an invalid bundle still protects
        Assert.Empty(Del(new[] { E("x", 1, valid: false, rej: true) }, new(RetentionMode.DeleteAfterExtract, 0), inc: true));
    }

    [Fact]
    public void RejectedRunStillCountsTowardNewestK()
    {
        var es = new[] { E("a", 1), E("b", 2), E("c", 3, rej: true) };
        Assert.Equal(new[] { "a" }, Del(es, new(RetentionMode.KeepLast, 2)));
    }

    [Fact]
    public void EveryKeepHasAReason() =>
        Assert.All(EtlRetention.Decide(Five, new(RetentionMode.KeepLast, 2)), d => Assert.False(string.IsNullOrEmpty(d.Reason)));
}

public class EtlCleanerTests
{
    private static EtlCleaner Cl(FakeFs fs) => new(fs, FakeFs.Root);

    [Fact]
    public void DeletesOnlyTheEtlOfAValidRun()
    {
        var fs = new FakeFs(); var etl = fs.AddRun(bytes: 123);
        var r = Cl(fs).Clean(new[] { etl });
        Assert.Equal(123, r.FreedBytes);
        Assert.Single(fs.Deleted);
        Assert.EndsWith(".etl", fs.Deleted[0]);
        Assert.Contains(fs.Files.Keys, k => k.EndsWith("bundle.json"));
    }

    private static string? Refusal(FakeFs fs, string path) => Cl(fs).Refusal(path);

    [Fact] public void RefusesNonGuidFolder() { var fs = new FakeFs(); var p = fs.AddRun(folder: "not-a-guid", etlName: "not-a-guid.etl"); Assert.Contains("GUID", Refusal(fs, p)); }
    [Fact] public void RefusesWrongFileName() { var fs = new FakeFs(); var p = fs.AddRun(etlName: "other.etl"); Assert.Contains("<guid>.etl", Refusal(fs, p)); }
    [Fact] public void RefusesGuidWithDifferentGuidFile() { var fs = new FakeFs(); var p = fs.AddRun(etlName: Guid.NewGuid() + ".etl"); Assert.NotNull(Refusal(fs, p)); }
    [Fact] public void RefusesBackupExtension() { var fs = new FakeFs(); var id = Guid.NewGuid().ToString(); var p = fs.AddRun(id, etlName: id + ".etl.bak"); Assert.NotNull(Refusal(fs, p)); }

    [Fact]
    public void RefusesBundleJsonAndOtherFilesEvenInValidRun()
    {
        var fs = new FakeFs(); var id = Guid.NewGuid().ToString(); fs.AddRun(id);
        var dir = Path.Combine(FakeFs.Root, id);
        Assert.NotNull(Refusal(fs, Path.Combine(dir, "bundle.json")));
        var rep = Cl(fs).Clean(new[] { Path.Combine(dir, "bundle.json"), Path.Combine(dir, "notes.txt") });
        Assert.Empty(fs.Deleted); Assert.All(rep.Items, i => Assert.False(i.Deleted));
    }

    [Fact]
    public void RefusesOutsideRootAndDotDotTraversal()
    {
        var fs = new FakeFs(); var id = Guid.NewGuid().ToString();
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", id, id + ".etl");
        Assert.Contains("outside", Refusal(fs, outside));
        Assert.Contains("outside", Refusal(fs, Path.Combine(FakeFs.Root, "..", "..", "elsewhere", id, id + ".etl")));
        Assert.Contains("outside", Refusal(fs, Path.Combine(FakeFs.Root, id, "..", "..", "x.etl")));
    }

    [Fact] public void RefusesNestedTooDeep() { var fs = new FakeFs(); var id = Guid.NewGuid().ToString(); Assert.NotNull(Refusal(fs, Path.Combine(FakeFs.Root, "x", id, id + ".etl"))); }
    [Fact] public void RefusesWildcards() { var fs = new FakeFs(); Assert.Equal("invalid path", Refusal(fs, Path.Combine(FakeFs.Root, "*", "*.etl"))); }
    [Fact] public void RefusesRootItself() { var fs = new FakeFs(); Assert.NotNull(Refusal(fs, FakeFs.Root)); }

    [Fact]
    public void RefusesReparsePointFileOrParent()
    {
        var fs = new FakeFs(); var p = fs.AddRun(); fs.Reparse.Add(Path.GetFullPath(p));
        Assert.Contains("reparse", Refusal(fs, p));
        var fs2 = new FakeFs(); var p2 = fs2.AddRun(); fs2.Reparse.Add(Path.GetDirectoryName(Path.GetFullPath(p2))!);
        Assert.Contains("reparse", Refusal(fs2, p2));
        Assert.Empty(fs.Deleted); Assert.Empty(fs2.Deleted);
    }

    [Fact] public void RefusesMissingFile() { var fs = new FakeFs(); var p = fs.AddRun(); fs.Files.Remove(Path.GetFullPath(p)); Assert.Contains("regular file", Refusal(fs, p)); }
    [Fact] public void RefusesMissingBundle() { var fs = new FakeFs(); var p = fs.AddRun(bundle: false); Assert.Contains("missing", Refusal(fs, p)); }
    [Fact] public void RefusesBundleRunIdMismatch() { var fs = new FakeFs(); var p = fs.AddRun(bundleRunId: Guid.NewGuid().ToString()); Assert.Contains("match", Refusal(fs, p)); }

    [Fact]
    public void RefusesCorruptBundle()
    {
        var fs = new FakeFs(); var id = Guid.NewGuid().ToString(); var p = fs.AddRun(id);
        fs.Files[Path.GetFullPath(Path.Combine(FakeFs.Root, id, "bundle.json"))] = "{not json";
        Assert.Contains("unreadable", Refusal(fs, p));
    }

    [Fact]
    public void LockedFileIsReportedNotThrown()
    {
        var fs = new FakeFs(); var a = fs.AddRun(bytes: 10); var b = fs.AddRun(bytes: 20);
        fs.DeleteFails.Add(Path.GetFullPath(a));
        var r = Cl(fs).Clean(new[] { a, b });
        Assert.Equal(20, r.FreedBytes);
        Assert.Contains(r.Items, i => !i.Deleted && i.Reason!.Contains("delete failed"));
        Assert.Contains(r.Items, i => i.Deleted);
    }

    [Fact]
    public void RealTempDirDeletesOnlyEtl()
    {
        var root = Path.Combine(Path.GetTempPath(), "wtba-clean-" + Guid.NewGuid().ToString("N"));
        var runs = Path.Combine(root, "data", "runs");
        try
        {
            var id = Guid.NewGuid().ToString(); var dir = Path.Combine(runs, id);
            Directory.CreateDirectory(dir);
            var etl = Path.Combine(dir, id + ".etl");
            File.WriteAllBytes(etl, new byte[64]);
            File.WriteAllText(Path.Combine(dir, "bundle.json"), $"{{\"schema\":1,\"run_id\":\"{id}\",\"processes\":[]}}");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
            var r = new EtlCleaner(new RealFileOps(), runs).Clean(new[] { etl });
            Assert.Equal(64, r.FreedBytes);
            Assert.False(File.Exists(etl));
            Assert.True(File.Exists(Path.Combine(dir, "bundle.json")));
            Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
            Assert.True(Directory.Exists(dir));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void RealTempDirSymlinkIsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "wtba-clean-" + Guid.NewGuid().ToString("N"));
        var runs = Path.Combine(root, "data", "runs");
        try
        {
            var id = Guid.NewGuid().ToString(); var dir = Path.Combine(runs, id);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(root, "target.bin"); File.WriteAllBytes(target, new byte[8]);
            var etl = Path.Combine(dir, id + ".etl");
            try { File.CreateSymbolicLink(etl, target); } catch (Exception) { return; } // no privilege: nothing to test
            File.WriteAllText(Path.Combine(dir, "bundle.json"), $"{{\"schema\":1,\"run_id\":\"{id}\",\"processes\":[]}}");
            var r = new EtlCleaner(new RealFileOps(), runs).Clean(new[] { etl });
            Assert.Equal(0, r.FreedBytes);
            Assert.True(File.Exists(target));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}

public class EtlCleanVerbTests
{
    private static (FakeFs fs, string[] etls) Setup()
    {
        var fs = new FakeFs();
        var etls = new[]
        {
            fs.AddRun(started: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            fs.AddRun(started: new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            fs.AddRun(started: new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
            fs.AddRun(started: new DateTime(2020, 1, 4, 0, 0, 0, DateTimeKind.Utc)),
        };
        return (fs, etls);
    }

    private static int Core(FakeFs fs, TextWriter o, TextWriter e, string input = "", bool apply = false, bool yes = false,
        int keep = 2, bool inc = false, double days = 0) =>
        Cli.EtlCleanCore(fs, Path.GetDirectoryName(FakeFs.Root)!, FakeFs.Root, keep, inc, days, apply, yes, o, e, new StringReader(input),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void DryRunByDefaultDeletesNothingAndListsCandidates()
    {
        var (fs, _) = Setup(); var o = new StringWriter();
        Assert.Equal(0, Core(fs, o, new StringWriter()));
        Assert.Empty(fs.Deleted);
        Assert.Contains("would be reclaimed", o.ToString()); Assert.Contains("dry run", o.ToString());
        Assert.Equal(2, o.ToString().Split("DELETE").Length - 1);
    }

    [Fact]
    public void ApplyRequiresTypedYes()
    {
        var (fs, _) = Setup(); var e = new StringWriter();
        Assert.Equal(3, Core(fs, new StringWriter(), e, input: "no\n", apply: true));
        Assert.Empty(fs.Deleted);
        Assert.Equal(0, Core(fs, new StringWriter(), e, input: "yes\n", apply: true));
        Assert.Equal(2, fs.Deleted.Count);
    }

    [Fact]
    public void YesFlagSkipsPrompt()
    {
        var (fs, _) = Setup(); var o = new StringWriter();
        Assert.Equal(0, Core(fs, o, new StringWriter(), apply: true, yes: true));
        Assert.Equal(2, fs.Deleted.Count);
        Assert.Contains("freed", o.ToString());
    }

    [Fact]
    public void OlderThanDaysOnlyConsidersOldRuns()
    {
        var fs = new FakeFs();
        fs.AddRun(started: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        fs.AddRun(started: new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        fs.AddRun(started: new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc));
        Core(fs, new StringWriter(), new StringWriter(), apply: true, yes: true, keep: 1, days: 30);
        Assert.Equal(2, fs.Deleted.Count); // only the two 2020 runs; the recent one is newer than 30 days and newest-1
    }

    [Fact]
    public void RejectedRunsKeptUnlessIncluded()
    {
        var fs = new FakeFs();
        fs.AddRun(started: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), gates: false);
        fs.AddRun(started: new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        fs.AddRun(started: new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        Core(fs, new StringWriter(), new StringWriter(), apply: true, yes: true, keep: 1);
        Assert.Single(fs.Deleted);
        var fs2 = new FakeFs();
        fs2.AddRun(started: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), gates: false);
        fs2.AddRun(started: new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        Core(fs2, new StringWriter(), new StringWriter(), apply: true, yes: true, keep: 1, inc: true);
        Assert.Single(fs2.Deleted);
    }

    [Fact]
    public void InvalidBundleAndMissingFolderAreSafe()
    {
        var fs = new FakeFs(); fs.AddRun(bundle: false); fs.AddRun(bundle: false);
        Core(fs, new StringWriter(), new StringWriter(), apply: true, yes: true, keep: 1);
        Assert.Empty(fs.Deleted);
        var empty = new FakeFs();
        Assert.Equal(0, Core(empty, new StringWriter(), new StringWriter(), apply: true, yes: true));
    }

    [Fact]
    public void BadArgsRejected()
    {
        using var env = new Env();
        Assert.Equal(1, Cli.Run(new[] { "etl-clean", "--keep-last", "abc" }, new StringWriter(), new StringWriter(), env.Guard, env.Svc()));
        Assert.Equal(1, Cli.Run(new[] { "etl-clean", "--older-than-days", "-3" }, new StringWriter(), new StringWriter(), env.Guard, env.Svc()));
    }

    [Fact]
    public void RealTempDirEndToEndViaCli()
    {
        using var env = new Env();
        var runs = Path.Combine(env.DataRoot, "runs");
        var paths = new List<string>();
        for (int day = 1; day <= 3; day++)
        {
            var id = Guid.NewGuid().ToString(); var dir = Path.Combine(runs, id); Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, id + ".etl"), new byte[10]);
            File.WriteAllText(Path.Combine(dir, "bundle.json"),
                $"{{\"schema\":1,\"run_id\":\"{id}\",\"started_utc\":\"2020-01-0{day}T00:00:00Z\",\"quality\":{{\"gates_passed\":true}},\"processes\":[]}}");
            paths.Add(Path.Combine(dir, id + ".etl"));
        }
        var o = new StringWriter();
        Assert.Equal(0, Cli.Run(new[] { "etl-clean", "--keep-last", "1" }, o, new StringWriter(), env.Guard, env.Svc()));
        Assert.All(paths, p => Assert.True(File.Exists(p))); // dry run
        Assert.Equal(0, Cli.Run(new[] { "etl-clean", "--keep-last", "1", "--apply", "--yes" }, o, new StringWriter(), env.Guard, env.Svc()));
        Assert.Equal(1, paths.Count(File.Exists));
        Assert.True(File.Exists(paths[2])); // newest survives
    }
}

public class ExperimentRetentionTests
{
    private sealed class DeleteThrowsFs : IFileOps
    {
        private readonly RealFileOps _r = new();
        public bool FileExists(string p) => _r.FileExists(p);
        public bool DirectoryExists(string p) => _r.DirectoryExists(p);
        public bool IsReparsePoint(string p) => _r.IsReparsePoint(p);
        public long Length(string p) => _r.Length(p);
        public string ReadAllText(string p) => _r.ReadAllText(p);
        public string? RealPath(string d) => _r.RealPath(d);
        public void DeleteFileVerified(string p, string e) => throw new UnauthorizedAccessException("denied");
        public IReadOnlyList<string> SubDirectories(string p) => _r.SubDirectories(p);
    }

    private static int EtlCount(Env env) =>
        Directory.Exists(Path.Combine(env.DataRoot, "runs")) ? Directory.GetFiles(Path.Combine(env.DataRoot, "runs"), "*.etl", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public void KeepLastAppliedAfterSlotsAndRecordedInManifest()
    {
        using var env = new Env { CreateEtl = true };
        var plan = env.Plan(extra: "etl_retention: { mode: keep_last, keep: 1 }"); env.PrimeSaver(plan);
        Assert.Equal(0, env.Runner().Execute(plan, "sha", null, false, default));
        var m = env.Manifest();
        Assert.Equal(1, EtlCount(env));
        Assert.Equal(400, m.EtlFreedBytes);
        Assert.Equal(4, m.EtlDeletedRunIds.Count);
        Assert.Equal("keep_last 1", m.EtlRetention);
        Assert.Empty(Directory.GetFiles(env.ExpDir(), "*.tmp"));
        // every bundle survives
        Assert.Equal(5, Directory.GetFiles(Path.Combine(env.DataRoot, "runs"), "bundle.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void KeepAllDeletesNothing()
    {
        using var env = new Env { CreateEtl = true };
        var plan = env.Plan(extra: "etl_retention: { mode: keep_all }"); env.PrimeSaver(plan);
        env.Runner().Execute(plan, "sha", null, false, default);
        Assert.Equal(5, EtlCount(env)); Assert.Equal(0, env.Manifest().EtlFreedBytes);
    }

    [Fact]
    public void RejectedAttemptEtlIsKeptWhenDeleteAfterExtract()
    {
        using var env = new Env { CreateEtl = true };
        var plan = env.Plan(runs: 1, warm: 0, extra: "etl_retention: { mode: delete_after_extract }"); env.PrimeSaver(plan);
        env.ExtractHook = n => new EtlAggregate(Array.Empty<ProcessStat>(), n == 1 ? 7 : 0);
        Assert.Equal(0, env.Runner().Execute(plan, "sha", null, false, default));
        var m = env.Manifest();
        Assert.Equal(1, EtlCount(env)); // only the rejected attempt's ETL remains
        var rejectedId = m.Slots[0].Attempts.First(a => a.Status == "rejected").RunId;
        Assert.True(File.Exists(Path.Combine(env.DataRoot, "runs", rejectedId, rejectedId + ".etl")));
        Assert.DoesNotContain(rejectedId, m.EtlDeletedRunIds);
    }

    [Fact]
    public void DeleteFailureNeverFailsTheExperiment()
    {
        using var env = new Env { CreateEtl = true };
        var plan = env.Plan(runs: 1, warm: 0, extra: "etl_retention: { mode: delete_after_extract }"); env.PrimeSaver(plan);
        var e = new StringWriter();
        var runner = new ExperimentRunner(env.Svc(), env.Guard, env.DataRoot, new StringWriter(), e, new StringReader("yes\n\n\n")) { Fs = new DeleteThrowsFs() };
        Assert.Equal(0, runner.Execute(plan, "sha", null, false, default));
        var m = env.Manifest();
        Assert.Equal("completed", m.State);
        Assert.Equal(0, m.EtlFreedBytes); Assert.Empty(m.EtlDeletedRunIds);
        Assert.Equal(2, EtlCount(env));
        Assert.Contains("delete failed", e.ToString());
    }
}

public class RetentionYamlTests
{
    private const string Base = "scenario: s\nduration_s: 10\nconditions:\n  A: {label: a, battery_saver: off}\n  B: {label: b, battery_saver: on}\n";

    [Fact] public void DefaultIsKeepLastTwo() => Assert.Equal(new RetentionPolicy(RetentionMode.KeepLast, 2), PlanLoader.Parse(Base).Retention);

    [Theory]
    [InlineData("etl_retention: { mode: keep_last, keep: 5 }", RetentionMode.KeepLast, 5)]
    [InlineData("etl_retention: { mode: keep_last }", RetentionMode.KeepLast, 2)]
    [InlineData("etl_retention: { mode: delete_after_extract }", RetentionMode.DeleteAfterExtract, 0)]
    [InlineData("etl_retention: { mode: keep_all }", RetentionMode.KeepAll, 0)]
    public void ValidModes(string line, RetentionMode mode, int keep)
    {
        var p = PlanLoader.Parse(Base + line + "\n").Retention;
        Assert.Equal(mode, p.Mode); Assert.Equal(keep, p.Keep);
    }

    [Theory]
    [InlineData("etl_retention: { mode: purge }")]
    [InlineData("etl_retention: { mode: keep_last, keep: 0 }")]
    [InlineData("etl_retention: { mode: keep_last, keep: 1001 }")]
    [InlineData("etl_retention: { mode: keep_all, keep: 3 }")]
    [InlineData("etl_retention: { keep: 3 }")]
    [InlineData("etl_retention: { mode: keep_last, bogus: 1 }")]
    [InlineData("etl_retention: { mode: keep_last, keep: many }")]
    public void InvalidRetentionRejected(string line) => Assert.Throws<PlanException>(() => PlanLoader.Parse(Base + line + "\n"));

    [Fact] public void RetentionChangeChangesPlanHash() =>
        Assert.NotEqual(ExperimentRunner.Sha(Base + "etl_retention: { mode: keep_all }\n"), ExperimentRunner.Sha(Base + "etl_retention: { mode: keep_last, keep: 2 }\n"));

    [Theory]
    [InlineData("idle_10m.yaml")]
    [InlineData("edge_video_5m.yaml")]
    [InlineData("rehearsal_idle_30s.yaml")]
    public void ShippedScenariosDeclareKeepLastTwo(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "scenarios"))) d = d.Parent;
        var text = File.ReadAllText(Path.Combine(d!.FullName, "scenarios", file));
        Assert.Contains("etl_retention", text);
        Assert.Equal(new RetentionPolicy(RetentionMode.KeepLast, 2), PlanLoader.Parse(text).Retention);
    }
}
