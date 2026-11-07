using System.Management;

namespace Wtba.Collector;

public record BatteryReading(double DischargeMw, double RemainingMwh, int Pct);

public interface IBatteryReader
{
    /// <summary>Returns null if the reading is unavailable.</summary>
    BatteryReading? Read();
}

/// <summary>Read-only WMI root\wmi BatteryStatus query plus GetSystemPowerStatus for the percentage.</summary>
public sealed class WmiBatteryReader : IBatteryReader
{
    public BatteryReading? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT DischargeRate, RemainingCapacity FROM BatteryStatus");
        foreach (ManagementBaseObject mo in searcher.Get())
        {
            using (mo)
            {
                var mw = Convert.ToDouble(mo["DischargeRate"]);
                var mwh = Convert.ToDouble(mo["RemainingCapacity"]);
                return new BatteryReading(mw, mwh, SystemStatus.ReadPower().Pct);
            }
        }
        return null;
    }
}

/// <summary>Samples every 5 s on a background task, tracks gaps, fires the S5 abort callback once at 25% or below.</summary>
public sealed class BatterySampler
{
    private readonly IBatteryReader _reader;
    private readonly Action<string> _onAbort;
    private readonly Func<double> _nowS;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _interval;
    private readonly List<BatterySample> _samples = new();
    private readonly object _lock = new();
    private double _gapS;
    private double _lastOkT = double.NaN;
    private bool _aborted;
    private readonly int _abortPct;

    public BatterySampler(IBatteryReader reader, Action<string> onAbort, TimeSpan? interval = null,
        Func<double>? nowS = null, Func<TimeSpan, CancellationToken, Task>? delay = null, int abortPct = SafetyGates.AbortPct)
    {
        _reader = reader; _onAbort = onAbort; _abortPct = Math.Max(abortPct, SafetyGates.AbortPct);
        _interval = interval ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _nowS = nowS ?? (() => sw.Elapsed.TotalSeconds);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public IReadOnlyList<BatterySample> Samples { get { lock (_lock) return _samples.ToList(); } }
    public double GapSeconds { get { lock (_lock) return _gapS; } }
    public int LastPct { get; private set; } = 255;

    /// <summary>Takes the first sample synchronously (so a run always has a t=0 sample), then continues on a background task.</summary>
    public Task Start(CancellationToken ct)
    {
        SampleOnce();
        return Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _delay(_interval, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
                    SampleOnce();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public void SampleOnce()
    {
        BatteryReading? r;
        try { r = _reader.Read(); } catch (Exception) { r = null; }
        if (r is null) return; // gap is accounted for when the next good sample arrives
        var t = _nowS();
        lock (_lock)
        {
            if (!double.IsNaN(_lastOkT))
            {
                var d = t - _lastOkT;
                if (d > _interval.TotalSeconds * 1.5) _gapS += d - _interval.TotalSeconds;
            }
            _lastOkT = t;
            _samples.Add(new BatterySample(Math.Round(t, 3), r.DischargeMw, r.RemainingMwh));
        }
        LastPct = r.Pct;
        if (r.Pct != 255 && !_aborted)
        {
            var g = SafetyGates.CheckAbort(r.Pct, _abortPct);
            if (!g.Passed) { _aborted = true; _onAbort(string.Join("; ", g.Reasons)); }
        }
    }
}

/// <summary>
/// S5 independent of WMI: polls GetSystemPowerStatus (via the supplied reader) and aborts the run at or below the abort
/// threshold, or after 3 consecutive unknown reads (fail safe).
/// </summary>
public sealed class PowerWatchdog
{
    public const int MaxConsecutiveUnknown = 3;
    private readonly Func<(int Pct, bool OnAc, bool BatterySaver)> _read;
    private readonly Action<string> _onAbort;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _interval;
    private readonly int _abortPct;
    private int _unknown;
    private bool _fired;

    public PowerWatchdog(Func<(int Pct, bool OnAc, bool BatterySaver)> read, Action<string> onAbort, int abortPct = SafetyGates.AbortPct,
        TimeSpan? interval = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _read = read; _onAbort = onAbort; _abortPct = Math.Max(abortPct, SafetyGates.AbortPct);
        _interval = interval ?? TimeSpan.FromSeconds(5);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public Task Start(CancellationToken ct)
    {
        PollOnce();
        return Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !_fired)
                {
                    await _delay(_interval, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
                    PollOnce();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public void PollOnce()
    {
        if (_fired) return;
        int pct;
        try { pct = _read().Pct; } catch (Exception) { pct = 255; }
        if (pct < 0 || pct > 100)
        {
            if (++_unknown >= MaxConsecutiveUnknown) Fire($"battery level unknown for {_unknown} consecutive reads; aborting (fail safe)");
            return;
        }
        _unknown = 0;
        var g = SafetyGates.CheckAbort(pct, _abortPct);
        if (!g.Passed) Fire(string.Join("; ", g.Reasons));
    }

    private void Fire(string msg) { _fired = true; _onAbort(msg); }
}
