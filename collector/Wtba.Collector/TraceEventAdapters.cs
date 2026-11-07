using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Wtba.Collector;

// The ONLY file (with EtlExtractor's reader shell) that touches real ETW. Never constructed by tests.

public sealed class TraceEventSessionAdapter : IEtwSession
{
    private readonly string _etl;
    private TraceEventSession? _s;
    public string Name { get; }
    public long MaxFileBytes { get; set; }
    private SizeCapPoller? _poller;

    public TraceEventSessionAdapter(string name, string etl) { Name = name; _etl = etl; }

    public void Start()
    {
        if (!SessionNames.IsEligible(Name)) throw new InvalidOperationException("session name not eligible (S2)");
        _s = new TraceEventSession(Name, _etl) { StopOnDispose = true };
        if (MaxFileBytes > 0) _s.MaximumFileMB = (int)Math.Min(int.MaxValue, Math.Max(1, MaxFileBytes / (1024 * 1024))); // session-level cap
        // Dispatcher = ReadyThread events.
        _s.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Process |
            KernelTraceEventParser.Keywords.Profile |
            KernelTraceEventParser.Keywords.ContextSwitch |
            KernelTraceEventParser.Keywords.Dispatcher);
        // Belt and braces: an independent poller stops the session if the file passes the cap, even if the recorder loop dies.
        if (MaxFileBytes > 0) _poller = new SizeCapPoller(FileSizeBytes, MaxFileBytes, () => { try { _s?.Stop(); } catch (Exception) { } }, TimeSpan.FromSeconds(1));
    }

    public void Stop() => _s?.Stop();
    public long FileSizeBytes() { try { var f = new FileInfo(_etl); return f.Exists ? f.Length : 0; } catch (IOException) { return 0; } }
    public void Dispose() { _poller?.Dispose(); _poller = null; _s?.Dispose(); _s = null; }
}

public sealed class TraceEventSessionFactory : IEtwSessionFactory
{
    public IEtwSession Create(string sessionName, string etlPath) => new TraceEventSessionAdapter(sessionName, etlPath);
}

public sealed class TraceEventSessionCatalog : IEtwSessionCatalog
{
    public IReadOnlyList<string> ActiveNames() => TraceEventSession.GetActiveSessionNames().ToList();

    public void Stop(string name)
    {
        if (!SessionNames.IsEligible(name)) throw new InvalidOperationException($"refusing to stop non-WTBA session (S2): {name}");
        using var s = new TraceEventSession(name, TraceEventSessionOptions.Attach);
        s.Stop();
    }
}

public static class Elevation
{
    public static bool IsElevated() => OperatingSystem.IsWindows() && (TraceEventSession.IsElevated() ?? false);
}
