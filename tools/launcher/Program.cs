using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AcrExtraStages;

internal static class Program
{
    const string Usage = """
        AcrExtraStages.exe [command] [options]       no command: interactive menu

        commands:
          detect       find Assetto Corsa Rally and DiRT Rally 2.0 (Steam registry + libraryfolders.vdf), save them
          preflight    detect + disk space, build ids, ACR not running, tools, mappings (never starts the game)
          mappings     mappings path check (add --dump to create it: ACR starts briefly)
          stages       list the stages in config\stages.toml and the current selection
          specs        write work\acr\specs\*.json only (scripts\gen-stage-specs.ps1 equivalent)
          build        preflight + mappings + prepare + build + verify (no install)
          install      build, then install into ACR's ~mods (hard links on the same drive)
          update       like install, for the installed (or selected) stages: only changed stages are rebuilt
          uninstall    remove every zz_acrxs_* (and legacy zz_acrdirt_*) file from ~mods

        options:
          --acr <dir>             ACR install folder        --dr2 <dir>        DiRT Rally 2.0 install folder
          --usmap <file>          Dumper-7 mappings file (instead of acr-mappings)
          --only <id,...>         stages to build (AcrXsNz01r5 or new_zealand_rally_01_route_5); default: the selection
          --dry-run-mods <dir>    install / uninstall into this folder instead of the game's ~mods
          --force                 rebuild (and re-prepare) even when inputs are unchanged
          --no-verify             skip acr-additive verify      --roundtrip   also round-trip the registry tables
          --max-length-km <km>    defer stages longer than this (stage-fit; default 9)
          --keep-temp             keep per-stage scratch folders --copy     copy instead of hard-linking
          --ignore-disk-space     warn instead of stopping when free space is below the estimate
          --root <dir>            folder with config\ and work\ (default: next to the exe / the repo root)
          --tools <dir>           folder with <tool>\<tool>.exe  --yes      accept defaults, never prompt
          --verbose               echo the tools' output
        """;

