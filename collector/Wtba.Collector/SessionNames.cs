namespace Wtba.Collector;

/// <summary>S2: only WTBA-* sessions may be started, stopped or cleaned.</summary>
public static class SessionNames
{
    public const string Prefix = "WTBA-";

    public static bool IsEligible(string? name) =>
        name is not null && name.Length > Prefix.Length && name.StartsWith(Prefix, StringComparison.Ordinal)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    public static IReadOnlyList<string> FilterEligible(IEnumerable<string> names) =>
        names.Where(IsEligible).ToList();

    /// <summary>Parses "logman query -ets" output (first column = session name). Pure; does not run anything.</summary>
    public static IReadOnlyList<string> ParseLogmanQuery(string output)
    {
        var result = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("Data Collector", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("---") || line.StartsWith("The command", StringComparison.OrdinalIgnoreCase)) continue;
            var first = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null) result.Add(first);
        }
        return result;
    }
}
