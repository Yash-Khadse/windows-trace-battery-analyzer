using System.Globalization;

namespace Wtba.Collector;

public static class Simulator
{
    public static double ScenarioDuration(string scenario) => scenario switch
    {
        "idle_10m" => 600.0,
        "edge_video_5m" => 300.0,
        _ => 60.0,
    };

    public static Bundle Generate(string scenario, string condition, string label, int seed)
    {
        if (condition != "A" && condition != "B") throw new ArgumentException("condition must be A or B", nameof(condition));
        if (string.IsNullOrWhiteSpace(scenario)) throw new ArgumentException("scenario required", nameof(scenario));
        var rng = new Random(seed);
        bool b = condition == "B";
        double cpuF = b ? 0.80 : 1.0;
        double dischargeF = b ? 0.82 : 1.0;
        double duration = ScenarioDuration(scenario);
        bool video = scenario.StartsWith("edge", StringComparison.Ordinal);

        var idBytes = new byte[16];
        rng.NextBytes(idBytes);
        var runId = new Guid(idBytes).ToString();
        var started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes((seed & 0xFFFF) * 15L);

        var names = new[] { "msedge.exe", "System", "svchost.exe", "explorer.exe", "dwm.exe", "MsMpEng.exe", "SearchHost.exe" };
        var baseCpu = video ? new[] { 9000, 900, 700, 500, 1200, 600, 150 } : new[] { 1500, 600, 800, 300, 400, 500, 100 };
        var procs = new List<ProcessStat>();
        for (int i = 0; i < names.Length; i++)
        {
            long samples = (long)(baseCpu[i] * cpuF * (0.9 + 0.2 * rng.NextDouble()));
            long cs = samples * (6 + rng.Next(0, 3));
            long wk = (long)(cs * (0.2 + 0.05 * rng.NextDouble()) * (b ? 0.85 : 1.0));
            // sample interval 1 ms => cpu_ms = cpu_samples * 1.0
            procs.Add(new ProcessStat(names[i], 1000 + i * 4 + rng.Next(0, 4), samples, samples * 1.0, cs, wk));
        }

        double baseMw = (video ? 9500 : 5500) * dischargeF;
        const double startMwh = 41000;
        double remaining = startMwh;
        var battery = new List<BatterySample>();
        for (double t = 0; t <= duration; t += 5.0)
        {
            double mw = baseMw * (0.95 + 0.1 * rng.NextDouble());
            battery.Add(new BatterySample(t, Math.Round(mw, 1), Math.Round(remaining, 1)));
            remaining -= mw * 5.0 / 3600.0;
        }
        int startPct = 87;
        int endPct = Math.Max(0, startPct - (int)Math.Round((startMwh - remaining) / startMwh * 100));

        return new Bundle(
            Schema: 1,
            RunId: runId,
            Scenario: scenario,
            Condition: condition,
            Label: label ?? "",
            StartedUtc: started.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            DurationS: duration,
            Machine: new MachineInfo("simulated0hash0000", "10.0.26200", "Simulated CPU", 16),
            Power: new PowerInfo(false, b, "00000000-0000-0000-0000-000000000000", startPct, endPct),
            Quality: new QualityInfo(0, 0.0, true),
            Processes: procs,
            BatterySamples: battery);
    }
}
