using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcrExtraStages;

/// The build: scripts/build-stages.ps1 (plan, skip unchanged, one stage at a time, continue past failures, registry, report)
/// with the per-stage critical path of mods/additive/build.ps1 (stage-build, overlay, relocate, menu assets, retoc to-zen)
/// ported here. Only acr-ref, acr-additive, dr2-extract and retoc run as child processes.
internal sealed class Pipeline
{
    // bump when the build sequence below changes: every stage's input hash changes with it
    const string PipelineVersion = "launcher-pipeline-1";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly Ctx _c;
    readonly Layout _l;
    readonly string _acrRef, _additive, _retoc;
    int _done, _total;

    public Pipeline(Ctx c)
    {
        _c = c;
        _l = c.L;
        _acrRef = _l.Tool("acr-ref");
        _additive = _l.Tool("acr-additive");
        _retoc = _l.Tool("retoc");
        _l.Tool("dr2-extract");
    }

    public sealed class Row
    {
        public string id { get; set; } = "";
        public string dr2 { get; set; } = "";
        public string location { get; set; } = "";
        public string status { get; set; } = "pending";
        public string? reason { get; set; }
        public List<string> warnings { get; set; } = new();
        public int seconds { get; set; }
        public string? name { get; set; }
        public double? length_km { get; set; }
        public string? placement_mode { get; set; }
        public double? theta_deg { get; set; }
        public double? world_km2 { get; set; }
        public string? fit_status { get; set; }
        public string? input_hash { get; set; }
        public double? container_mb { get; set; }
        public int? pacenote_rows { get; set; }
        public string? pacenote_tokens { get; set; }
        public string? pacenote_unmapped { get; set; }
        public string? world_m { get; set; }
        public string? prepare { get; set; }
    }

    public sealed class RegistryResult
    {
        public List<string> ids { get; set; } = new();
        public bool built { get; set; }
        public string verify { get; set; } = "skipped";
        public string? error { get; set; }
    }

    public sealed record Outcome(List<Row> Rows, RegistryResult Registry, bool Ok);

    void Progress(string msg)
    {
        var pct = _total == 0 ? 100 : Math.Min(100, _done * 100 / _total);
        Ui.Line($"[{pct,3}%] {msg}");
    }

