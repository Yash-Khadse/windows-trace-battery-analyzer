namespace Wtba.Collector;

/// <summary>Thin seam over one ETW session. The real implementation lives in TraceEventAdapters.cs.</summary>
public interface IEtwSession : IDisposable
{
    string Name { get; }
    void Start();
    void Stop();
    /// <summary>Current size of the ETL file in bytes (0 if not yet created).</summary>
    long FileSizeBytes();
    /// <summary>Session-level size cap in bytes (0 = unset); implementations enforce it independently of the recorder's loop.</summary>
    long MaxFileBytes { get => 0; set { } }
}

public interface IEtwSessionFactory
{
    IEtwSession Create(string sessionName, string etlPath);
}

/// <summary>Lists and stops existing sessions by name (never spawns logman).</summary>
public interface IEtwSessionCatalog
{
    IReadOnlyList<string> ActiveNames();
    void Stop(string name);
}

public enum StopReason { Completed, Cancelled, Watchdog, SizeCap, DiskLow, Aborted }

public sealed record RecorderOptions(
    string RunId,
    TimeSpan ScenarioDuration,
    string OutDir,
    long MaxEtlBytes = 2L * 1024 * 1024 * 1024,
    double MinFreeDiskGb = SafetyGates.MinFreeDiskGb,
    TimeSpan? PollInterval = null,
    TimeSpan? WatchdogSlack = null);

public sealed record RecordResult(string SessionName, string EtlPath, StopReason Reason, string? Detail);

/// <summary>
/// Owns the session lifecycle: S3 (stop in finally, on cancel/Ctrl+C, by watchdog), S1 (PathGuard), S2 (WTBA- names),
/// file-size cap and free-disk check while running.
/// </summary>
public sealed class EtwRecorder
{
    private readonly IEtwSessionFactory _factory;
    private readonly PathGuard _guard;
    private readonly Func<double> _freeDiskGb;
    private int _reason = -1;
    private string? _detail;
    private CancellationTokenSource? _stop;

    private readonly IShutdownRegistry? _shutdown;

    public EtwRecorder(IEtwSessionFactory factory, PathGuard guard, Func<double> freeDiskGb, IShutdownRegistry? shutdown = null)
    {
        _factory = factory; _guard = guard; _freeDiskGb = freeDiskGb; _shutdown = shutdown;
    }

    public static string SessionNameFor(string runId)
    {
        var hex = new string(runId.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (hex.Length < 8) throw new ArgumentException("run id too short", nameof(runId));
        return SessionNames.Prefix + hex[..8];
    }

    /// <summary>Asks the running recording to stop (e.g. battery abort). Safe to call from any thread.</summary>
    public void RequestStop(StopReason reason, string detail)
    {
        if (Interlocked.CompareExchange(ref _reason, (int)reason, -1) == -1) _detail = detail;
        try { _stop?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<RecordResult> RecordAsync(RecorderOptions o, Func<CancellationToken, Task> workload, CancellationToken external)
    {
        var name = SessionNameFor(o.RunId);
        if (!SessionNames.IsEligible(name)) throw new InvalidOperationException($"session name not eligible (S2): {name}");
        var outDir = _guard.Resolve(o.OutDir);
        var etl = _guard.Resolve(Path.Combine(outDir, o.RunId + ".etl"));
        var poll = o.PollInterval ?? TimeSpan.FromSeconds(2);
        var slack = o.WatchdogSlack ?? TimeSpan.FromSeconds(30);
        var watchdog = o.ScenarioDuration + slack;

        var free = _freeDiskGb();
        if (free < o.MinFreeDiskGb) throw new InvalidOperationException($"free disk {free:0.0} GB < {o.MinFreeDiskGb:0.0} GB");
        Directory.CreateDirectory(outDir);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(external);
        _stop = stop;
        if (Volatile.Read(ref _reason) != -1) stop.Cancel(); // abort requested before recording began
        IEtwSession? session = null;
        Task? guardTask = null;
        IDisposable? hook = null;
        int stopped = 0;
        void StopOnce()
        {
            if (session is null || Interlocked.Exchange(ref stopped, 1) == 1) return;
            try { session.Stop(); } catch (Exception) { /* best effort; Dispose follows */ }
        }
        try
        {
            session = _factory.Create(name, etl);
            session.MaxFileBytes = o.MaxEtlBytes;
            hook = _shutdown?.Register(StopOnce); // console close / logoff / shutdown / SIGTERM / ProcessExit
            session.Start();
            guardTask = Task.Run(() => GuardLoopAsync(session, o, watchdog, poll, stop.Token));
            try { await workload(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            if (external.IsCancellationRequested) RequestStop(StopReason.Cancelled, "cancelled");
        }
        finally
        {
            if (!stop.IsCancellationRequested) stop.Cancel();
            hook?.Dispose();
            if (session is not null)
            {
                StopOnce();
                try { session.Dispose(); } catch (Exception) { }
            }
            if (guardTask is not null) { try { await guardTask.ConfigureAwait(false); } catch (Exception) { } }
        }
        var reason = _reason == -1 ? StopReason.Completed : (StopReason)_reason;
        return new RecordResult(name, etl, reason, _detail);
    }

    private async Task GuardLoopAsync(IEtwSession s, RecorderOptions o, TimeSpan watchdog, TimeSpan poll, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var left = watchdog - sw.Elapsed;
                if (left <= TimeSpan.Zero) { RequestStop(StopReason.Watchdog, $"max duration {watchdog.TotalSeconds:0}s exceeded"); return; }
                await Task.Delay(left < poll ? left : poll, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                if (sw.Elapsed >= watchdog) { RequestStop(StopReason.Watchdog, $"max duration {watchdog.TotalSeconds:0}s exceeded"); return; }
                if (s.FileSizeBytes() > o.MaxEtlBytes) { RequestStop(StopReason.SizeCap, $"ETL exceeded {o.MaxEtlBytes} bytes"); return; }
                if (_freeDiskGb() < o.MinFreeDiskGb) { RequestStop(StopReason.DiskLow, "free disk below minimum"); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The guard must never die silently with the session still running: stop it on ANY failure.
            RequestStop(StopReason.Watchdog, "guard loop failed: " + ex.GetType().Name);
            try { s.Stop(); } catch (Exception) { }
        }
    }
}

/// <summary>S2: stops only WTBA-* sessions.</summary>
public static class SessionCleanup
{
    public static IReadOnlyList<string> Cleanup(IEtwSessionCatalog catalog)
    {
        var stopped = new List<string>();
        foreach (var n in SessionNames.FilterEligible(catalog.ActiveNames()))
        {
            catalog.Stop(n);
            stopped.Add(n);
        }
        return stopped;
    }
}