    static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string? cmd = null;
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valued = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acr", "dr2", "usmap", "only", "dry-run-mods", "max-length-km", "root", "tools" };
        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            if (a is "-h" or "--help" or "/?") { Console.WriteLine(Usage); return 0; }
            if (a.StartsWith("--") || a.StartsWith('-') && a.Length > 1 && !char.IsDigit(a[1]))
            {
                var k = a.TrimStart('-');
                if (valued.Contains(k))
                {
                    if (i + 1 >= argv.Length) { Console.Error.WriteLine($"{a} needs a value"); return 2; }
                    named[k] = argv[++i];
                }
                else flags.Add(k);
            }
            else if (cmd is null) cmd = a.ToLowerInvariant();
            else { Console.Error.WriteLine($"unexpected argument {a}\n\n{Usage}"); return 2; }
        }
        var interactive = cmd is null;
        Ui.Yes = flags.Contains("yes");

        Ctx c;
        try
        {
            var l = new Layout(named.GetValueOrDefault("root"), named.GetValueOrDefault("tools"));
            var s = Settings.Load(l.SettingsFile);
            c = new Ctx
            {
                L = l,
                P = new Proc(l) { Verbose = flags.Contains("verbose") },
                S = s,
                Cfg = LauncherConfig.Load(Path.Combine(l.Config, "launcher.toml")),
                AcrDir = named.GetValueOrDefault("acr"),
                Dr2Dir = named.GetValueOrDefault("dr2"),
                UsmapArg = named.GetValueOrDefault("usmap"),
                Only = named.GetValueOrDefault("only")?.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                DryRunMods = named.GetValueOrDefault("dry-run-mods"),
                Force = flags.Contains("force"),
                NoVerify = flags.Contains("no-verify"),
                KeepTemp = flags.Contains("keep-temp"),
                IgnoreDisk = flags.Contains("ignore-disk-space"),
                Copy = flags.Contains("copy"),
                MaxLengthKm = named.TryGetValue("max-length-km", out var mk) ? double.Parse(mk, CultureInfo.InvariantCulture) : null,
            };
            Directory.CreateDirectory(l.Logs);
            c.Log = new StreamWriter(Path.Combine(l.Logs, "launcher.log"), true, new UTF8Encoding(false)) { AutoFlush = true };
            c.Log.WriteLine($"==== {DateTime.Now:s} AcrExtraStages {Version} {string.Join(' ', argv)} (root {l.Root})");
        }
        catch (Exception e) when (e is LauncherException or IOException or FormatException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }

        var roundTrip = flags.Contains("roundtrip");
        try
        {
            if (interactive) return Menu(c, roundTrip);
            return cmd switch
            {
                "detect" => Preflight.Detect(c, false) ? 0 : 1,
                "preflight" => Preflight.Detect(c, false) & Preflight.Run(c, c.Selection(StagesConfig.Load(c.L.StagesToml)).Count, true).Ok ? 0 : 1,
                "mappings" => MappingsCmd(c, flags.Contains("dump")),
                "stages" => ListStages(c),
                "specs" => GenSpecs(c),
                "build" => Build(c, false, false, roundTrip),
                "install" => Build(c, true, false, roundTrip),
                "update" => Build(c, true, true, roundTrip),
                "uninstall" => UninstallCmd(c),
                _ => Bad(cmd!),
            };
        }
        catch (LauncherException e)
        {
            Ui.Err("error: " + e.Message);
            c.Log?.WriteLine("error: " + e.Message);
            return 1;
        }
        finally { c.Log?.Dispose(); }
    }

    static string Version => typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";

    static int Bad(string cmd)
    {
        Console.Error.WriteLine($"unknown command {cmd}\n\n{Usage}");
        return 2;
    }

    static int MappingsCmd(Ctx c, bool dump)
    {
        if (!Preflight.Detect(c, false) && c.AcrDir is null) return 1;
        Ui.Head("Mappings");
        if (dump)
        {
            var p = Mappings.Ensure(c);
            Ui.Ok($"  {p}");
            return 0;
        }
        var m = Mappings.Check(c);
        if (m.Usmap is not null) { Ui.Ok($"  {m.Usmap}  ({m.Source})"); return 0; }
        Ui.Warn($"  none: {m.Problem}");
        Ui.Line("  `mappings --dump` creates it (ACR starts briefly), or pass --usmap <Dumper-7 file>.");
        return 1;
    }

    static int UninstallCmd(Ctx c)
    {
        if (c.DryRunMods is null && !Preflight.Detect(c, false) && c.AcrDir is null) return 1;
        if (Preflight.AcrRunning() && c.DryRunMods is null) throw new LauncherException("Assetto Corsa Rally is running: close it first");
        Installer.Uninstall(c);
        return 0;
    }

    static int Build(Ctx c, bool install, bool update, bool roundTrip)
    {
        var cfg = StagesConfig.Load(c.L.StagesToml);
        if (!Preflight.Detect(c, !Ui.Yes)) return 1;
        var plan = c.Selection(cfg);
        if (update && c.Only is not { Count: > 0 } && c.S.Installed is { Count: > 0 })
        {
            var inst = cfg.Entries().Where(e => e.Enabled && c.S.Installed.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (inst.Count > 0) plan = inst;
        }
        if (plan.Count == 0) throw new LauncherException("no stage selected (Choose stages, or --only <id>)");
        var pf = Preflight.Run(c, plan.Count, true);
        if (!pf.Ok) return 1;
        Ui.Line();
        Ui.Line($"{plan.Count} stage(s): {string.Join(", ", plan.Select(p => p.Id))}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = new Pipeline(c).Run(plan, roundTrip);
        Ui.Line($"build finished in {sw.Elapsed:hh\\:mm\\:ss}");
        var failed = outcome.Rows.Count(r => r.status is "failed" or "needs-prepare");
        if (failed > 0) Ui.Warn($"{failed} stage(s) not built - see work\\acr\\batch\\report.md and logs");
        if (!outcome.Registry.built) { Ui.Err("no registry was built: nothing to install"); return 1; }
        if (!c.NoVerify && !outcome.Registry.verify.StartsWith("ALL PASS"))
        {
            Ui.Err($"verify did not pass ({outcome.Registry.verify}): not installing. See work\\acr\\additive\\check\\verify.txt");
            return 1;
        }
        if (!install) return 0;
        Installer.Install(c);
        Ui.Line("Start the game and look for the new stages in the stage menus (offline modes only).");
        return 0;
    }

    static int GenSpecs(Ctx c)
    {
        var specs = Specs.Generate(c, StagesConfig.Load(c.L.StagesToml), c.Log ?? TextWriter.Null);
        Ui.Ok($"generated {specs.Count} stage specs in {c.L.Specs}");
        return 0;
    }

    static int ListStages(Ctx c)
    {
        var cfg = StagesConfig.Load(c.L.StagesToml);
        var sel = c.Selection(cfg).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PrintStages(c, cfg.Entries(), sel);
        return 0;
    }

    static void PrintStages(Ctx c, List<StageEntry> all, HashSet<string> sel)
    {
        var installed = c.S.Installed?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            var (name, km) = StageInfo(c, e);
            var mark = !e.Enabled ? "later" : sel.Contains(e.Id) ? "[x]" : "[ ]";
            var tags = new List<string>();
            if (installed.Contains(e.Id)) tags.Add("installed");
            if (File.Exists(Path.Combine(c.L.Dist, $"zz_acrxs_s_{e.Id.ToLowerInvariant()}_P.utoc"))) tags.Add("built");
            var line = string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1,3} {mark,-5} {e.Id,-13} {e.LocationName,-12} {Trunc(name, 30),-30} {(km is null ? "" : $"{km:0.0} km"),8}  {string.Join(", ", tags)}");
            if (!e.Enabled) Ui.Detail(line.Trim());
            else Ui.Line(line);
        }
        Ui.Line($"  {sel.Count} selected, {all.Count(e => e.Enabled)} available (fit), {all.Count(e => e.Default)} recommended, {all.Count(e => !e.Enabled)} too long to fit");
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    static (string Name, double? Km) StageInfo(Ctx c, StageEntry e)
    {
        var p = Path.Combine(c.L.IrRoot, e.Dr2, "stage.json");
        if (!File.Exists(p)) return (e.Dr2, null);
        try
        {
            // stage.json is large; the name and length sit near the top
            using var r = new StreamReader(p);
            var head = new char[4096];
            var n = r.ReadBlock(head, 0, head.Length);
            var text = new string(head, 0, n);
            var name = System.Text.RegularExpressions.Regex.Match(text, "\"name\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
            var len = System.Text.RegularExpressions.Regex.Match(text, "\"length_m\"\\s*:\\s*([0-9.]+)");
            return (name.Length > 0 ? name : e.Dr2, len.Success ? double.Parse(len.Groups[1].Value, CultureInfo.InvariantCulture) / 1000 : null);
        }
        catch (IOException) { return (e.Dr2, null); }
    }

    // ---- interactive -----------------------------------------------------------------------------------------------------

    static int Menu(Ctx c, bool roundTrip)
    {
        Console.Title = "ACR Extra Stages";
        Ui.Line($"ACR Extra Stages {Version} (beta) - extra rally stages for Assetto Corsa Rally, built from your own DiRT Rally 2.0");
        Ui.Line("Offline / single-player only. Do not redistribute the generated files.");
        Preflight.Detect(c, true);
        while (true)
        {
            var cfg = StagesConfig.Load(c.L.StagesToml);
            var sel = c.Selection(cfg);
            Ui.Head("Menu");
            Ui.Line($"  ACR: {c.AcrDir ?? "(not set)"}");
            Ui.Line($"  DR2: {c.Dr2Dir ?? "(not set)"}");
            Ui.Line($"  stages selected: {sel.Count}; installed: {c.S.Installed?.Count ?? 0}" +
                    (c.DryRunMods is not null ? $"; dry run into {c.DryRunMods}" : ""));
            Ui.Line();
            Ui.Line("  1) Install         build the selected stages, verify and install them");
            Ui.Line("  2) Update          rebuild what changed (game update / new release) and reinstall");
            Ui.Line("  3) Uninstall       remove the stages from the game");
            Ui.Line("  4) Choose stages");
            Ui.Line("  5) Check           preflight and mappings check");
            Ui.Line("  6) Game folders    set the ACR / DR2 folders");
            Ui.Line("  0) Exit");
            var a = Ui.Ask("Choose", "0");
            try
            {
                switch (a)
                {
                    case "1": Build(c, true, false, roundTrip); break;
                    case "2": Build(c, true, true, roundTrip); break;
                    case "3":
                        if (Ui.Confirm("Remove every zz_acrxs_* file from the game's ~mods folder?")) UninstallCmd(c);
                        break;
                    case "4": Choose(c, cfg); break;
                    case "5": Preflight.Run(c, sel.Count, true); break;
                    case "6": SetFolders(c); break;
                    case "0" or "q" or "exit": return 0;
                    default: Ui.Warn("  unknown choice"); break;
                }
            }
            catch (LauncherException e) { Ui.Err("error: " + e.Message); c.Log?.WriteLine("error: " + e.Message); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Ui.Err("error: " + e.Message);
                c.Log?.WriteLine(e.ToString());
            }
            if (Ui.Yes) return 0;
            Ui.Ask("Press Enter to return to the menu", "");
        }
    }

    static void SetFolders(Ctx c)
    {
        var acr = Ui.Ask("  Assetto Corsa Rally folder", c.AcrDir);
        var dr2 = Ui.Ask("  DiRT Rally 2.0 folder", c.Dr2Dir);
        if (Steam.IsAcr(acr)) c.AcrDir = Path.GetFullPath(acr!); else Ui.Err($"  not an ACR install: {acr}");
        if (Steam.IsDr2(dr2)) c.Dr2Dir = Path.GetFullPath(dr2!); else Ui.Err($"  not a DR2 install: {dr2}");
        c.SaveSettings();
    }

    static void Choose(Ctx c, StagesConfig cfg)
    {
        var all = cfg.Entries();
        var sel = c.Selection(cfg).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            Ui.Head("Choose stages");
            PrintStages(c, all, sel);
            Ui.Line("  numbers or ranges toggle (3 5-8), a location name toggles it");
            Ui.Line("  'all' = every stage that fits, 'default' = the recommended 30, 'none', Enter = done");
            var a = Ui.Ask("  toggle", "");
            if (string.IsNullOrWhiteSpace(a)) break;
            if (a.Equals("all", StringComparison.OrdinalIgnoreCase)) { sel = all.Where(e => e.Enabled).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); continue; }
            if (a.Equals("default", StringComparison.OrdinalIgnoreCase)) { sel = all.Where(e => e.Enabled && e.Default).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); continue; }
            if (a.Equals("none", StringComparison.OrdinalIgnoreCase)) { sel.Clear(); continue; }
            var locs = all.Where(e => e.Enabled && (e.LocationName.Equals(a, StringComparison.OrdinalIgnoreCase) || e.LocationKey.Equals(a, StringComparison.OrdinalIgnoreCase))).ToList();
            if (locs.Count > 0)
            {
                var on = locs.Any(e => !sel.Contains(e.Id));
                foreach (var e in locs) { if (on) sel.Add(e.Id); else sel.Remove(e.Id); }
                continue;
            }
            foreach (var tok in a.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = tok.Split('-');
                if (!int.TryParse(parts[0], out var lo) || !int.TryParse(parts[^1], out var hi)) { Ui.Warn($"  ignored '{tok}'"); continue; }
                for (int i = Math.Max(1, lo); i <= Math.Min(all.Count, hi); i++)
                {
                    var e = all[i - 1];
                    if (!e.Enabled) { Ui.Warn($"  {e.Id} is listed for later (long stage) and cannot be built yet"); continue; }
                    if (!sel.Remove(e.Id)) sel.Add(e.Id);
                }
            }
        }
        c.S.Selected = all.Where(e => sel.Contains(e.Id)).Select(e => e.Id).ToList();
        c.SaveSettings();
        Ui.Ok($"  {c.S.Selected.Count} stage(s) selected");
    }
}
