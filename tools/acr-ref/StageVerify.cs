using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcrRef;

/// `stage-verify --game <dir with global.* + the built container> --ir <dir> [--out file]`: reads the four overridden
/// packages back through CUE4Parse, the same reader that loads the game's own data, and prints what the game will see.
internal static class StageVerify
{
    public static int Run(Options o)
    {
        var ir = StageBuild.Ir.Load(o.Named.GetValueOrDefault("ir") ?? throw new ArgumentException("--ir is required"));
        using var w = o.Out is null ? Console.Out : new StreamWriter(o.Out);
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }

        using var p = Program.Open(o);
        var mounted = p.MountedVfs.Select(v => v.Name).ToList();
        C(mounted.Any(n => n.Contains("zz_acrdirt", StringComparison.OrdinalIgnoreCase)), $"container mounted ({string.Join(", ", mounted.Where(n => n.Contains("zz_acrdirt", StringComparison.OrdinalIgnoreCase)))} among {mounted.Count} containers)");

        var baked = p.LoadPackage("acr/Content/Environments/WalesS4HafrenSouth/DataAssets/DA_WalesS4HafrenSouthBakedData.uasset").GetExports().First();
        var arr = baked.GetOrDefault<UScriptArray>("BakedMeshCollisionData");
        long tris = 0;
        var materials = new Dictionary<string, long>();
        foreach (var el in arr.Properties)
        {
            if (el is not StructProperty { Value.StructType: FStructFallback s }) continue;
            long n = s.GetOrDefault<UScriptArray>("Indices")?.Properties.Count / 3 ?? 0;
            tris += n;
            var tag = s.Properties.FirstOrDefault(t => t.Name.Text == "PhysMaterial")?.Tag;
            var json = tag is null ? "null" : JsonConvert.SerializeObject(tag);
            var m = System.Text.RegularExpressions.Regex.Match(json, @"PM_\w+");
            var key = m.Success ? m.Value : json;
            materials[key] = materials.GetValueOrDefault(key) + n;
        }
        C(arr.Properties.Count > 734, $"BakedData via CUE4Parse: {arr.Properties.Count} mesh colliders, {tris} triangles");
        foreach (var (k, v) in materials.OrderByDescending(kv => kv.Value)) w.WriteLine($"     {k}: {v} triangles");
        var registryJson = JsonConvert.SerializeObject(p.LoadPackage("acr/Content/Data/Gameplay/PhysicalMaterials/DA_PhysMaterialsMappingCommon.uasset").GetExports());
        var registered = System.Text.RegularExpressions.Regex.Matches(registryJson, @"PhysicalMaterials/(PM_\w+)\.\d+").Select(m => m.Groups[1].Value).ToHashSet();
        var unregistered = materials.Keys.Where(k => !registered.Contains(k)).ToList();
        C(registered.Count >= 10 && unregistered.Count == 0,
          $"every mesh collider material is a registered surface ({registered.Count} in DA_PhysMaterialsMappingCommon){(unregistered.Count > 0 ? $"; UNREGISTERED {string.Join(", ", unregistered)}" : "")}");
        var wanted = ir.Materials.Values.Select(a => a[(a.LastIndexOf('/') + 1)..]).Where(registered.Contains).Distinct().ToList();
        var missing = wanted.Where(m => !materials.ContainsKey(m)).ToList();
        C(missing.Count == 0 && !materials.ContainsKey("null"),
            $"every collider's physical material resolves; DR2 materials present: {string.Join(", ", wanted)}{(missing.Count > 0 ? $"; MISSING {string.Join(", ", missing)}" : "")}");

