using System.Globalization;

namespace Wtba.Collector;

public static partial class Cli
{
    /// <summary>etl-clean: dry-run by default; deletes only with --apply (+ typed yes unless --yes).</summary>
    private static int EtlCleanVerb(Dictionary<string, string> opt, TextWriter o, TextWriter e, CaptureServices svc, TextReader input)
    {
        int keep = 2;
        if (opt.TryGetValue("keep-last", out var kv) && (!int.TryParse(kv, NumberStyles.Integer, CultureInfo.InvariantCulture, out keep) || keep < 0 || keep > 1000))
        { e.WriteLine("--keep-last must be 0..1000"); return 1; }
        double olderDays = 0;
        if (opt.TryGetValue("older-than-days", out var dv) && (!double.TryParse(dv, NumberStyles.Float, CultureInfo.InvariantCulture, out olderDays) || olderDays < 0))
        { e.WriteLine("--older-than-days must be >= 0"); return 1; }
        bool includeRejected = opt.ContainsKey("include-rejected"), apply = opt.ContainsKey("apply"), yes = opt.ContainsKey("yes");

        var dataRoot = svc.DataRootOrDefault(ProjectRoot());
        var runsRoot = Path.Combine(dataRoot, "runs");
        return EtlCleanCore(new RealFileOps(), dataRoot, runsRoot, keep, includeRejected, olderDays, apply, yes, o, e, input, DateTime.UtcNow);
    }

    public static int EtlCleanCore(IFileOps fs, string dataRoot, string runsRoot, int keep, bool includeRejected, double olderDays,
        bool apply, bool yes, TextWriter o, TextWriter e, TextReader input, DateTime nowUtc)
    {
        if (!fs.DirectoryExists(runsRoot)) { o.WriteLine($"no runs folder at {runsRoot}; nothing to do"); return 0; }

        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expRoot = Path.Combine(dataRoot, "experiments");
        if (fs.DirectoryExists(expRoot))
            foreach (var d in fs.SubDirectories(expRoot))
            {
                try
                {
                    var m = ManifestStore.Load(d);
                    foreach (var s in m.Slots)
                        foreach (var a in s.Attempts)
                            if (a.Status != "done" && !string.IsNullOrEmpty(a.RunId)) rejected.Add(a.RunId);
                }
                catch (Exception) { /* unreadable manifest: bundle quality flag still protects failed runs */ }
            }

        var entries = EtlScanner.Scan(fs, runsRoot, rejected);
        var policy = keep == 0 ? new RetentionPolicy(RetentionMode.DeleteAfterExtract, 0) : new RetentionPolicy(RetentionMode.KeepLast, keep);
        var decisions = EtlRetention.Decide(entries, policy, includeRejected).OrderByDescending(d => d.Entry.StartedUtc).ToList();
        var cutoff = nowUtc - TimeSpan.FromDays(olderDays);
        var rows = decisions.Select(d =>
        {
            bool del = d.Delete && (olderDays <= 0 || d.Entry.StartedUtc < cutoff);
            return (d, del, reason: d.Delete && !del ? $"newer than {olderDays:0.#} days" : d.Reason);
        }).ToList();

        o.WriteLine($"{"run id",-36}  {"started (UTC)",-20}  {"MB",8}  action");
        foreach (var (d, del, reason) in rows)
            o.WriteLine($"{d.Entry.RunId,-36}  {(d.Entry.StartedUtc == DateTime.MinValue ? "?" : d.Entry.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),-20}  {d.Entry.EtlBytes / 1048576.0,8:0.0}  {(del ? "DELETE" : "keep")} ({reason})");
        var targets = rows.Where(r => r.del).Select(r => r.d.Entry).ToList();
        long total = targets.Sum(t => t.EtlBytes);
        o.WriteLine($"{targets.Count} ETL file(s), {total / 1048576.0:0.0} MB {(apply ? "to delete" : "would be reclaimed")}");

        if (!apply) { o.WriteLine("dry run: nothing deleted. Re-run with --apply to delete."); return 0; }
        if (targets.Count == 0) return 0;
        if (!yes)
        {
            o.WriteLine("Type 'yes' to delete the files marked DELETE:");
            if (!string.Equals(input.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) { e.WriteLine("not confirmed; nothing deleted"); return 3; }
        }
        var report = new EtlCleaner(fs, runsRoot).Clean(targets.Select(t => t.EtlPath));
        foreach (var i in report.Items)
            o.WriteLine($"{(i.Deleted ? "deleted" : "skipped")}: {i.Path} ({i.Bytes / 1048576.0:0.0} MB){(i.Reason is null ? "" : " " + i.Reason)}");
        o.WriteLine($"freed {report.FreedBytes / 1048576.0:0.0} MB");
        return 0;
    }
}
