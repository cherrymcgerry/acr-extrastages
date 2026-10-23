using System.Text.Json.Nodes;

namespace AcrExtraStages;

/// scripts/install-stages.ps1: the registry + the containers named in work\acr\batch\registry-ids.json into ~mods, as hard links
/// on the same volume (else copies). Uninstall removes zz_acrxs_* and the legacy zz_acrdirt_* files, nothing else.
internal static class Installer
{
    static readonly string[] Ours = { "zz_acrxs_*", "zz_acrdirt_*" };

    public static int Uninstall(Ctx c)
    {
        var mods = c.ModsDir;
        Ui.Head($"Uninstall from {mods}{(c.DryRunMods is not null ? "  (dry run)" : "")}");
        if (!Directory.Exists(mods)) { Ui.Line("  nothing installed (no ~mods folder)"); return 0; }
        var old = Ours.SelectMany(p => Directory.EnumerateFiles(mods, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in old) File.Delete(f);
        Ui.Ok(old.Count > 0 ? $"  removed {old.Count} zz_acrxs_* / legacy zz_acrdirt_* file(s)" : "  nothing to remove");
        if (c.DryRunMods is null) { c.S.Installed = null; c.S.InstalledTo = null; c.S.InstalledAt = null; c.SaveSettings(); }
        return old.Count;
    }

    public static void Install(Ctx c)
    {
        var mods = c.ModsDir;
        var idsPath = Path.Combine(c.L.Batch, "registry-ids.json");
        if (!File.Exists(idsPath)) throw new LauncherException("nothing built yet (no registry-ids.json): run Install / Build first");
        var reg = JsonNode.Parse(File.ReadAllText(idsPath))!;
        var ids = reg["ids"]?.AsArray().Select(x => x!.ToString()).ToList() ?? new();
        if (reg["built"]?.GetValue<bool>() != true || ids.Count == 0)
            throw new LauncherException($"the last build has no usable registry ({reg["error"]})");
        var files = Directory.EnumerateFiles(c.L.Dist, "zz_acrxs_registry_P.*").ToList();
        foreach (var id in ids) files.AddRange(Directory.EnumerateFiles(c.L.Dist, $"zz_acrxs_s_{id.ToLowerInvariant()}_P.*"));
        if (files.Count == 0) throw new LauncherException($"nothing to install in {c.L.Dist}");
        if (Preflight.AcrRunning()) throw new LauncherException("Assetto Corsa Rally is running: close it before installing");

        Ui.Head($"Install into {mods}{(c.DryRunMods is not null ? "  (dry run)" : "")}");
        if (Directory.Exists(mods))
        {
            // current containers plus the legacy zz_acrdirt_* names, so reinstalling migrates cleanly
            var old = Ours.SelectMany(p => Directory.EnumerateFiles(mods, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in old) File.Delete(f);
            if (old.Count > 0) Ui.Line($"  removed {old.Count} previous zz_acrxs_* / zz_acrdirt_* file(s)");
        }
        Directory.CreateDirectory(mods);
        var mode = "copied";
        if (Preflight.SameVolume(c.L.Dist, mods) && !c.Copy)
        {
            mode = "hard-linked";
            try { foreach (var f in files) Files.HardLink(Path.Combine(mods, Path.GetFileName(f)), f); }
            catch (IOException e)
            {
                Ui.Warn($"  hard link failed ({e.Message}); copying instead");
                mode = "copied";
                foreach (var f in files) File.Delete(Path.Combine(mods, Path.GetFileName(f)));
            }
        }
        if (mode == "copied")
            foreach (var f in files) File.Copy(f, Path.Combine(mods, Path.GetFileName(f)), true);
        var mb = files.Sum(f => new FileInfo(f).Length) / (1024.0 * 1024);
        Ui.Ok($"  installed ({mode}) registry + {ids.Count} stage container(s), {mb:0} MB");
        Ui.Line($"  stages: {string.Join(", ", ids)}");
        if (mode == "hard-linked") Ui.Line("  note: after a rebuild, install again (links keep the old content until then)");
        if (c.DryRunMods is null)
        {
            c.S.Installed = ids;
            c.S.InstalledTo = mods;
            c.S.InstalledAt = DateTime.Now.ToString("s");
            c.SaveSettings();
        }
    }
}