    public Outcome Run(List<StageEntry> plan, bool roundTrip)
    {
        Directory.CreateDirectory(_l.Logs);
        Directory.CreateDirectory(_l.State);
        Directory.CreateDirectory(_l.Dist);
        var log = _c.Log ?? TextWriter.Null;
        _total = plan.Count * 2 + 5;
        _done = 0;
        var cfg = StagesConfig.Load(_l.StagesToml);

        // ---- mappings -----------------------------------------------------------------------------------------------------
        Ui.Head("Mappings");
        var usmap = Mappings.Ensure(_c);
        _c.P.Env["ACR_USMAP"] = usmap;
        _c.P.Env["ACR_GAME_DIR"] = _c.AcrDir!;
        _c.P.Env["DR2_DIR"] = _c.Dr2Dir!;
        _c.P.Env["ACR_ADDITIVE"] = _additive;
        _done++;

        // ---- prepare DR2 data -----------------------------------------------------------------------------------------------
        Ui.Head($"Preparing DR2 data ({plan.Count} stage(s))");
        var needArt = cfg.Entries().Any(e => e.Enabled && StagesConfig.Bool(e.Raw, "menu_images")) ||
                      cfg.Locations.Values.Any(l => StagesConfig.Str(l, "row") is null && StagesConfig.Str(l, "tile") is null);
        if (needArt)
        {
            try { Prepare.MenuArt(_c, log); }
            catch (StepFailed e) { Ui.Warn($"  DR2 loading art: {e.Message} (menu backdrops keep the template's)"); }
        }
        var prep = new Dictionary<string, Prepare.Row>();
        int pi = 0;
        foreach (var st in plan)
        {
            pi++;
            using var plog = Append(Path.Combine(_l.Logs, $"prepare-{st.Id}.log"));
            var r = Prepare.Stage(_c, st, plog);
            prep[st.Dr2] = r;
            _done++;
            var what = !r.Ok ? $"FAILED: {r.Error}" : r.Steps.Contains("skip") ? $"ready (bake {r.Bake})" : $"prepared in {r.Seconds} s (bake {r.Bake})";
            Progress($"prepare {pi}/{plan.Count} {st.Id}: {what}");
        }
        Files.WriteJson(Path.Combine(_l.Batch, "prepare-data.json"), prep.Values.ToList());

        // ---- specs, vanilla inputs, fit -------------------------------------------------------------------------------------
        Ui.Head("Build inputs");
        var specs = Specs.Generate(_c, cfg, log);
        Ui.Line($"  {specs.Count} stage spec(s) in {_l.Specs}");
        _done++;
        var vanilla = Vanilla.NewView(_c.AcrDir!, Path.Combine(_l.Additive, "vanilla"), log);
        var paks = Path.Combine(vanilla, "acr", "Content", "Paks");
        var planSpecs = plan.Where(p => specs.ContainsKey(p.Id)).Select(p => specs[p.Id]).ToList();
        var legacy = EnsureLegacy(planSpecs, paks, log);
        var stageLegacy = EnsureStageLegacy(vanilla, paks, log);
        Ui.Line("  vanilla ACR packages extracted");
        _done++;

        var fitPath = Path.Combine(_l.AcrWork, "fit-report.json");
        var fitArgs = new List<string> { "stage-fit", "--ir-root", _l.IrRoot, "--out", fitPath };
        if (_c.MaxLengthKm is { } mk) fitArgs.AddRange(new[] { "--max-length", (mk * 1000).ToString(Inv) });
        _c.P.Run(_acrRef, fitArgs, log, "checking stage placement");
        var fit = new Dictionary<string, JsonNode>();
        if (File.Exists(fitPath))
            foreach (var f in JsonNode.Parse(File.ReadAllText(fitPath))?["stages"]?.AsArray() ?? new JsonArray())
                if (f?["stage"]?.ToString() is { } s) fit[s] = f;
        _done++;

        var configSig = string.Join(";", new[] { "visuals", "sky", "pacenote-map", "object-collision", "scenery", "surface-map" }
            .Select(n => $"{n}=" + Files.ContentHash(Path.Combine(_l.Config, n + ".toml"))));
        var toolSig = string.Join(";", new[] { _acrRef, _additive }.Select(t =>
        {
            var dll = Path.ChangeExtension(t, ".dll");
            return Files.ContentHash(File.Exists(dll) ? dll : t);
        }));
        var vanillaSig = Files.HashText(string.Join(";", Vanilla.Stamp(legacy) ?? new List<string> { "none" }));

        // ---- plan -------------------------------------------------------------------------------------------------------------
        var rows = new List<Row>();
        foreach (var p in plan)
        {
            var row = new Row { id = p.Id, dr2 = p.Dr2, location = p.LocationName };
            var ir = Path.Combine(_l.IrRoot, p.Dr2);
            if (fit.TryGetValue(p.Dr2, out var f))
            {
                row.name = f["name"]?.ToString();
                row.length_km = f["length_m"] is { } lm ? Math.Round((double)lm / 1000, 2) : null;
                row.placement_mode = f["placement_mode"]?.ToString();
                row.theta_deg = f["theta_deg"] is { } th ? (double)th : null;
                row.world_km2 = f["world_km2"] is { } wk ? (double)wk : null;
                row.fit_status = f["status"]?.ToString();
            }
            if (prep.TryGetValue(p.Dr2, out var pr)) row.prepare = pr.Ok ? $"ok, bake {pr.Bake}" : pr.Error;
            var needs = new List<string>();
            if (!File.Exists(Path.Combine(ir, "stage.json"))) needs.Add("stage.json");
            if (!File.Exists(Path.Combine(ir, "render", "terrain.glb"))) needs.Add("render meshes");
            if (!Directory.Exists(Path.Combine(ir, "textures")) || !Directory.EnumerateFileSystemEntries(Path.Combine(ir, "textures")).Any()) needs.Add("textures");
            var bake = Path.Combine(ir, "render", "ground_bake", "manifest.json");
            if (row.fit_status?.StartsWith("deferred") == true)
            {
                row.status = "deferred";
                row.reason = string.Join("; ", (f!["notes"]?.AsArray() ?? new JsonArray()).Select(x => x?.ToString()));
            }
            else if (row.fit_status == "error") { row.status = "failed"; row.reason = $"stage-fit: {f!["error"]}"; }
            else if (needs.Count > 0 || !specs.ContainsKey(p.Id))
            {
                row.status = "needs-prepare";
                row.reason = $"needs prepare: missing {string.Join(", ", needs.DefaultIfEmpty("stage spec"))} in work\\ir\\{p.Dr2}" + (pr is { Ok: false } ? $" ({pr.Error})" : "");
            }
            else
            {
                if (!File.Exists(bake)) row.warnings.Add("no ground_bake manifest: building with the DR2 grass-slice ground");
                row.input_hash = Files.HashText(string.Join("|",
                    "spec=" + Files.ContentHash(specs[p.Id].File), "ir=" + Files.FileSig(Path.Combine(ir, "stage.json")),
                    "terrain=" + Files.FileSig(Path.Combine(ir, "render", "terrain.glb")), "bake=" + Files.FileSig(bake),
                    $"cfg={configSig}", $"tools={toolSig}", $"script={PipelineVersion}", $"vanilla={vanillaSig}"));
                row.status = "todo";
            }
            rows.Add(row);
        }

        // ---- build ------------------------------------------------------------------------------------------------------------
        Ui.Head($"Building {rows.Count(r => r.status == "todo")} stage(s)");
        int n = 0;
        foreach (var r in rows)
        {
            n++;
            var idl = r.id.ToLowerInvariant();
            var container = Path.Combine(_l.Dist, $"zz_acrxs_s_{idl}_P.utoc");
            var stFile = Path.Combine(_l.State, idl + ".json");
            if (r.status != "todo")
            {
                // not buildable now: never leave an old container for the registry to pick up
                Files.DeleteMatching(_l.Dist, $"zz_acrxs_s_{idl}_P.*");
                _done++;
                Progress($"{n}/{rows.Count} {r.id}: {r.status} - {r.reason}");
                continue;
            }
            var prev = ReadState(stFile);
            if (!_c.Force && prev is { ok: true } && prev.input_hash == r.input_hash && File.Exists(container))
            {
                r.status = "unchanged";
                _done++;
                Progress($"{n}/{rows.Count} {r.id}: unchanged (inputs identical, container present)");
                continue;
            }
            var logPath = Path.Combine(_l.Logs, r.id + ".log");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? error = null;
            using (var slog = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true })
            {
                try { BuildStage(specs[r.id], legacy, stageLegacy, vanilla, slog, $"{n}/{rows.Count} {r.id}"); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    error = e.Message;
                    slog.WriteLine($"FAILED: {e.Message}");
                }
            }
            r.seconds = (int)sw.Elapsed.TotalSeconds;
            var lines = File.ReadAllLines(logPath);
            if (error is null && File.Exists(container))
            {
                r.status = "built";
                // advisory offline checks print WARN: listed in the report, they do not fail the stage
                var warn = lines.Where(x => x.StartsWith("WARN ")).Select(x =>
                    Regex.Replace(Regex.Replace(x, @"^WARN (visuals ground: |visuals road corridor: |visuals: )?", ""), @"\s*\(.*$", "")).ToList();
                if (warn.Count > 0) r.warnings.Add($"advisory WARN x{warn.Count}: " + string.Join(" / ", warn.Select(w => w[..Math.Min(60, w.Length)])));
            }
            else
            {
                r.status = "failed";
                Files.DeleteMatching(_l.Dist, $"zz_acrxs_s_{idl}_P.*");
                CleanScratch(idl);
                var why = lines.Where(x => x.StartsWith("FAIL ")).Take(3)
                    .Concat(lines.Where(x => Regex.IsMatch(x, @"(?i)\berror\b|exception|not enough space|failed \(")).TakeLast(2))
                    .Select(x => x.Trim()).Distinct().ToList();
                var msg = $"{error ?? "no container written"}; {string.Join(" | ", why)}";
                r.reason = msg[..Math.Min(500, msg.Length)];
                var miss = lines.FirstOrDefault(x => Regex.IsMatch(x, @"Could not find file '.*\\work\\ir\\"));
                if (miss is not null)
                {
                    // prepared data is incomplete (a texture the render materials reference was never extracted)
                    r.status = "needs-prepare";
                    r.reason = "needs prepare: incomplete IR data - " + Regex.Replace(Regex.Replace(miss, @"^.*Could not find file '", ""), @"'\.?\s*$", "");
                }
            }
            Files.WriteJson(stFile, new StageState { input_hash = r.input_hash, ok = r.status == "built", at = DateTime.Now.ToString("s") });
            _done++;
            Progress($"{n}/{rows.Count} {r.id}: {r.status} in {r.seconds} s{(r.reason is null ? "" : ": " + r.reason)}");
        }

