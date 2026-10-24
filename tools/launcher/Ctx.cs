namespace AcrExtraStages;

/// Command-line options and the state shared by every step.
internal sealed class Ctx
{
    public required Layout L { get; init; }
    public required Proc P { get; init; }
    public required Settings S { get; init; }
    public LauncherConfig Cfg { get; init; } = new();
    public TextWriter? Log { get; set; }            // work\acr\batch\logs\launcher.log

    public string? AcrDir { get; set; }
    public string? Dr2Dir { get; set; }
    public string? UsmapArg { get; init; }
    public List<string>? Only { get; init; }
    public string? DryRunMods { get; init; }
    public bool Force { get; init; }
    public bool NoVerify { get; init; }
    public bool KeepTemp { get; init; }
    public bool IgnoreDisk { get; init; }
    public bool Copy { get; init; }
    public double? MaxLengthKm { get; init; }
    public int MaxPrepareAttempts { get; init; } = 2;

    public string ModsDir => DryRunMods is not null ? Path.GetFullPath(DryRunMods) : Path.Combine(AcrDir!, "acr", "Content", "Paks", "~mods");

    public void SaveSettings()
    {
        S.AcrDir = AcrDir;
        S.Dr2Dir = Dr2Dir;
        S.Save(L.SettingsFile);
    }

    /// The stages a build covers: --only, else the saved selection, else every enabled stage. Disabled ("later") entries never.
    public List<StageEntry> Selection(StagesConfig cfg)
    {
        var enabled = cfg.Entries().Where(e => e.Enabled).ToList();
        if (Only is { Count: > 0 })
        {
            var pick = enabled.Where(e => Only.Contains(e.Id, StringComparer.OrdinalIgnoreCase) || Only.Contains(e.Dr2, StringComparer.OrdinalIgnoreCase)).ToList();
            var unknown = Only.Where(o => !enabled.Any(e => e.Id.Equals(o, StringComparison.OrdinalIgnoreCase) || e.Dr2.Equals(o, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0) throw new LauncherException($"not an enabled stage in config\\stages.toml: {string.Join(", ", unknown)}");
            return pick;
        }
        if (S.Selected is not null) return enabled.Where(e => S.Selected.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var defaults = enabled.Where(e => e.Default).ToList();
        return defaults.Count > 0 ? defaults : enabled;
    }
}
