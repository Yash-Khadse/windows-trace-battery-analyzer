namespace Wtba.Collector;

public record SystemSnapshot(bool Elevated, double FreeDiskGb, int BatteryPct, bool OnAc, bool BatterySaver, bool DeclaredBatterySaver);

public record GateResult(bool Passed, IReadOnlyList<string> Reasons);

public static class SafetyGates
{
    public const double MinFreeDiskGb = 5.0;
    public const int DefaultStartPct = 80;
    public const int AbortPct = 25;

    /// <summary>S4: start gates. batteryScenario=true requires battery power and the start charge threshold.</summary>
    public static GateResult CheckStart(SystemSnapshot s, bool batteryScenario = true, int startThresholdPct = DefaultStartPct)
    {
        var r = new List<string>();
        if (!s.Elevated) r.Add("not elevated");
        if (s.FreeDiskGb < MinFreeDiskGb) r.Add($"free disk {s.FreeDiskGb:0.0} GB < {MinFreeDiskGb:0.0} GB");
        // S4 fail closed: an unknown or out-of-range battery level (255, failed read, no battery) never passes.
        if (s.BatteryPct < 0 || s.BatteryPct > 100) r.Add($"battery level unknown ({s.BatteryPct}); power state must be readable");
        if (batteryScenario)
        {
            if (s.OnAc) r.Add("on AC power; battery scenario requires DC");
            if (s.BatteryPct < startThresholdPct) r.Add($"battery {s.BatteryPct}% < start threshold {startThresholdPct}%");
        }
        if (s.BatterySaver != s.DeclaredBatterySaver)
            r.Add($"battery saver is {(s.BatterySaver ? "on" : "off")} but condition declares {(s.DeclaredBatterySaver ? "on" : "off")}");
        return new GateResult(r.Count == 0, r);
    }

    /// <summary>S5: abort a running capture when battery is at or below 25%.</summary>
    public static GateResult CheckAbort(int batteryPct, int abortPct = AbortPct) =>
        (batteryPct < 0 || batteryPct > 100)
            ? new GateResult(false, new[] { $"battery level unknown ({batteryPct})" })
            : batteryPct <= abortPct
            ? new GateResult(false, new[] { $"battery {batteryPct}% <= abort threshold {abortPct}%" })
            : new GateResult(true, Array.Empty<string>());
}
