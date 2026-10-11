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
        var wanted = ir.Materials.Values.Select(a => a[(a.LastIndexOf('/') + 1)..]).Distinct().ToList();
        var missing = wanted.Where(m => !materials.ContainsKey(m)).ToList();
        C(missing.Count == 0 && !materials.ContainsKey("null"),
            $"every collider's physical material resolves; DR2 materials present: {string.Join(", ", wanted)}{(missing.Count > 0 ? $"; MISSING {string.Join(", ", missing)}" : "")}");

        var cell = p.LoadPackage("acr/Content/Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP.umap").GetExports().ToList();
        var centre = JToken.Parse(JsonConvert.SerializeObject(cell.Single(e => e.Name == "CenterSpline")));
        int pts = centre.SelectToken("Properties.SplineCurves.Position.Points")?.Count() ?? 0;
        var reparam = centre.SelectToken("Properties.SplineCurves.ReparamTable.Points") as JArray;
        double len = reparam?.Last?["InVal"]?.Value<double>() / 100 ?? 0;
        C(pts == ir.Centre.Count, $"CenterSpline via CUE4Parse: {pts} points, {len:0.#} m (HUD route length; DR2 total {ir.TotalM:0.#} m)");
        foreach (var name in new[] { "RaceSector", "BC_StartSequenceTrigger_C", "BC_EndSequenceTrigger_C", "BC_CompleStartTargetPoint_C" })
            foreach (var a in cell.Where(e => e.ExportType == name))
                foreach (var comp in cell.Where(e => e.Outer?.Name.ToString() == a.Name && e.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector _, "RelativeLocation")))
                {
                    var v = comp.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeLocation");
                    w.WriteLine($"     {name}.{comp.Name}: ({v.X:0}, {v.Y:0}, {v.Z:0})");
                }

        var pace = p.LoadPackage("acr/Content/Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward.uasset").GetExports().OfType<UDataTable>().Single();
        C(true, $"DT_PacenoteHafrenSouthForward via CUE4Parse: {pace.RowMap.Count} rows");

        var variants = p.LoadPackage("acr/Content/Data/Database/Main/TrackSelection/DT_TracksVariants.uasset").GetExports().OfType<UDataTable>().Single();
        var hafren = variants.RowMap.Where(kv => JsonConvert.SerializeObject(kv.Value).Contains("HafrenSouth")).ToList();
        foreach (var (k, v) in hafren)
        {
            var length = v.GetOrDefault<float>("Length");
            w.WriteLine($"     DT_TracksVariants {k.Text}: Length {length}");
        }
        C(hafren.Any(kv => Math.Abs(kv.Value.GetOrDefault<float>("Length") - Math.Round(ir.LengthM / 1000, 3)) < 1e-3),
            $"a Hafren South variant has Length {Math.Round(ir.LengthM / 1000, 3)} km");

        w.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
        return failures == 0 ? 0 : 5;
    }
}