        // ---- per-stage facts from the stage-build reports -----------------------------------------------------------------------
        foreach (var r in rows) Facts(r);

        // ---- registry (only stages that have a container) -----------------------------------------------------------------------
        Ui.Head("Registry");
        var passed = rows.Where(r => r.status is "built" or "unchanged").ToList();
        var reg = new RegistryResult { ids = passed.Select(r => r.id).ToList() };
        if (passed.Count > 0)
        {
            using var rlog = new StreamWriter(Path.Combine(_l.Logs, "registry.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            var regSpecs = passed.Select(r => specs[r.id].File).ToList();
            try
            {
                BuildRegistry(regSpecs, legacy, rlog);
                reg.built = true;
                _done++;
                Progress($"registry built for {passed.Count} stage(s)");
                if (!_c.NoVerify)
                {
                    reg.verify = VerifyAll(passed, regSpecs, vanilla, paks, roundTrip, rlog);
                    Progress($"verify: {reg.verify}");
                }
            }
            catch (Exception e) when (e is StepFailed or IOException or LauncherException)
            {
                rlog.WriteLine($"FAILED: {e.Message}");
                reg.error = e.Message;
                if (reg.built) reg.verify = "FAILED: " + e.Message;
                else Files.DeleteMatching(_l.Dist, "zz_acrxs_registry_P.*");
                Ui.Err($"  registry: {e.Message}");
            }
        }
        else
        {
            Files.DeleteMatching(_l.Dist, "zz_acrxs_registry_P.*");
            reg.error = "no stage passed; no registry written";
        }
        _done = _total;
        Files.WriteJson(Path.Combine(_l.Batch, "registry-ids.json"), reg);
        WriteReport(rows, reg);
        var verifyOk = _c.NoVerify || reg.verify.StartsWith("ALL PASS");
        return new Outcome(rows, reg, reg.built && verifyOk);
    }

    static StreamWriter Append(string path) => new(path, true, new UTF8Encoding(false)) { AutoFlush = true };

    sealed class StageState
    {
        public string? input_hash { get; set; }
        public bool ok { get; set; }
        public string? at { get; set; }
    }

    static StageState? ReadState(string p)
    {
        try { return File.Exists(p) ? JsonSerializer.Deserialize<StageState>(File.ReadAllText(p)) : null; }
        catch (JsonException) { return null; }
    }

    // ---- vanilla extraction ---------------------------------------------------------------------------------------------------

    /// work\acr\additive\legacy: the DB tables, menu texture templates and every package a stage clones (mods/additive/build.ps1).
    string EnsureLegacy(List<Specs.Spec> specs, string paks, TextWriter log)
    {
        var legacy = Path.Combine(_l.Additive, "legacy");
        Vanilla.InvalidateIfStale(legacy, paks);
        var filters = new List<string> { "Data/Database/Main/" };
        if (specs.Any(s => s.MenuImages)) filters.Add("Data/UITextures/Tracks/Tracks/Wales");
        if (specs.Any(s => s.MenuImages && s.MenuArt is not null))
            filters.AddRange(new[]
            {
                "Data/UITextures/Tracks/T_HafrenSouthBackground", "Data/UITextures/Tracks/TracksImages/Wales/T_Severne",
                "Data/UITextures/Tracks/LobbyImages/SingleStage/Wales/T_Afon_Lobby",
            });
        if (specs.Any(s => s.LocationTemplate && s.LocationArtOrLogo))
            filters.AddRange(new[] { "Data/UITextures/Tracks/T_WalesRally", "Data/UITextures/Tracks/T_RallyWales" });
        foreach (var s in specs)
            foreach (var p in s.ClonePackages) filters.Add(p.Key["/Game/".Length..]);
        var need = filters.Distinct().Where(f =>
        {
            var rel = Path.Combine(legacy, "acr", "Content", f.Replace('/', '\\').TrimEnd('\\'));
            var hasDir = Directory.Exists(rel) && Directory.EnumerateFiles(rel, "*", SearchOption.AllDirectories)
                .Any(x => x.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".umap", StringComparison.OrdinalIgnoreCase));
            return !hasDir && !File.Exists(rel + ".umap") && !File.Exists(rel + ".uasset");
        }).ToList();
        if (need.Count > 0)
        {
            Vanilla.AssertPaks(paks);
            var args = new List<string> { "to-legacy", "--version", "UE5_6", "--no-shaders" };
            foreach (var f in need) args.AddRange(new[] { "--filter", f });
            args.AddRange(new[] { paks, legacy });
            _c.P.Must(_retoc, args, log, "extracting vanilla ACR packages");
        }
        Vanilla.WriteStamp(legacy, paks);
        return legacy;
    }

