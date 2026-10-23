using System.Diagnostics;

namespace AcrExtraStages;

internal static class Preflight
{
    public static bool AcrRunning() => Process.GetProcessesByName("acr").Length > 0 || Process.GetProcessesByName("acr-Win64-Shipping").Length > 0;

    /// Detect (or ask for) both games and persist them. Returns false when one is still missing.
    public static bool Detect(Ctx c, bool interactive)
    {
        Ui.Head("Game folders");
        c.AcrDir = Pick("Assetto Corsa Rally", c.AcrDir, c.S.AcrDir, Steam.DetectAcr, Steam.IsAcr, interactive,
            @"the folder that contains acr\Content\Paks");
        c.Dr2Dir = Pick("DiRT Rally 2.0", c.Dr2Dir, c.S.Dr2Dir, Steam.DetectDr2, Steam.IsDr2, interactive,
            @"the folder that contains dirtrally2.exe and game\game.dat");
        if (c.AcrDir is not null || c.Dr2Dir is not null) c.SaveSettings();
        if (c.AcrDir is not null) c.P.Env["ACR_GAME_DIR"] = c.AcrDir;
        if (c.Dr2Dir is not null) c.P.Env["DR2_DIR"] = c.Dr2Dir;
        return c.AcrDir is not null && c.Dr2Dir is not null;
    }

    static string? Pick(string game, string? arg, string? saved, Func<string?> detect, Func<string?, bool> valid, bool interactive, string hint)
    {
        string? dir = null, from = null;
        if (arg is not null)
        {
            if (!valid(arg)) Ui.Err($"  {game}: {arg} is not a valid install ({hint})");
            else (dir, from) = (Path.GetFullPath(arg), "command line");
        }
        if (dir is null && valid(saved)) (dir, from) = (saved, "saved setting");
        if (dir is null && detect() is { } d) (dir, from) = (d, "Steam / default location");
        while (dir is null && interactive && !Ui.Yes)
        {
            var a = Ui.Ask($"  {game} not found. Enter {hint} (empty = skip)");
            if (string.IsNullOrWhiteSpace(a)) break;
            if (valid(a)) (dir, from) = (Path.GetFullPath(a), "entered");
            else Ui.Err($"  not a valid {game} install: {a}");
        }
        if (dir is null) Ui.Err($"  {game}: not found (pass --{(game.StartsWith("Assetto") ? "acr" : "dr2")} <dir>)");
        else Ui.Ok($"  {game}: {dir}  ({from})");
        return dir;
    }

    public sealed class Report
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool Ok => Errors.Count == 0;
    }

    /// Disk, games, build ids, ACR not running, tools present, mappings ready (path check only, never starts the game).
    public static Report Run(Ctx c, int stageCount, bool forBuild)
    {
        var r = new Report();
        Ui.Head("Preflight");
        if (c.AcrDir is null) r.Errors.Add("Assetto Corsa Rally install not set");
        if (c.Dr2Dir is null) r.Errors.Add("DiRT Rally 2.0 install not set");
        if (c.AcrDir is not null)
        {
            var b = Steam.BuildId(c.AcrDir, Steam.AcrAppId);
            Ui.Line($"  ACR build: {b ?? "unknown (no Steam appmanifest)"}");
            if (b is null) r.Warnings.Add("ACR Steam build id unknown: acr-mappings needs steamapps\\appmanifest_3917090.acf");
        }
        if (c.Dr2Dir is not null)
            Ui.Line($"  DR2 build: {Steam.BuildId(c.Dr2Dir, Steam.Dr2AppId) ?? "n/a (not a Steam library install)"}");

        if (AcrRunning()) r.Errors.Add("Assetto Corsa Rally is running: close the game first");
        else Ui.Line("  ACR not running: ok");

        // disk: the work folder's drive (extraction caches, scratch, dist) and the ~mods drive (hard links cost nothing there)
        Directory.CreateDirectory(c.L.Work);
        var need = c.Cfg.RequiredGb(stageCount);
        var free = FreeGb(c.L.Work);
        var line = $"  free space on {Path.GetPathRoot(c.L.Work)}: {free:0.0} GB (estimate needed for {stageCount} stage(s): {need:0} GB)";
        if (free < need)
        {
            if (c.IgnoreDisk || !forBuild) { Ui.Warn(line); r.Warnings.Add($"low disk space: {free:0.0} GB free, {need:0} GB estimated"); }
            else r.Errors.Add($"not enough free space on {Path.GetPathRoot(c.L.Work)}: {free:0.0} GB free, about {need:0} GB needed (config\\launcher.toml; --ignore-disk-space to try anyway)");
        }
        else Ui.Line(line);
        if (c.AcrDir is not null && !SameVolume(c.L.Work, c.AcrDir))
            r.Errors.Add($"the work folder ({c.L.Work}) must be on the same drive as ACR ({Path.GetPathRoot(c.AcrDir)}): the build reads the game's " +
                         "containers through hard links. Move the launcher folder to that drive.");
        if (c.AcrDir is not null)
        {
            var mods = c.ModsDir;
            Ui.Line($"  install target: {mods}{(c.DryRunMods is not null ? "  (dry run)" : "")}  -> " +
                    (SameVolume(c.L.Dist, mods) && !c.Copy ? "hard links (no extra space)" : "copies"));
        }

        foreach (var t in Layout.ToolNames)
        {
            var p = c.L.FindTool(t);
            if (p is null)
            {
                if (t == "acr-mappings") r.Warnings.Add("acr-mappings.exe not found: a Dumper-7 usmap (--usmap) is required");
                else if (forBuild) r.Errors.Add($"{t}.exe not found (tools\\{t}\\ next to the launcher, or work\\bin for developers)");
                else r.Warnings.Add($"{t}.exe not found");
            }
            else Ui.Line($"  {t}: {p}");
        }
        foreach (var f in new[] { "stages.toml", "visuals.toml", "sky.toml", "pacenote-map.toml", "object-collision.toml", "scenery.toml", "surface-map.toml" })
            if (!File.Exists(Path.Combine(c.L.Config, f))) r.Errors.Add($"missing config\\{f}");

        if (c.AcrDir is not null)
        {
            var m = Mappings.Check(c);
            if (m.Usmap is not null) Ui.Line($"  mappings: {m.Usmap} ({m.Source})");
            else r.Warnings.Add($"no mappings file yet ({m.Problem}); Install creates it (ACR starts briefly) or asks for a Dumper-7 file");
        }

        foreach (var w in r.Warnings) Ui.Warn("  warning: " + w);
        foreach (var e in r.Errors) Ui.Err("  error: " + e);
        if (r.Ok) Ui.Ok("  preflight ok");
        return r;
    }

    public static double FreeGb(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace / (1024.0 * 1024 * 1024);

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
