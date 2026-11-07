using System.Globalization;

namespace Wtba.Collector;

public static partial class Cli
{
    public const string NotElevatedMessage = "This verb needs an elevated (Administrator) terminal for ETW. Re-run from an elevated shell.";

    /// <summary>Allowed output root: the parent of the "collector" directory (the WTBA project dir), else CWD.</summary>
    public static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.Name.Equals("collector", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null) return dir.Parent.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public static Dictionary<string, string> ParseOptions(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        }
        return d;
    }

    public static int Run(string[] args, TextWriter o, TextWriter e, PathGuard guard,
        CaptureServices? svc = null, TextReader? input = null, CancellationToken ct = default)
    {
        if (args.Length == 0) { e.WriteLine("usage: wtba <simulate|status|run|experiment|extract|cleanup|etl-clean> [options]"); return 1; }
        var verb = args[0].ToLowerInvariant();
        var opt = ParseOptions(args.Skip(1).ToArray());
        switch (verb)
        {
            case "simulate": return Simulate(opt, o, e, guard);
            case "status": return Status(o, svc ?? CaptureServices.Real());
            case "cleanup": return Cleanup(o, e, svc ?? CaptureServices.Real());
            case "extract": return Extract(args.Skip(1).ToArray(), opt, o, e, guard);
            case "experiment": return ExperimentVerb(opt, o, e, guard, svc, input ?? TextReader.Null, ct);
            case "etl-clean": return EtlCleanVerb(opt, o, e, svc ?? CaptureServices.Real(), input ?? TextReader.Null);
            case "run": return RunVerb(opt, o, e, guard, svc ?? CaptureServices.Real(), input ?? TextReader.Null, ct);
            default:
                e.WriteLine($"unknown verb '{verb}'");
                return 1;
        }
    }

    private static int Simulate(Dictionary<string, string> opt, TextWriter o, TextWriter e, PathGuard guard)
    {
        var scenario = opt.GetValueOrDefault("scenario", "idle_10m");
        var cond = opt.GetValueOrDefault("condition", "A").ToUpperInvariant();
        var label = opt.GetValueOrDefault("label", cond == "B" ? "battery-saver-on" : "battery-saver-off");
        if (!int.TryParse(opt.GetValueOrDefault("seed", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)) { e.WriteLine("bad --seed"); return 1; }
        if (cond != "A" && cond != "B") { e.WriteLine("--condition must be A or B"); return 1; }
        var b = Simulator.Generate(scenario, cond, label, seed);
        var outArg = opt.GetValueOrDefault("out", Path.Combine(ProjectRoot(), "data", "runs", b.RunId));
        string outDir;
        try { outDir = guard.Resolve(outArg); }
        catch (UnauthorizedAccessException ex) { e.WriteLine(ex.Message); return 1; }
        o.WriteLine($"wrote {BundleWriter.Write(b, outDir)}");
        return 0;
    }

    private static int Status(TextWriter o, CaptureServices svc)
    {
        var (pct, ac, saver) = svc.ReadPower();
        o.WriteLine($"elevated:      {svc.IsElevated()}");
        o.WriteLine($"free disk GB:  {svc.FreeDiskGb(ProjectRoot()):0.0}");
        o.WriteLine($"battery:       {(pct == 255 ? "unknown" : pct + "%")}");
        o.WriteLine($"power source:  {(ac ? "AC" : "DC (battery)")}");
        o.WriteLine($"battery saver: {(saver ? "ON" : "off")} (SystemStatusFlag={svc.RawFlagFn()}) [EnergySaverState={SystemStatus.ReadEnergySaverState()?.ToString() ?? "n/a"}]");
        try
        {
            var names = svc.Catalog.ActiveNames();
            var wtba = SessionNames.FilterEligible(names);
            o.WriteLine($"ETW sessions:  {names.Count} active, {wtba.Count} WTBA-*{(wtba.Count > 0 ? " (" + string.Join(", ", wtba) + ")" : "")}");
        }
        catch (Exception ex) { o.WriteLine($"ETW sessions:  unavailable ({ex.GetType().Name})"); }
        return 0;
    }

    private static int Cleanup(TextWriter o, TextWriter e, CaptureServices svc)
    {
        if (!svc.IsElevated()) { e.WriteLine(NotElevatedMessage); return 3; }
        var stopped = SessionCleanup.Cleanup(svc.Catalog);
        o.WriteLine(stopped.Count == 0 ? "no WTBA-* sessions active" : "stopped: " + string.Join(", ", stopped));
        return 0;
    }

    private static int Extract(string[] rest, Dictionary<string, string> opt, TextWriter o, TextWriter e, PathGuard guard)
    {
        var etlArg = rest.FirstOrDefault(a => !a.StartsWith("--"));
        if (etlArg is null) { e.WriteLine("usage: wtba extract <etl> [--scenario s --condition A|B --label l --duration-s n --battery-saver on|off]"); return 1; }
        string etl;
        try { etl = guard.Resolve(etlArg); } catch (UnauthorizedAccessException ex) { e.WriteLine(ex.Message); return 1; }
        if (!File.Exists(etl)) { e.WriteLine($"not found: {etl}"); return 1; }
        var scenario = ScenarioName(opt.GetValueOrDefault("scenario", "idle_10m"));
        var cond = opt.GetValueOrDefault("condition", "A").ToUpperInvariant();
        if (cond != "A" && cond != "B") { e.WriteLine("--condition must be A or B"); return 1; }
        var dur = opt.TryGetValue("duration-s", out var ds) && double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
            ? dv : Simulator.ScenarioDuration(scenario);
        var meta = new RunMetadata(Path.GetFileNameWithoutExtension(etl), scenario, cond, opt.GetValueOrDefault("label", ""),
            File.GetCreationTimeUtc(etl), dur, opt.GetValueOrDefault("battery-saver", "off") == "on", 0, 0, false);
        var agg = EtlExtractor.Read(etl);
        var path = BundleWriter.Write(EtlExtractor.BuildBundle(meta, agg, Array.Empty<BatterySample>(), 0), Path.GetDirectoryName(etl)!);
        o.WriteLine($"wrote {path}");
        return 0;
    }

    private static string ScenarioName(string s) =>
        s.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(s) : s;
}