    /// work\acr\legacy-vanilla: Hafren South host packages and texture templates for stage-build (mods/stage/build.ps1 -ExtractOnly),
    /// plus the physical-material registry and its surface-map check.
    string EnsureStageLegacy(string vanilla, string paks, TextWriter log)
    {
        var legacy = Path.Combine(_l.AcrWork, "legacy-vanilla");
        Vanilla.InvalidateIfStale(legacy, paks);
        var host = new (string Rel, string? Filter)[]
        {
            (@"acr\Content\Environments\WalesS4HafrenSouth\DataAssets\DA_WalesS4HafrenSouthBakedData", null),
            (@"acr\Content\Levels\WalesS4HafrenSouth\_Generated_\22VG0QIP9ATUQI633K86KWPFP", null),
            (@"acr\Content\Data\Database\Main\TrackSelection\DT_TracksVariants", null),
            (@"acr\Content\Data\Pacenote\Tables\DT_PacenoteHafrenSouthForward", null),
            (@"acr\Content\Levels\WalesS4HafrenSouth", "WalesS4HafrenSouth.umap"),   // the bare name would match all of its cells
            (@"acr\Content\Data\Localization\ContentTexts\ST_Track", null),
            (@"acr\Content\Materials\PhotoMode\Textures\T_Dirt", null),
            (@"acr\Content\Effects\Vehicles\Skid\T_SkidDebug_N", null),
            (@"acr\Content\Environments\MonteCarloS2Sisteron\TileableMaterials\MI_SisteronConcrete01", null),
        };
        foreach (var (rel, filter) in host)
        {
            if (File.Exists(Path.Combine(legacy, rel + ".uexp"))) continue;
            Vanilla.AssertPaks(paks);
            _c.P.Must(_retoc, new[] { "to-legacy", "--version", "UE5_6", "--no-shaders", "--filter", filter ?? Path.GetFileName(rel), paks, legacy },
                log, "extracting the host stage");
        }
        Vanilla.WriteStamp(legacy, paks);
        var surfaceReg = Path.Combine(legacy, "DA_PhysMaterialsMappingCommon.json");
        if (!File.Exists(surfaceReg))
            _c.P.Must(_acrRef, new[] { "export", "acr/Content/Data/Gameplay/PhysicalMaterials/DA_PhysMaterialsMappingCommon.uasset", "--game", vanilla, "--out", surfaceReg }, log);
        _c.P.Must(_acrRef, new[] { "surface-map-check", "--map", Path.Combine(_l.Config, "surface-map.toml"), "--registry", surfaceReg }, log);
        return legacy;
    }

