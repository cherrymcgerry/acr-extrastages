namespace AcrExtraStages;

/// The ACR .usmap, as tools/acr-mappings/README.md ("Launcher integration") describes: cached path, else dump + validate,
/// else a user-supplied Dumper-7 file (--usmap, ACR_USMAP, the saved setting, or a prompt).
internal static class Mappings
{
    public sealed record Outcome(string? Usmap, string Source, string? Problem);

    /// path only: never starts the game (preflight / "mappings check").
    public static Outcome Check(Ctx c)
    {
        var tool = c.L.FindTool("acr-mappings");
        if (tool is not null)
        {
            var r = c.P.Run(tool, new[] { "path", "--game", c.AcrDir!, "--cache-dir", c.L.MappingsCache }, c.Log);
            var path = r.Lines.LastOrDefault(x => x.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase))?.Trim();
            if (r.Code == 0 && path is not null && File.Exists(path)) return new(path, "acr-mappings cache (validated)", null);
            var why = r.Lines.LastOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? $"exit {r.Code}";
            var manual = Manual(c);
            return manual is not null ? new(manual, "user-supplied usmap", null) : new(null, "acr-mappings", why);
        }
        var m = Manual(c);
        return m is not null ? new(m, "user-supplied usmap", null) : new(null, "none", "acr-mappings.exe not found and no --usmap / ACR_USMAP file");
    }

    static string? Manual(Ctx c)
    {
        foreach (var p in new[] { c.UsmapArg, Environment.GetEnvironmentVariable("ACR_USMAP"), c.S.Usmap })
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return Path.GetFullPath(p);
        return null;
    }

    /// Full flow for a build: cached -> (confirm) dump + validate -> Dumper-7 fallback. Returns the usmap or throws.
    public static string Ensure(Ctx c)
    {
        if (c.UsmapArg is not null)
        {
            if (!File.Exists(c.UsmapArg)) throw new LauncherException($"--usmap {c.UsmapArg} not found");
            return Path.GetFullPath(c.UsmapArg);
        }
        var tool = c.L.FindTool("acr-mappings");
        if (tool is not null)
        {
            var args = new[] { "--game", c.AcrDir!, "--cache-dir", c.L.MappingsCache };
            var r = c.P.Run(tool, new[] { "path" }.Concat(args), c.Log);
            var path = r.Lines.LastOrDefault(x => x.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase))?.Trim();
            if (r.Code == 0 && path is not null && File.Exists(path))
            {
                Ui.Ok($"  mappings: {path}");
                return path;
            }
            Ui.Line("  no validated mappings file for this ACR build yet.");
            if (Preflight.AcrRunning())
                Ui.Warn("  ACR is running: acr-mappings will attach to it and leave it running.");
            if (Ui.Confirm("  ACR will start briefly to read its data layout (read-only, ~1 min). Continue?"))
            {
                var d = c.P.Run(tool, new[] { "dump" }.Concat(args), c.Log, "reading ACR's data layout");
                if (d.Code == 0)
                {
                    var v = c.P.Run(tool, new[] { "validate" }.Concat(args).Concat(new[] { "--tool", c.L.Tool("acr-additive") }), c.Log, "validating the mappings");
                    if (v.Code == 0)
                    {
                        r = c.P.Run(tool, new[] { "path" }.Concat(args), c.Log);
                        path = r.Lines.LastOrDefault(x => x.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase))?.Trim();
                        if (r.Code == 0 && path is not null && File.Exists(path))
                        {
                            Ui.Ok($"  mappings: {path}");
                            return path;
                        }
                    }
                    else Ui.Err("  validation failed: " + Last(v));
                }
                else Ui.Err("  dump failed: " + Last(d));
            }
        }
        else Ui.Warn("  acr-mappings.exe not found; a Dumper-7 usmap is needed.");

        var manual = Manual(c);
        if (manual is not null)
        {
            Ui.Line($"  using {manual}");
            return manual;
        }
        var ask = Ui.Ask("  Path to a Dumper-7 .usmap for this ACR build (empty = cancel)");
        if (string.IsNullOrWhiteSpace(ask) || !File.Exists(ask)) throw new LauncherException("no mappings file: cannot read ACR's packages");
        c.S.Usmap = Path.GetFullPath(ask);
        c.SaveSettings();
        return c.S.Usmap;
    }

    static string Last(Proc.Result r) => r.Lines.LastOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? $"exit {r.Code}";
}
