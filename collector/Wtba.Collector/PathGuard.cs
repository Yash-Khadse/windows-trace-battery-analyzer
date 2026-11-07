namespace Wtba.Collector;

/// <summary>
/// S1: output paths must resolve under one of the allowed roots, and (for Resolve) no existing component below that
/// root may be a reparse point (symlink / junction / mount point), so an elevated write or delete cannot be redirected.
/// </summary>
public sealed class PathGuard
{
    private readonly string[] _roots;
    private readonly Func<string, bool> _isReparse;

    public PathGuard(IEnumerable<string> allowedRoots, Func<string, bool>? isReparse = null)
    {
        _roots = allowedRoots.Select(r => Normalize(Path.GetFullPath(r))).ToArray();
        if (_roots.Length == 0) throw new ArgumentException("at least one allowed root required");
        _isReparse = isReparse ?? DefaultIsReparse;
    }

    private static string Normalize(string full) =>
        full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>True only if the path exists and carries the reparse-point attribute; an unreadable path counts as reparse (fail closed).</summary>
    public static bool DefaultIsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception) { return true; }
    }

    /// <summary>Text-only containment check (no filesystem access).</summary>
    public bool IsAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Normalize(Path.GetFullPath(path)); } catch (Exception) { return false; }
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return _roots.Any(r => full.StartsWith(r, cmp));
    }

    /// <summary>Returns the first existing component below the matching root that is a reparse point, else null.</summary>
    public string? FindReparsePoint(string path)
    {
        var full = Path.GetFullPath(path);
        var normalized = Normalize(full);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = _roots.Where(r => normalized.StartsWith(r, cmp)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root is null) return null;
        var rel = full.Length > root.Length - 1 ? full[(root.Length - 1)..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "";
        if (rel.Length == 0) return null;
        var cur = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var part in rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, part);
            if (_isReparse(cur)) return cur;
        }
        return null;
    }

    public string Resolve(string path)
    {
        if (!IsAllowed(path)) throw new UnauthorizedAccessException($"Path is outside allowed roots (S1): {path}");
        var full = Path.GetFullPath(path);
        var bad = FindReparsePoint(full);
        if (bad is not null) throw new UnauthorizedAccessException($"Path passes through a reparse point (symlink/junction) (S1): {bad}");
        return full;
    }
}