    // ---- one stage ---------------------------------------------------------------------------------------------------------

    void BuildStage(Specs.Spec s, string legacy, string stageLegacy, string vanilla, TextWriter log, string label)
    {
        var id = s.Id;
        var idl = id.ToLowerInvariant();
        var work = _l.Additive;
        var ir = Path.Combine(_l.IrRoot, s.Dr2);
        if (!File.Exists(Path.Combine(ir, "stage.json"))) throw new StepFailed($"{id}: no stage IR at {ir}");
        var mod = Path.Combine(work, $"mod-build-{idl}");
        var report = Path.Combine(work, $"stage-{idl}.report.json");
        Files.DeleteDir(mod);
        var cfg = (string n) => Path.Combine(_l.Config, n);

        var sb = new List<string>
        {
            "stage-build", "--ir", ir, "--legacy", stageLegacy, "--out", mod, "--report", report,
            "--surface-registry", Path.Combine(stageLegacy, "DA_PhysMaterialsMappingCommon.json"), "--additive", "1",
            "--package-root", $"/Game/AcrXs/Stages/{id}",
            "--objects", cfg("object-collision.toml"),
            "--pacenote-map", cfg("pacenote-map.toml"),
            "--pacenote-report", Path.Combine(work, $"pacenotes-{idl}.md"),
            "--respawn", "relay",
            "--scenery", cfg("scenery.toml"), "--scenery-actor-hidden", "1",
            "--visuals", cfg("visuals.toml"), "--visuals-host", "start", "--visuals-retarget", "1",
            "--visuals-vegetation", "none", "--visuals-vista", "1", "--visuals-textured", "1",
            "--visuals-dr2", "1", "--visuals-canopies", "apex", "--visuals-terrain-uv", "box",
            "--visuals-ground", "render", "--visuals-ground-lod", "1",
            "--sky", cfg("sky.toml"), "--sky-section", s.Sky,
            // placement picks rotation / height itself (host region when the route fits, else an enlarged world); GroundOffset adds to it
            "--bounds", "1",
        };
        if (s.GroundOffset != 0) sb.AddRange(new[] { "--ground-offset", s.GroundOffset.ToString(Inv) });
        var bake = Path.Combine(ir, "render", "ground_bake", "manifest.json");
        if (File.Exists(bake)) sb.AddRange(new[] { "--visuals-ground-bake", bake });
        _c.P.Must(_acrRef, sb, log, $"{label}: stage-build");

        var overlay = Path.Combine(work, $"overlay-{idl}");
        using (new Ticker($"{label}: overlay")) Overlay(legacy, mod, overlay);

        var outMod = Path.Combine(work, $"mod-stage-{idl}");
        Files.DeleteDir(outMod);
        var rel = new List<string> { "relocate", "--legacy", overlay, "--out", outMod };
        foreach (var p in s.ClonePackages) rel.AddRange(new[] { "--map", $"{p.Key}={p.Value}" });
        foreach (var p in s.CloneObjects) rel.AddRange(new[] { "--object", $"{p.Key}={p.Value}" });
        _c.P.Must(_additive, rel, log, $"{label}: relocate");

        // Tex/Mat already live under /Game/AcrXs/Stages/<Id>/ (not relocated): copy into the stage container
        foreach (var sub in new[] { "Tex", "Mat" })
        {
            var src = Path.Combine(mod, "acr", "Content", "AcrXs", "Stages", id, sub);
            if (Directory.Exists(src)) Files.CopyTree(src, Path.Combine(outMod, "acr", "Content", "AcrXs", "Stages", id, sub));
        }
        // menu images: route map, track map and elevation profile as UI textures under /Game/AcrXs/Stages/<Id>/Menu
        if (s.MenuImages)
        {
            var menu = new List<string> { "menu-assets", "--ir", ir, "--id", id, "--legacy", legacy, "--out", outMod, "--preview", Path.Combine(_l.AcrWork, "menu-preview", id) };
            if (s.MenuArt is not null)
            {
                var art = Path.Combine(_l.MenuArtLoading, s.MenuArt + ".tpk");
                if (!File.Exists(art)) throw new StepFailed($"{id}: missing DR2 loading art {art}");
                menu.AddRange(new[] { "--art", art });
            }
            _c.P.Must(_additive, menu, log, $"{label}: menu images");
        }
        Files.DeleteMatching(_l.Dist, $"zz_acrxs_s_{idl}_P.*");
        _c.P.Must(_retoc, new[] { "to-zen", "--version", "UE5_6", outMod, Path.Combine(_l.Dist, $"zz_acrxs_s_{idl}_P.utoc") }, log, $"{label}: packing");
        // per-stage scratch is large (full Hafren tree + render meshes); the container in dist is the product
        if (!_c.KeepTemp) CleanScratch(idl);
    }

