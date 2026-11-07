using System.Security.Cryptography;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Wtba.Collector;

public record RunMetadata(string RunId, string Scenario, string Condition, string Label, DateTime StartedUtc,
    double DurationS, bool BatterySaver, int StartPct, int EndPct, bool GatesPassed);

public record EtlAggregate(IReadOnlyList<ProcessStat> Processes, int DroppedEvents);

public static class EtlExtractor
{
    /// <summary>Thin ETW reader shell: decodes events and feeds the pure aggregator. Not used by tests.</summary>
    public static EtlAggregate Read(string etlPath)
    {
        var agg = new TraceAggregator();
        int lost;
        using (var src = new ETWTraceEventSource(etlPath))
        {
            var k = new KernelTraceEventParser(src);
            k.PerfInfoCollectionStart += d => agg.SetSampleIntervalMs(d.NewInterval / 10000.0 /* 100ns units */);
            // ProcessStartGroup covers start + rundown. Only ProcessID and ImageFileName are read; never CommandLine.
            k.ProcessStartGroup += d => agg.OnProcess(d.ProcessID, d.ImageFileName);
            k.ThreadStartGroup += d => agg.OnThread(d.ThreadID, d.ProcessID); // Start + DCStart (rundown)
            k.ThreadEndGroup += d => agg.OnThread(d.ThreadID, d.ProcessID);   // End + DCEnd (end rundown)
            k.PerfInfoSample += d => agg.OnCpuSample(d.ProcessID, d.ThreadID);
            k.ThreadCSwitch += d => agg.OnContextSwitch(d.OldProcessID, d.OldThreadID, d.ProcessID, d.ThreadID);
            src.Process();
            lost = src.EventsLost;
        }
        return new EtlAggregate(agg.Result(), lost);
    }

    public static Bundle BuildBundle(RunMetadata m, EtlAggregate a, IReadOnlyList<BatterySample> battery, double samplerGapsS) =>
        new(
            Schema: 1, RunId: m.RunId, Scenario: m.Scenario, Condition: m.Condition, Label: m.Label,
            StartedUtc: m.StartedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            DurationS: m.DurationS,
            Machine: new MachineInfo(MachineHash(), Environment.OSVersion.Version.ToString(),
                Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown", Environment.ProcessorCount),
            Power: new PowerInfo(false, m.BatterySaver, "unknown", m.StartPct, m.EndPct),
            Quality: new QualityInfo(a.DroppedEvents, samplerGapsS, m.GatesPassed),
            Processes: a.Processes, BatterySamples: battery);

    private static string MachineHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wtba-v1:" + Environment.MachineName)))[..16].ToLowerInvariant();
}
