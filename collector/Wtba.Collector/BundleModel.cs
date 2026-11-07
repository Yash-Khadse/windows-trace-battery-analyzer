using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wtba.Collector;

public record MachineInfo(string IdHash, string OsBuild, string Cpu, int LogicalCpus);
public record PowerInfo(bool OnAc, bool BatterySaver, string PlanGuid, int StartPct, int EndPct);
public record QualityInfo(int DroppedEvents, double SamplerGapsS, bool GatesPassed);
public record ProcessStat(string Name, int Pid, long CpuSamples, double CpuMs, long ContextSwitches, long Wakeups);
public record BatterySample([property: JsonPropertyName("t_s")] double TS, double DischargeMw, double RemainingMwh);

public record Bundle(
    int Schema,
    string RunId,
    string Scenario,
    string Condition,
    string Label,
    string StartedUtc,
    double DurationS,
    MachineInfo Machine,
    PowerInfo Power,
    QualityInfo Quality,
    IReadOnlyList<ProcessStat> Processes,
    IReadOnlyList<BatterySample> BatterySamples);

public static class BundleJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