    void CleanScratch(string idl)
    {
        foreach (var d in new[] { $"mod-build-{idl}", $"overlay-{idl}", $"mod-stage-{idl}" })
            try { Files.DeleteDir(Path.Combine(_l.Additive, d)); } catch (IOException) { }
    }

    /// Full Hafren tree + BakedData + pacenote table, then the stage-build output on top (edited packages + DR2 Tex/Mat).
    static void Overlay(string legacy, string mod, string outDir)
    {
        Files.DeleteDir(outDir);
        Directory.CreateDirectory(outDir);
        foreach (var rel in new[]
                 {
                     @"acr\Content\Levels\WalesS4HafrenSouth", @"acr\Content\Environments\WalesS4HafrenSouth\DataAssets", @"acr\Content\Data\Pacenote\Tables",
                 })
        {
            var s = Path.Combine(legacy, rel);
            if (Directory.Exists(s)) Files.CopyTree(s, Path.Combine(outDir, rel));
        }
        Files.CopyTree(mod, outDir);
    }

    void Facts(Row r)
    {
        var idl = r.id.ToLowerInvariant();
        var ucas = Path.Combine(_l.Dist, $"zz_acrxs_s_{idl}_P.ucas");
        if (File.Exists(ucas)) r.container_mb = Math.Round(new FileInfo(ucas).Length / (1024.0 * 1024), 0);
        var rep = Path.Combine(_l.Additive, $"stage-{idl}.report.json");
        if (r.status is not ("built" or "unchanged") || !File.Exists(rep)) return;
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(rep));
            if (j?["tables"]?["pacenotes"] is JsonObject pn)
            {
                r.pacenote_rows = pn["rows"] is { } rows ? (int)(double)rows : null;
                r.pacenote_tokens = $"{pn["dr2_tokens_mapped"]}/{pn["dr2_tokens"]}";
                r.pacenote_unmapped = pn["unmapped"] is JsonObject un ? string.Join("; ", un.Select(kv => $"{kv.Key} x{kv.Value}")) : "";
            }
            if (j?["placement"]?["world"] is JsonObject w)
                r.world_m = string.Format(Inv, "X {0:0}..{1:0}, Z {2:0}..{3:0}, Y {4:0}..{5:0}",
                    (double)w["min_x"]!, (double)w["max_x"]!, (double)w["min_z"]!, (double)w["max_z"]!, (double)w["min_y"]!, (double)w["max_y"]!);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
    }

    // ---- registry + verify ------------------------------------------------------------------------------------------------

    void BuildRegistry(List<string> regSpecs, string legacy, TextWriter log)
    {
        var reg = Path.Combine(_l.Additive, "mod-registry");
        Files.DeleteDir(reg);
        _c.P.Must(_additive, new[] { "registry", "--legacy", legacy, "--out", reg, "--ir-root", _l.IrRoot }.Concat(regSpecs), log, "registry: menu tables");
        // new locations: tile (DR2 loading art) and rally-plate logo textures under /Game/AcrXs/Locations/<Row>/
        var logoDir = Path.Combine(_l.Additive, "location-logos");
        Logos.Write(regSpecs, logoDir, log);
        Directory.CreateDirectory(_l.MenuArtLoading);
        _c.P.Must(_additive, new[]
        {
            "menu-locations", "--legacy", legacy, "--out", reg, "--art-dir", _l.MenuArtLoading, "--logo-dir", logoDir,
            "--preview", Path.Combine(_l.AcrWork, "menu-preview", "locations"),
        }.Concat(regSpecs), log, "registry: locations");
        Files.DeleteMatching(_l.Dist, "zz_acrxs_registry_P.*");
        _c.P.Must(_retoc, new[] { "to-zen", "--version", "UE5_6", reg, Path.Combine(_l.Dist, "zz_acrxs_registry_P.utoc") }, log, "registry: packing");
    }

    /// acr-additive verify over vanilla + the registry and the passed containers (the fast path); --roundtrip also converts
    /// the registry back to legacy and compares every table byte for byte.
    string VerifyAll(List<Row> passed, List<string> regSpecs, string vanilla, string paks, bool roundTrip, TextWriter log)
    {
        var check = Path.Combine(_l.Additive, "check");
        var full = Path.Combine(check, "acr", "Content", "Paks");
        Files.DeleteDir(check);
        Directory.CreateDirectory(full);
        foreach (var f in Directory.EnumerateFiles(paks)) Files.HardLink(Path.Combine(full, Path.GetFileName(f)), f);
        var mine = new List<string>(Directory.EnumerateFiles(_l.Dist, "zz_acrxs_registry_P.*"));
        foreach (var r in passed) mine.AddRange(Directory.EnumerateFiles(_l.Dist, $"zz_acrxs_s_{r.id.ToLowerInvariant()}_P.*"));
        foreach (var f in mine) Files.HardLink(Path.Combine(full, Path.GetFileName(f)), f);
        var specDir = Path.Combine(check, "specs");
        Directory.CreateDirectory(specDir);
        foreach (var s in regSpecs) File.Copy(s, Path.Combine(specDir, Path.GetFileName(s)), true);
        var outTxt = Path.Combine(check, "verify.txt");
        var v = _c.P.Run(_additive, new[] { "verify", "--vanilla", vanilla, "--game", check, "--spec", specDir, "--ir-root", _l.IrRoot, "--out", outTxt },
            log, "verifying the built stages");
        var last = File.Exists(outTxt) ? File.ReadLines(outTxt).LastOrDefault() ?? "" : "";
        if (v.Code != 0 && last.Length == 0) throw new StepFailed($"acr-additive verify failed ({v.Code})");
        var passes = File.Exists(outTxt) ? File.ReadLines(outTxt).Count(x => x.StartsWith("PASS")) : 0;
        var fails = File.Exists(outTxt) ? File.ReadLines(outTxt).Count(x => x.StartsWith("FAIL")) : 0;
        var result = $"{last} ({regSpecs.Count} specs, {passes} PASS / {fails} FAIL; registry round trip {(roundTrip ? "pending" : "skipped")})";
        if (!roundTrip || !last.StartsWith("ALL PASS")) return result;

        var rtPaks = Path.Combine(check, "roundtrip-paks");
        Directory.CreateDirectory(rtPaks);
        foreach (var f in Directory.EnumerateFiles(paks, "global.*").Concat(Directory.EnumerateFiles(_l.Dist, "zz_acrxs_registry_P.*")))
            File.Copy(f, Path.Combine(rtPaks, Path.GetFileName(f)), true);
        var rt = Path.Combine(check, "roundtrip");
        _c.P.Must(_retoc, new[] { "to-legacy", "--version", "UE5_6", "--no-shaders", rtPaks, rt }, log, "registry round trip");
        var reg = Path.Combine(_l.Additive, "mod-registry");
        var uexp = Directory.EnumerateFiles(reg, "*.uexp", SearchOption.AllDirectories).ToList();
        var diff = uexp.Where(f =>
        {
            var o = Path.Combine(rt, Path.GetRelativePath(reg, f));
            return !File.Exists(o) || Files.ContentHash(o) != Files.ContentHash(f);
        }).Select(Path.GetFileName).ToList();
        if (diff.Count > 0) throw new StepFailed($"registry round trip differs: {string.Join(", ", diff)}");
        return result.Replace("round trip pending", $"round trip: {uexp.Count} tables byte-identical");
    }

    // ---- report -----------------------------------------------------------------------------------------------------------

    void WriteReport(List<Row> rows, RegistryResult reg)
    {
        var sum = rows.GroupBy(r => r.status).ToDictionary(g => g.Key, g => g.Count());
        var gen = DateTime.Now.ToString("s");
        Files.WriteJson(Path.Combine(_l.Batch, "report.json"), new { generated = gen, selected = rows.Count, summary = sum, registry = reg, stages = rows });
        var sumText = string.Join(", ", sum.Select(kv => $"{kv.Value} {kv.Key}"));
        var md = new StringBuilder();
        md.AppendLine($"# Additive stage batch report\n\nGenerated {gen} by AcrExtraStages.exe. Selected {rows.Count}: {sumText}.\n");
        md.AppendLine($"Registry: {(reg.built ? $"built for {reg.ids.Count} stage(s)" : "NOT built")}; verify: {reg.verify}{(reg.error is null ? "" : "; " + reg.error)}\n");
        md.AppendLine("| Stage | DR2 id | Location | Name | km | Placement | World km2 | Status | MB | s | Pacenote tokens | Unmapped / notes |");
        md.AppendLine("|---|---|---|---|---:|---|---:|---|---:|---:|---|---|");
        foreach (var r in rows)
        {
            var notes = new[] { r.reason, r.pacenote_unmapped }.Concat(r.warnings).Where(x => !string.IsNullOrEmpty(x));
            md.AppendLine(string.Create(Inv, $"| {r.id} | {r.dr2} | {r.location} | {r.name} | {r.length_km} | {r.placement_mode} | {r.world_km2} | **{r.status}** | {r.container_mb} | {r.seconds} | {r.pacenote_tokens} | {string.Join("; ", notes).Replace("|", "/")} |"));
        }
        md.AppendLine("\nLogs: work\\acr\\batch\\logs\\<id>.log (prepare-<id>.log for the DR2 data, registry.log, launcher.log).");
        File.WriteAllText(Path.Combine(_l.Batch, "report.md"), md.ToString(), new UTF8Encoding(false));
        Ui.Line($"report -> {Path.Combine(_l.Batch, "report.md")}  ({sumText})");
    }
}
