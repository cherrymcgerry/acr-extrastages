using System.Globalization;
using System.Text.RegularExpressions;

namespace AcrExtraStages;

/// config/stages.toml, read like scripts/stages-lib.ps1 (Read-StagesToml): [locations.x] tables and [[stages]] entries of
/// key = value lines; "strings", true/false and plain numbers.
internal sealed class StagesConfig
{
    public Dictionary<string, Dictionary<string, object>> Locations { get; } = new();
    public List<Dictionary<string, object>> Stages { get; } = new();

    public static StagesConfig Load(string path)
    {
        if (!File.Exists(path)) throw new LauncherException($"missing {path}");
        var c = new StagesConfig();
        Dictionary<string, object>? cur = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var loc = Regex.Match(line, @"^\[locations\.([^\]]+)\]$");
            if (loc.Success) { cur = c.Locations[loc.Groups[1].Value] = new(); continue; }
            if (line == "[[stages]]") { cur = new(); c.Stages.Add(cur); continue; }
            var kv = Regex.Match(line, @"^([A-Za-z0-9_]+)\s*=\s*(.+)$");
            if (kv.Success && cur is not null) cur[kv.Groups[1].Value] = Value(kv.Groups[2].Value.Trim());
        }
        return c;
    }

    internal static object Value(string v)
    {
        var s = Regex.Match(v, "^\"(.*)\"$");
        if (s.Success) return s.Groups[1].Value;
        if (v == "true") return true;
        if (v == "false") return false;
        if (Regex.IsMatch(v, @"^-?\d+(\.\d+)?$")) return double.Parse(v, CultureInfo.InvariantCulture);
        return v;
    }

    /// AcrXs<LocCode><Rally><Route>, e.g. new_zealand_rally_01_route_5 + Nz -> AcrXsNz01r5 (Get-AcrId).
    public static string AcrId(string dr2, string code)
    {
        var m = Regex.Match(dr2, @"^([a-z_]+)_rally_(\d+)_route_(\d+)$");
        if (!m.Success) throw new LauncherException($"bad dr2 id: {dr2}");
        return $"AcrXs{code}{int.Parse(m.Groups[2].Value):D2}r{int.Parse(m.Groups[3].Value)}";
    }

    /// Every [[stages]] entry with its acr id; disabled ones ("later") are listed but never built.
    public List<StageEntry> Entries()
    {
        var list = new List<StageEntry>();
        foreach (var st in Stages)
        {
            var dr2 = Str(st, "dr2") ?? throw new LauncherException("[[stages]] entry without dr2");
            var locKey = Str(st, "location") ?? throw new LauncherException($"{dr2}: no location");
            if (!Locations.TryGetValue(locKey, out var loc)) throw new LauncherException($"unknown location {locKey} for {dr2}");
            list.Add(new StageEntry(AcrId(dr2, Str(loc, "code") ?? ""), dr2, locKey, Str(loc, "name") ?? locKey, Bool(st, "enabled"), Bool(st, "default"), st));
        }
        return list;
    }

    public static string? Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    public static bool Bool(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is true;
    public static double? Num(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is double x ? x : null;
}

internal sealed record StageEntry(string Id, string Dr2, string LocationKey, string LocationName, bool Enabled, bool Default, Dictionary<string, object> Raw);

/// config/launcher.toml: disk estimates and other launcher defaults (flat key = value lines).
internal sealed class LauncherConfig
{
    public double FreeGbBase = 10;       // vanilla extraction caches, registry, per-stage scratch
    public double FreeGbPerStage = 1.5;  // container + prepared DR2 data per stage
    public double FreeGbFullSet = 35;    // cap: the whole curated set

    public static LauncherConfig Load(string path)
    {
        var c = new LauncherConfig();
        if (!File.Exists(path)) return c;
        foreach (var raw in File.ReadLines(path))
        {
            var kv = Regex.Match(raw.Trim(), @"^([A-Za-z0-9_]+)\s*=\s*(.+)$");
            if (!kv.Success || StagesConfig.Value(kv.Groups[2].Value.Trim()) is not double d) continue;
            switch (kv.Groups[1].Value)
            {
                case "free_gb_base": c.FreeGbBase = d; break;
                case "free_gb_per_stage": c.FreeGbPerStage = d; break;
                case "free_gb_full_set": c.FreeGbFullSet = d; break;
            }
        }
        return c;
    }

    /// Conservative free space needed for building n stages (until the disk todo measures the real peak).
    public double RequiredGb(int stages) => Math.Min(FreeGbFullSet, FreeGbBase + FreeGbPerStage * stages);
}
