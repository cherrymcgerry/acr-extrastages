namespace AcrExtraStages;

/// Where things live. Release zip: AcrExtraStages.exe next to config\ and tools\<name>\<name>.exe (scripts/package.ps1);
/// a repo checkout: the exe anywhere below the repo root, tools from the developer builds under work\bin.
internal sealed class Layout
{
    public string ExeDir { get; }
    public string Root { get; }
    public string Work => Path.Combine(Root, "work");
    public string Config => Path.Combine(Root, "config");
    public string StagesToml => Path.Combine(Config, "stages.toml");
    public string IrRoot => Path.Combine(Work, "ir");
    public string AcrWork => Path.Combine(Work, "acr");
    public string Additive => Path.Combine(AcrWork, "additive");
    public string Dist => Path.Combine(Additive, "dist");
    public string Batch => Path.Combine(AcrWork, "batch");
    public string Logs => Path.Combine(Batch, "logs");
    public string State => Path.Combine(Batch, "state");
    public string Specs => Path.Combine(AcrWork, "specs");
    public string MappingsCache => Path.Combine(Work, "mappings");
    public string MenuArt => Path.Combine(Work, "dr2", "menu-art");
    public string MenuArtLoading => Path.Combine(MenuArt, "frontend", "streamed_textures", "loading");
    public string SettingsFile => Path.Combine(Work, "launcher-settings.json");
    public string? ToolsOverride { get; }

    public Layout(string? rootOverride, string? toolsOverride)
    {
        ExeDir = Path.GetFullPath(AppContext.BaseDirectory);
        ToolsOverride = toolsOverride is null ? null : Path.GetFullPath(toolsOverride);
        Root = rootOverride is not null ? Path.GetFullPath(rootOverride) : FindRoot(ExeDir) ?? FindRoot(Environment.CurrentDirectory) ?? ExeDir;
    }

    static string? FindRoot(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "config", "stages.toml"))) return d.FullName;
        return null;
    }

    /// Candidate locations per tool, release layout first.
    public IEnumerable<string> ToolCandidates(string name)
    {
        var exe = name + ".exe";
        if (ToolsOverride is not null)
        {
            yield return Path.Combine(ToolsOverride, name, exe);
            yield return Path.Combine(ToolsOverride, exe);
        }
        foreach (var b in new[] { ExeDir, Root }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(b, "tools", name, exe);
            yield return Path.Combine(b, "tools", exe);
            yield return Path.Combine(b, "bin", name, exe);
            yield return Path.Combine(b, "bin", exe);
        }
        // developer builds (CONTRIBUTING.md); dr2-extract: scripts\build.ps1 output first, as scripts\prepare-stage-data.ps1 uses it
        if (name == "dr2-extract") yield return Path.Combine(Root, "tools", "dr2-extract", "bin", "Release", "net10.0", exe);
        yield return Path.Combine(Work, "bin", name, exe);
        yield return Path.Combine(Work, "bin", "bin", exe);                        // cargo install --root work\bin (retoc)
        if (name == "acr-mappings")
        {
            yield return Path.Combine(Root, "tools", "acr-mappings", "target", "release", exe);
            var ct = Environment.GetEnvironmentVariable("CARGO_TARGET_DIR");
            if (!string.IsNullOrEmpty(ct)) yield return Path.Combine(ct, "release", exe);
        }
    }

    public string? FindTool(string name) => ToolCandidates(name).FirstOrDefault(File.Exists);

    public string Tool(string name) => FindTool(name)
        ?? throw new LauncherException($"{name}.exe not found (looked in tools\\{name}\\ next to the launcher and in work\\bin); " +
                                       "re-extract the release zip, or build it (CONTRIBUTING.md)");

    public static readonly string[] ToolNames = { "acr-ref", "acr-additive", "dr2-extract", "retoc", "acr-mappings" };
}

internal sealed class LauncherException(string message) : Exception(message);
