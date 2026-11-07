using System.Text.Json;

namespace Wtba.Collector;

public static class BundleWriter
{
    public static string Serialize(Bundle b) => JsonSerializer.Serialize(b, BundleJson.Options);

    /// <summary>Writes bundle.json into outDir (created if missing). Caller must have validated outDir with PathGuard.</summary>
    public static string Write(Bundle b, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, "bundle.json");
        File.WriteAllText(path, Serialize(b));
        return path;
    }
}
