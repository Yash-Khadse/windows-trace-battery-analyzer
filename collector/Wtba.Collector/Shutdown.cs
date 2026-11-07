using System.Runtime.InteropServices;

namespace Wtba.Collector;

/// <summary>Lets the recorder register "stop my session" callbacks to run on process shutdown signals.</summary>
public interface IShutdownRegistry
{
    IDisposable Register(Action stop);
}

/// <summary>Pure registry core (testable): callbacks run at most once each; failures are swallowed.</summary>
public class ShutdownRegistry : IShutdownRegistry
{
    private readonly object _lock = new();
    private readonly List<Action> _actions = new();

    public virtual IDisposable Register(Action stop)
    {
        lock (_lock) _actions.Add(stop);
        return new Token(this, stop);
    }

    public int Count { get { lock (_lock) return _actions.Count; } }

    /// <summary>Runs and clears every registered callback.</summary>
    public void RunAll()
    {
        Action[] copy;
        lock (_lock) { copy = _actions.ToArray(); _actions.Clear(); }
        foreach (var a in copy) { try { a(); } catch (Exception) { /* best effort */ } }
    }

    private sealed class Token : IDisposable
    {
        private readonly ShutdownRegistry _r; private readonly Action _a;
        public Token(ShutdownRegistry r, Action a) { _r = r; _a = a; }
        public void Dispose() { lock (_r._lock) _r._actions.Remove(_a); }
    }
}

/// <summary>
/// Process-wide registry wired to OS shutdown signals: console close, logoff, shutdown (SetConsoleCtrlHandler, process-local),
/// SIGTERM/SIGHUP and ProcessExit. Installing a handler changes no system setting.
/// </summary>
public sealed class ShutdownGuard : ShutdownRegistry
{
    public static readonly ShutdownGuard Instance = new();
    private delegate bool HandlerRoutine(uint ctrlType);
    private static HandlerRoutine? _handler; // kept alive for the native callback
    private static bool _installed;
    private static readonly List<PosixSignalRegistration> _signals = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    private ShutdownGuard() { }

    public override IDisposable Register(Action stop)
    {
        Install();
        return base.Register(stop);
    }

    private static void Install()
    {
        lock (Instance)
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Instance.RunAll();
            try
            {
                _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { Instance.RunAll(); }));
                _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { Instance.RunAll(); }));
            }
            catch (Exception) { /* not supported on this platform */ }
            if (OperatingSystem.IsWindows())
            {
                _handler = type =>
                {
                    // 2 = CTRL_CLOSE_EVENT, 5 = CTRL_LOGOFF_EVENT, 6 = CTRL_SHUTDOWN_EVENT. Ctrl+C/Break (0/1) are handled by CancelKeyPress.
                    if (type is 2 or 5 or 6) { Instance.RunAll(); return true; }
                    return false;
                };
                try { SetConsoleCtrlHandler(_handler, true); } catch (Exception) { }
            }
        }
    }
}

/// <summary>Independent size poller: stops the session when the ETL exceeds the cap, even if the recorder's own loop is dead.</summary>
public sealed class SizeCapPoller : IDisposable
{
    private readonly Timer _timer;
    private int _fired;

    public SizeCapPoller(Func<long> size, long capBytes, Action stop, TimeSpan interval)
    {
        _timer = new Timer(_ =>
        {
            try
            {
                if (Volatile.Read(ref _fired) == 0 && size() > capBytes && Interlocked.Exchange(ref _fired, 1) == 0) stop();
            }
            catch (Exception)
            {
                // a failing size probe must not leave the session running unbounded: stop it
                if (Interlocked.Exchange(ref _fired, 1) == 0) { try { stop(); } catch (Exception) { } }
            }
        }, null, interval, interval);
    }

    public bool Fired => Volatile.Read(ref _fired) == 1;
    public void Dispose() => _timer.Dispose();
}
