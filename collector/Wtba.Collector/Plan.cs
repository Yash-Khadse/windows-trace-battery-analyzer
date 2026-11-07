using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Wtba.Collector;

public sealed class PlanException : Exception
{
    public PlanException(string message, Exception? inner = null) : base(message, inner) { }
}

// Raw YAML shape (nullable so missing required keys can be reported clearly).
public sealed class RawCondition
{
    public string? Label { get; set; }
    public string? BatterySaver { get; set; }
}

public sealed class RawRetention
{
    public string? Mode { get; set; }
    public int? Keep { get; set; }
}

public sealed class RawPlan
{
    public string? Scenario { get; set; }
    public double? DurationS { get; set; }
    public Dictionary<string, RawCondition>? Conditions { get; set; }
    public int? RunsPerCondition { get; set; }
    public int? WarmupRuns { get; set; }
    public int? Seed { get; set; }
    public double? CooldownS { get; set; }
    public int? MinStartPct { get; set; }
    public int? AbortPct { get; set; }
    public double? SettleS { get; set; }
    public int? MaxRetriesPerSlot { get; set; }
    public bool? ManualWorkload { get; set; }
    public RawRetention? EtlRetention { get; set; }
}

public sealed record ConditionPlan(string Label, bool BatterySaver);

public sealed record ScenarioPlan(
    string Scenario, double DurationS, ConditionPlan A, ConditionPlan B,
    int RunsPerCondition, int WarmupRuns, int Seed, double CooldownS,
    int MinStartPct, int AbortPct, double SettleS, int MaxRetriesPerSlot, bool ManualWorkload, RetentionPolicy Retention)
{
    public ConditionPlan For(string cond) => cond == "A" ? A : B;
    public int TotalRuns => RunsPerCondition * 2 + WarmupRuns;
}

