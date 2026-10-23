using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcrExtraStages;

/// work\launcher-settings.json: game folders, usmap fallback and the stage selection.
internal sealed class Settings
{
    public string? AcrDir { get; set; }
    public string? Dr2Dir { get; set; }
    public string? Usmap { get; set; }               // user-supplied Dumper-7 file (fallback when acr-mappings has none)
    public List<string>? Selected { get; set; }      // acr ids; null = every enabled stage in config/stages.toml
    public List<string>? Installed { get; set; }     // ids of the last install (registry-ids.json at that time)
    public string? InstalledTo { get; set; }
    public string? InstalledAt { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static Settings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}