        var simple = baked.GetOrDefault<UScriptArray>("BakedSimpleCollisionData");
        if (simple is not null)
        {
            var simpleMats = new Dictionary<string, int>();
            int boxes = 0, caps = 0;
            foreach (var el in simple.Properties.Skip(15326))
            {
                if (el is not StructProperty { Value.StructType: FStructFallback s }) continue;
                boxes += s.GetOrDefault<UScriptArray>("Boxes")?.Properties.Count ?? 0;
                caps += s.GetOrDefault<UScriptArray>("Capsules")?.Properties.Count ?? 0;
                var tag = s.Properties.FirstOrDefault(t => t.Name.Text == "PhysMaterial")?.Tag;
                var m = System.Text.RegularExpressions.Regex.Match(tag is null ? "null" : JsonConvert.SerializeObject(tag), @"PM_\w+");
                var key = m.Success ? m.Value : "null";
                simpleMats[key] = simpleMats.GetValueOrDefault(key) + 1;
            }
            int added = simple.Properties.Count - 15326;
            var simpleUnreg = simpleMats.Keys.Where(k => !registered.Contains(k)).ToList();
            C(simpleUnreg.Count == 0, $"every added simple collider material is a registered surface{(simpleUnreg.Count > 0 ? $"; UNREGISTERED {string.Join(", ", simpleUnreg)}" : "")}");
            C(added >= 0 && boxes + caps == added && !simpleMats.ContainsKey("null"),
              $"BakedSimpleCollisionData via CUE4Parse: {simple.Properties.Count} entries = 15326 host + {added} added ({boxes} boxes, {caps} capsules), all materials resolve");
            foreach (var (k, v) in simpleMats.OrderByDescending(kv => kv.Value)) w.WriteLine($"     {k}: {v} simple colliders");
            if (added > 0)
                w.WriteLine($"     first added entry: {JsonConvert.SerializeObject(simple.Properties[15326]).Replace("\n", "")[..Math.Min(600, JsonConvert.SerializeObject(simple.Properties[15326]).Length)]}");
        }

