using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wtba.Collector;

public enum RetentionMode { DeleteAfterExtract, KeepLast, KeepAll }

public sealed record RetentionPolicy(RetentionMode Mode, int Keep = 2)
{
    public static readonly RetentionPolicy Default = new(RetentionMode.KeepLast, 2);
    public string Describe() => Mode switch
    {
        RetentionMode.KeepLast => $"keep_last {Keep}",
        RetentionMode.DeleteAfterExtract => "delete_after_extract",
        _ => "keep_all",
    };
}

public sealed record RunEntry(string RunId, string EtlPath, long EtlBytes, bool BundleValid, DateTime StartedUtc, bool Rejected);

public sealed record RetentionDecision(RunEntry Entry, bool Delete, string Reason);

/// <summary>Pure policy, no I/O. Decides which ETLs are eligible for deletion; the cleaner still re-verifies everything.</summary>
public static class EtlRetention
{
    public static IReadOnlyList<RetentionDecision> Decide(IEnumerable<RunEntry> entries, RetentionPolicy policy, bool includeRejected = false)
    {
        var list = entries.ToList();
        var protectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int k = policy.Mode == RetentionMode.KeepLast ? Math.Max(policy.Keep, 1) : 0;
        foreach (var e in list.OrderByDescending(e => e.StartedUtc).ThenBy(e => e.RunId, StringComparer.Ordinal).Take(k))
            protectedIds.Add(e.RunId);

        var result = new List<RetentionDecision>();
        foreach (var e in list)
        {
            if (policy.Mode == RetentionMode.KeepAll) { result.Add(new(e, false, "policy keep_all")); continue; }
            if (!e.BundleValid) { result.Add(new(e, false, "bundle missing or invalid")); continue; }
            if (e.Rejected && !includeRejected) { result.Add(new(e, false, "rejected/interrupted run kept")); continue; }
            if (protectedIds.Contains(e.RunId)) { result.Add(new(e, false, $"among newest {k} kept")); continue; }
            result.Add(new(e, true, policy.Mode == RetentionMode.KeepLast ? $"older than newest {k}" : "delete_after_extract"));
        }
        return result;
    }
}

/// <summary>All filesystem access of the cleaner/scanner, injectable so the safety matrix needs no real deletion.</summary>
public interface IFileOps
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    bool IsReparsePoint(string path);
    long Length(string path);
    string ReadAllText(string path);
    /// <summary>Real (link-resolved) path of an existing directory, or null if it cannot be resolved.</summary>
    string? RealPath(string dirPath);
    /// <summary>Deletes one regular file through an opened handle after verifying its real path equals expectedRealPath; throws otherwise.</summary>
    void DeleteFileVerified(string path, string expectedRealPath);
    IReadOnlyList<string> SubDirectories(string path);
}

public sealed class RealFileOps : IFileOps
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return true; } // unknown => treat as unsafe
    }
    public long Length(string path) => new FileInfo(path).Length;
    public string ReadAllText(string path) => File.ReadAllText(path);
    public string? RealPath(string dirPath) => NativeFs.RealDirectoryPath(dirPath);
    public void DeleteFileVerified(string path, string expectedRealPath) => NativeFs.DeleteVerified(path, expectedRealPath); // the ONLY delete primitive
    public IReadOnlyList<string> SubDirectories(string path) => Directory.GetDirectories(path);
}

public sealed record BundleCheck(bool Valid, string? Reason, DateTime StartedUtc, bool GatesPassed);

public static class BundleValidator
{
    /// <summary>bundle.json exists, parses, schema 1, and run_id matches the run folder name.</summary>
    public static BundleCheck Check(IFileOps fs, string runDir)
    {
        var bad = (string r) => new BundleCheck(false, r, DateTime.MinValue, false);
        var path = Path.Combine(runDir, "bundle.json");
        try
        {
            if (!fs.FileExists(path)) return bad("bundle.json missing");
            using var doc = JsonDocument.Parse(fs.ReadAllText(path));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return bad("bundle.json not an object");
            if (!r.TryGetProperty("schema", out var sc) || sc.ValueKind != JsonValueKind.Number || sc.GetInt32() != 1) return bad("bundle schema != 1");
            if (!r.TryGetProperty("run_id", out var id) || id.ValueKind != JsonValueKind.String) return bad("bundle run_id missing");
            if (!string.Equals(id.GetString(), Path.GetFileName(runDir.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase)) return bad("bundle run_id does not match folder");
            if (!r.TryGetProperty("processes", out var pr) || pr.ValueKind != JsonValueKind.Array) return bad("bundle processes missing");
            DateTime started = DateTime.MinValue;
            if (r.TryGetProperty("started_utc", out var su) && su.ValueKind == JsonValueKind.String)
                DateTime.TryParse(su.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out started);
            bool gates = r.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object
                && q.TryGetProperty("gates_passed", out var gp) && gp.ValueKind == JsonValueKind.True;
            return new BundleCheck(true, null, started, gates);
        }
        catch (Exception ex) { return bad("bundle unreadable: " + ex.GetType().Name); }
    }
}

public sealed record CleanItem(string Path, long Bytes, bool Deleted, string? Reason);

public sealed record CleanReport(IReadOnlyList<CleanItem> Items)
{
    public long FreedBytes => Items.Where(i => i.Deleted).Sum(i => i.Bytes);
    public IEnumerable<string> DeletedPaths => Items.Where(i => i.Deleted).Select(i => i.Path);
}

/// <summary>
/// The ONLY place allowed to delete files. Deletes single "&lt;guid&gt;.etl" files under data/runs/&lt;guid&gt;/ and nothing else.
/// Every component below the project root must be a plain directory (no reparse points), and the delete itself is
/// handle-based and verified against the real path captured when the cleaner was built.
/// </summary>
public sealed class EtlCleaner
{
    public static readonly Regex GuidDir = new("^[0-9a-fA-F-]{36}$", RegexOptions.Compiled);
    private readonly IFileOps _fs;
    private readonly string _runsRoot;
    private readonly PathGuard _guard;
    private readonly string? _realRoot;

