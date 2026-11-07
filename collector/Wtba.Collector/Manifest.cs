using System.Text.Json;

namespace Wtba.Collector;

public sealed class AttemptRecord
{
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "running"; // running|done|rejected|interrupted
    public string? Reason { get; set; }
    public string? BundlePath { get; set; }
    public string? StartedUtc { get; set; }
    public string? EndedUtc { get; set; }
}

public sealed class SlotRecord
{
    public int Index { get; set; }
    public string Condition { get; set; } = "A";
    public bool Warmup { get; set; }
    public string Status { get; set; } = "planned"; // planned|running|done|rejected|skipped|interrupted
    public string? RunId { get; set; }
    public string? BundlePath { get; set; }
    public string? Reason { get; set; }
    public string? StartedUtc { get; set; }
    public string? EndedUtc { get; set; }
    public List<AttemptRecord> Attempts { get; set; } = new();
}

public sealed class Manifest
{
    public int Schema { get; set; } = 1;
    public string ExperimentId { get; set; } = "";
    public string Scenario { get; set; } = "";
    public int Seed { get; set; }
    public string PlanSha256 { get; set; } = "";
    public string CollectorVersion { get; set; } = "";
    public string State { get; set; } = "running"; // running|paused|interrupted|aborted|completed
    public string? PauseReason { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public string EtlRetention { get; set; } = "";
    public long EtlFreedBytes { get; set; }
    public List<string> EtlDeletedRunIds { get; set; } = new();
    public List<SlotRecord> Slots { get; set; } = new();
}

public static class ManifestStore
{
    public static string PathFor(string experimentDir) => Path.Combine(experimentDir, "manifest.json");

    /// <summary>Atomic write: temp file in the same directory, then replace.</summary>
    public static void Save(string experimentDir, Manifest m)
    {
        Directory.CreateDirectory(experimentDir);
        var path = PathFor(experimentDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m, BundleJson.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static Manifest Load(string experimentDir)
    {
        var path = PathFor(experimentDir);
        if (!File.Exists(path)) throw new PlanException($"no manifest at {path}");
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), BundleJson.Options) ?? throw new PlanException("empty manifest"); }
        catch (JsonException ex) { throw new PlanException($"corrupt manifest {path}: {ex.Message}", ex); }
    }
}
