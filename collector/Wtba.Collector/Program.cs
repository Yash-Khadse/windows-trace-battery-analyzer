using Wtba.Collector;

var guard = new PathGuard(new[] { Cli.ProjectRoot() });
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; // S3: Ctrl+C stops the session via the recorder
return Cli.Run(args, Console.Out, Console.Error, guard, null, Console.In, cts.Token);