    /// <param name="projectRoot">Components from here down (data, data/runs, run folder, file) must not be reparse points.
    /// Defaults to the grandparent of runsRoot (the project root for root/data/runs).</param>
    public EtlCleaner(IFileOps fs, string runsRoot, string? projectRoot = null)
    {
        _fs = fs;
        _runsRoot = Path.GetFullPath(runsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = projectRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(_runsRoot)) ?? _runsRoot;
        _guard = new PathGuard(new[] { root }, fs.IsReparsePoint);
        try { _realRoot = fs.DirectoryExists(_runsRoot) ? fs.RealPath(_runsRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : null; } catch (Exception) { _realRoot = null; }
    }

    /// <summary>Returns null when the file may be deleted, else the refusal reason.</summary>
    public string? Refusal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '*', '?' }) >= 0) return "invalid path";
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return "invalid path"; }
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootNorm = _runsRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootNorm, cmp)) return "outside data/runs";
        var dir = Path.GetDirectoryName(full);
        if (dir is null) return "no parent";
        var folder = Path.GetFileName(dir);
        if (!string.Equals(Path.GetDirectoryName(dir), _runsRoot, cmp)) return "not directly under data/runs/<run>";
        if (!GuidDir.IsMatch(folder)) return "parent folder is not a run GUID";
        if (!string.Equals(Path.GetFileName(full), folder + ".etl", StringComparison.Ordinal)) return "file name is not <guid>.etl";
        if (_realRoot is null) return "cannot resolve real path of data/runs";
        if (!_fs.DirectoryExists(dir)) return "run folder missing";
        var link = _guard.FindReparsePoint(full);
        if (link is not null) return "path component is a reparse point: " + link;
        if (!_fs.FileExists(full)) return "not a regular file";
        var b = BundleValidator.Check(_fs, dir);
        if (!b.Valid) return b.Reason;
        return null;
    }

    public CleanReport Clean(IEnumerable<string> etlPaths)
    {
        var items = new List<CleanItem>();
        foreach (var p in etlPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var why = Refusal(p);
            if (why is not null) { items.Add(new CleanItem(p, 0, false, "skipped: " + why)); continue; }
            var full = Path.GetFullPath(p);
            var folder = Path.GetFileName(Path.GetDirectoryName(full)!);
            var expectedReal = Path.Combine(_realRoot!, folder, folder + ".etl");
            long bytes = 0;
            try { bytes = _fs.Length(full); _fs.DeleteFileVerified(full, expectedReal); items.Add(new CleanItem(full, bytes, true, null)); }
            catch (Exception ex) { items.Add(new CleanItem(full, bytes, false, "skipped: delete failed (" + ex.GetType().Name + ")")); }
        }
        return new CleanReport(items);
    }
}

public static class EtlScanner
{
    /// <summary>Lists runs under runsRoot that still have an ETL. Read-only.</summary>
    public static List<RunEntry> Scan(IFileOps fs, string runsRoot, ISet<string>? rejectedRunIds = null)
    {
        var list = new List<RunEntry>();
        if (!fs.DirectoryExists(runsRoot)) return list;
        foreach (var dir in fs.SubDirectories(runsRoot))
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (!EtlCleaner.GuidDir.IsMatch(name)) continue;
            var etl = Path.Combine(dir, name + ".etl");
            if (!fs.FileExists(etl)) continue;
            var b = BundleValidator.Check(fs, dir);
            long bytes = 0; try { bytes = fs.Length(etl); } catch (Exception) { }
            bool rej = !b.GatesPassed || (rejectedRunIds?.Contains(name) ?? false);
            list.Add(new RunEntry(name, etl, bytes, b.Valid, b.StartedUtc, rej));
        }
        return list;
    }
}