public static class PlanLoader
{
    private static readonly Regex NameRe = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static ScenarioPlan Parse(string yaml)
    {
        RawPlan raw;
        try
        {
            // Strict: unknown keys and wrong types throw.
            var d = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
            raw = d.Deserialize<RawPlan?>(yaml) ?? throw new PlanException("plan file is empty");
        }
        catch (YamlException ex)
        {
            throw new PlanException($"invalid plan YAML at line {ex.Start.Line}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        var scenario = raw.Scenario ?? throw new PlanException("missing required key: scenario");
        if (!NameRe.IsMatch(scenario)) throw new PlanException($"scenario '{scenario}' must match [A-Za-z0-9_-]{{1,64}}");
        var dur = raw.DurationS ?? throw new PlanException("missing required key: duration_s");
        if (!(dur > 0 && dur <= Cli.MaxRunDurationS)) throw new PlanException($"duration_s must be > 0 and <= {Cli.MaxRunDurationS:0}");
        if (raw.Conditions is null || !raw.Conditions.TryGetValue("A", out var ca) || !raw.Conditions.TryGetValue("B", out var cb) || ca is null || cb is null)
            throw new PlanException("conditions must define both A and B");
        if (raw.Conditions.Count != 2) throw new PlanException("conditions may only contain A and B");
        var a = Cond("A", ca);
        var b = Cond("B", cb);
        if (a.BatterySaver == b.BatterySaver) throw new PlanException("conditions A and B must differ in battery_saver");
        if (a.Label == b.Label) throw new PlanException("conditions A and B must have different labels");

        var rpc = raw.RunsPerCondition ?? 10;
        if (rpc < 1 || rpc > 100) throw new PlanException("runs_per_condition must be 1..100");
        var warm = raw.WarmupRuns ?? 1;
        if (warm < 0 || warm > 5) throw new PlanException("warmup_runs must be 0..5");
        var cool = raw.CooldownS ?? 60;
        if (cool < 0 || cool > 3600) throw new PlanException("cooldown_s must be 0..3600");
        var settle = raw.SettleS ?? 0;
        if (settle < 0 || settle > 600) throw new PlanException("settle_s must be 0..600");
        var abort = raw.AbortPct ?? SafetyGates.AbortPct;
        if (abort < SafetyGates.AbortPct || abort > 90) throw new PlanException($"abort_pct must be {SafetyGates.AbortPct}..90 (cannot be below the safety floor)");
        var start = raw.MinStartPct ?? SafetyGates.DefaultStartPct;
        if (start <= abort || start > 100) throw new PlanException("min_start_pct must be > abort_pct and <= 100");
        var retries = raw.MaxRetriesPerSlot ?? 2;
        if (retries < 0 || retries > 5) throw new PlanException("max_retries_per_slot must be 0..5");

        return new ScenarioPlan(scenario, dur, a, b, rpc, warm, raw.Seed ?? 1, cool, start, abort, settle, retries, raw.ManualWorkload ?? false, Retention(raw.EtlRetention));
    }

    private static RetentionPolicy Retention(RawRetention? r)
    {
        if (r is null) return RetentionPolicy.Default;
        switch (r.Mode)
        {
            case "keep_last":
                var k = r.Keep ?? 2;
                if (k < 1 || k > 1000) throw new PlanException("etl_retention.keep must be 1..1000");
                return new RetentionPolicy(RetentionMode.KeepLast, k);
            case "delete_after_extract":
            case "keep_all":
                if (r.Keep is not null) throw new PlanException($"etl_retention.keep is only valid with mode keep_last");
                return new RetentionPolicy(r.Mode == "keep_all" ? RetentionMode.KeepAll : RetentionMode.DeleteAfterExtract, 0);
            case null: throw new PlanException("etl_retention.mode is required (keep_last|delete_after_extract|keep_all)");
            default: throw new PlanException($"etl_retention.mode '{r.Mode}' is not one of keep_last, delete_after_extract, keep_all");
        }
    }

    private static ConditionPlan Cond(string key, RawCondition c)
    {
        if (string.IsNullOrWhiteSpace(c.Label)) throw new PlanException($"conditions.{key}.label is required");
        return c.BatterySaver switch
        {
            "on" => new ConditionPlan(c.Label, true),
            "off" => new ConditionPlan(c.Label, false),
            null => throw new PlanException($"conditions.{key}.battery_saver is required (on|off)"),
            var other => throw new PlanException($"conditions.{key}.battery_saver must be 'on' or 'off', got '{other}'"),
        };
    }

    public static ScenarioPlan Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (IOException ex) { throw new PlanException($"cannot read plan {path}: {ex.Message}", ex); }
    }
}

public sealed record PlannedRun(int Index, string Condition, bool Warmup);

public static class RunOrder
{
    /// <summary>
    /// Pure and seeded. Warm-up runs first (alternating conditions, random start). Then runsPerCondition blocks, each a
    /// random permutation of one A and one B; a block is flipped if it would create three equal conditions in a row.
    /// </summary>
    public static IReadOnlyList<PlannedRun> Build(int runsPerCondition, int warmupRuns, int seed)
    {
        var rng = new Random(seed);
        var conds = new List<(string C, bool W)>();
        var firstWarm = rng.Next(2) == 0 ? "A" : "B";
        for (int i = 0; i < warmupRuns; i++)
            conds.Add(((i % 2 == 0) == (firstWarm == "A") ? "A" : "B", true));
        for (int i = 0; i < runsPerCondition; i++)
        {
            var ab = rng.Next(2) == 0;
            var first = ab ? "A" : "B"; var second = ab ? "B" : "A";
            if (conds.Count >= 2 && conds[^1].C == conds[^2].C && conds[^1].C == first) (first, second) = (second, first);
            conds.Add((first, false)); conds.Add((second, false));
        }
        return conds.Select((x, i) => new PlannedRun(i, x.C, x.W)).ToList();
    }
}