        var cell = p.LoadPackage("acr/Content/Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP.umap").GetExports().ToList();
        var walls = cell.Where(e => e.ExportType == "SplineMeshComponent").ToList();
        int moved = walls.Count(wl => wl.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector _, "RelativeLocation"));
        int hidden = walls.Count(wl => wl.GetOrDefault<bool>("bHiddenInGame"));
        int withParams = walls.Count(wl => wl.TryGetValue(out FStructFallback _, "SplineParams"));
        int placed = walls.Count(wl => wl.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector s, "RelativeScale3D") && s.Z > 1.01);
        int rotated = walls.Count(wl => wl.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FRotator r, "RelativeRotation") && Math.Abs(r.Yaw) > 1e-3);
        C(hidden == walls.Count && withParams == walls.Count,
          $"respawn walls via CUE4Parse: {walls.Count} SplineMeshComponents, {moved} with a RelativeLocation, {placed} scaled along the DR2 contour ({rotated} rotated), {moved - placed} sunk; all still hidden with SplineParams");
        var centre = JToken.Parse(JsonConvert.SerializeObject(cell.Single(e => e.Name == "CenterSpline")));
        int pts = centre.SelectToken("Properties.SplineCurves.Position.Points")?.Count() ?? 0;
        var reparam = centre.SelectToken("Properties.SplineCurves.ReparamTable.Points") as JArray;
        double len = reparam?.Last?["InVal"]?.Value<double>() / 100 ?? 0;
        C(pts == ir.Centre.Count, $"CenterSpline via CUE4Parse: {pts} points, {len:0.#} m (HUD route length; DR2 total {ir.TotalM:0.#} m)");
        var startMesh = cell.Where(e => e.ExportType == "ProceduralMeshComponent")
            .MaxBy(e => e.GetOrDefault<UScriptArray>("ProcMeshSections")?.Properties.Count ?? 0)!;
        var secs = startMesh.GetOrDefault<UScriptArray>("ProcMeshSections");
        var mats = startMesh.GetOrDefault<UScriptArray>("OverrideMaterials");
        var matJson = mats is null ? "" : JsonConvert.SerializeObject(mats);
        if (secs is not null && secs.Properties.Count > 1)
        {
            var counts = secs.Properties.OfType<StructProperty>().Select(sp => (sp.Value?.StructType as FStructFallback)?.GetOrDefault<UScriptArray>("ProcVertexBuffer")?.Properties.Count ?? 0).ToList();
            C(counts.Count >= 5 && counts.All(c => c > 0) && mats?.Properties.Count == counts.Count && !startMesh.GetOrDefault<bool>("bHiddenInGame"),
              $"visuals via CUE4Parse: mesh of {startMesh.Outer?.Name} {counts.Count} sections ({string.Join(", ", counts)} vertices), {mats?.Properties.Count ?? 0} material slots ({string.Join(", ", System.Text.RegularExpressions.Regex.Matches(matJson, @"MI_\w+").Select(m => m.Value).Distinct())}), not hidden in game");
            foreach (var trig in cell.Where(e => e.ExportType is "BC_StartSequenceTrigger_C" or "BC_EndSequenceTrigger_C"))
            {
                var mc = trig.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("MeshComponent");
                var target = mc?.ResolvedObject;
                w.WriteLine($"     {trig.ExportType}.MeshComponent -> {target?.Outer?.Name}.{target?.Name} ({(target?.Name.ToString() == startMesh.Name && target?.Outer?.Name.ToString() == startMesh.Outer?.Name.ToString() ? "the visuals mesh" : "a ring")})");
            }
        }
        foreach (var name in new[] { "RaceSector", "BC_StartSequenceTrigger_C", "BC_EndSequenceTrigger_C", "BC_CompleStartTargetPoint_C" })
            foreach (var a in cell.Where(e => e.ExportType == name))
                foreach (var comp in cell.Where(e => e.Outer?.Name.ToString() == a.Name && e.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector _, "RelativeLocation")))
                {
                    var v = comp.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeLocation");
                    w.WriteLine($"     {name}.{comp.Name}: ({v.X:0}, {v.Y:0}, {v.Z:0})");
                }

        var pace = p.LoadPackage("acr/Content/Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward.uasset").GetExports().OfType<UDataTable>().Single();
        var tokenTable = p.LoadPackage("acr/Content/Data/Pacenote/Config/DT_PacenoteTokens_EA.uasset").GetExports().OfType<UDataTable>().Single();
        var known = tokenTable.RowMap.Keys.Select(k => k.Text).ToHashSet();
        var used = new List<string>();
        foreach (var (_, row) in pace.RowMap)
            if (row.TryGetValue(out FStructFallback tl, "TokenList"))
                used.AddRange(tl.GetOrDefault<UScriptArray>("Tokens")?.Properties.Select(t => t.GenericValue?.ToString() ?? "") ?? []);
        var unknown = used.Where(t => !known.Contains(t)).Distinct().ToList();
        C(unknown.Count == 0, $"DT_PacenoteHafrenSouthForward via CUE4Parse: {pace.RowMap.Count} rows, {used.Count} tokens, all in DT_PacenoteTokens_EA ({known.Count} tokens){(unknown.Count > 0 ? $"; UNKNOWN {string.Join(", ", unknown)}" : "")}");
        foreach (var (k, row) in pace.RowMap.Take(3).Concat(pace.RowMap.TakeLast(2)))
            w.WriteLine($"     {k.Text}: {row.GetOrDefault<float>("SplineDistanceM"):0.#} m, L {row.GetOrDefault<float>("LengthM"):0.#}, link {row.GetOrDefault<bool>("LinkToNext")}, " +
                        $"{string.Join(' ', row.GetOrDefault<FStructFallback>("TokenList")?.GetOrDefault<UScriptArray>("Tokens")?.Properties.Select(t => t.GenericValue?.ToString()) ?? [])}");

        var variants = p.LoadPackage("acr/Content/Data/Database/Main/TrackSelection/DT_TracksVariants.uasset").GetExports().OfType<UDataTable>().Single();
        var hafren = variants.RowMap.Where(kv => JsonConvert.SerializeObject(kv.Value).Contains("HafrenSouth")).ToList();
        foreach (var (k, v) in hafren)
        {
            var length = v.GetOrDefault<float>("Length");
            w.WriteLine($"     DT_TracksVariants {k.Text}: Length {length}");
        }
        C(hafren.Any(kv => Math.Abs(kv.Value.GetOrDefault<float>("Length") - Math.Round(ir.LengthM / 1000, 3)) < 1e-3),
            $"a Hafren South variant has Length {Math.Round(ir.LengthM / 1000, 3)} km");

        if (o.Named.GetValueOrDefault("sky") is { } skyCfg)
        {
            var cfg = Toml.Load(skyCfg);
            var level = p.LoadPackage("acr/Content/Levels/WalesS4HafrenSouth.umap").GetExports();
            var sky = level.Single(x => x.Name.StartsWith("BC_DMSky_Candidate_C_") && x.Outer?.Name == "PersistentLevel");
            var got = new[] { "Latitude", "Longitude", "Time Zone" }.Select(f => (f, v: sky.GetOrDefault<double>(f)))
                .Concat(new[] { "Year", "Month", "Day" }.Select(f => (f, v: (double)sky.GetOrDefault<int>(f)))).ToList();
            bool ok = got.All(g => Math.Abs(g.v - cfg.Num("nz", g.f.Replace(" ", "_").ToLowerInvariant())) < 1e-9)
                      && Math.Abs(sky.GetOrDefault<double>("Time of Day") - 1559.36) < 0.01;
            C(ok, $"sky via CUE4Parse: {sky.Name}: {string.Join(", ", got.Select(g => $"{g.f} {g.v}"))}, Time of Day {sky.GetOrDefault<double>("Time of Day"):0.##} (unchanged)");
        }

        w.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
        return failures == 0 ? 0 : 5;
    }
}
