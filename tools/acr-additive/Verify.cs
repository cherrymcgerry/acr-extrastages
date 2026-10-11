using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;

namespace AcrAdditive;

/// `verify --vanilla <dir> --game <dir with vanilla + built containers> --spec <stages dir> [--out file]`
/// Offline checks of the additive containers with CUE4Parse, the reader closest to the game's:
///  - the registry overrides exactly the four DB tables; every vanilla row reads back identical; the stage rows are present
///  - stage containers override nothing; their package ids are unique and match their paths; every hard import resolves
///  - each stage's level loads, its streaming cells and BakedData resolve, and every cell points back at that level
internal static class Verify
{
    static readonly string[] DbTables =
    [
        "/Game/Data/Database/Main/Common/DT_Levels",
        "/Game/Data/Database/Main/TrackSelection/DT_Tracks",
        "/Game/Data/Database/Main/TrackSelection/DT_TracksVariants",
        "/Game/Data/Database/Main/TrackSelection/DT_TracksLocations",
    ];

    public static int Run(Opts o)
    {
        var log = new List<string>();
        int fails = 0;
        void C(bool ok, string what) { if (!ok) fails++; var l = $"{(ok ? "PASS" : "FAIL")}  {what}"; log.Add(l); Console.WriteLine(l); }
        void I(string what) { log.Add($"      {what}"); Console.WriteLine($"      {what}"); }

        using var van = Game.Open(new Opts { Game = o.Named["vanilla"], Usmap = o.Usmap });
        using var mod = Game.Open(o);
        var specs = Directory.GetFiles(o.Named["spec"], "*.json").Select(StageSpec.Load).ToList();

        // ---- containers and package ids ----
        var vIndex = Game.PackageIndex(van);
        var mIndex = Game.PackageIndex(mod);
        var ours = mod.MountedVfs.OfType<IoStoreReader>().Where(r => r.Name.StartsWith("zz_acrdirt_", StringComparison.OrdinalIgnoreCase)).ToList();
        C(ours.Count >= 2, $"additive containers mounted: {string.Join(", ", ours.Select(r => r.Name))}");
        foreach (var r in ours)
        {
            var h = r.ContainerHeader!;
            var pkgs = h.PackageIds.Select(id => (id, name: r.PackageIdIndex.TryGetValue(id, out var f) ? Game.PackageName(f.Path) : "?")).ToList();
            var overrides = pkgs.Where(x => vIndex.ContainsKey(x.id.id)).Select(x => x.name).OrderBy(x => x).ToList();
            bool registry = r.Name.Contains("registry", StringComparison.OrdinalIgnoreCase);
            if (registry)
                C(overrides.SequenceEqual(DbTables.OrderBy(x => x)) && pkgs.Count == DbTables.Length,
                  $"{r.Name}: {pkgs.Count} packages, overrides exactly the DB tables ({string.Join(", ", overrides.Select(x => x[(x.LastIndexOf('/') + 1)..]))})");
            else
                C(overrides.Count == 0, $"{r.Name}: {pkgs.Count} packages, overrides {overrides.Count} vanilla packages{(overrides.Count > 0 ? ": " + string.Join(", ", overrides.Take(5)) : "")}");
            C(pkgs.All(x => FPackageId.FromName(x.name).id == x.id.id), $"{r.Name}: every package id = CityHash64(lowercase path)");
            C(pkgs.Select(x => x.id.id).Distinct().Count() == pkgs.Count, $"{r.Name}: package ids unique within the container");
            var others = mIndex.Where(kv => kv.Value.Count > 1 && kv.Value.Any(p => p.Container == r.Name) && !registry).ToList();
            C(others.Count == 0, $"{r.Name}: no package id shared with any other mounted container");
            var missing = new List<string>();
            for (int i = 0; i < h.PackageIds.Length; i++)
                foreach (var imp in h.StoreEntries[i].ImportedPackages ?? [])
                    if (!mIndex.ContainsKey(imp.id)) missing.Add($"{pkgs[i].name} -> {imp.id:X16}");
            C(missing.Count == 0, $"{r.Name}: all {h.StoreEntries.Sum(e => e.ImportedPackages?.Length ?? 0)} hard imports resolve{(missing.Count > 0 ? ": " + string.Join("; ", missing.Take(5)) : "")}");
        }

        // ---- database ----
        foreach (var t in DbTables)
        {
            var a = Rows(van, t);
            var b = Rows(mod, t);
            var changed = a.Keys.Where(k => !b.ContainsKey(k) || b[k] != a[k]).ToList();
            var added = b.Keys.Except(a.Keys).ToList();
            var expectChanged = t.EndsWith("DT_TracksLocations") ? specs.Where(s => s.Location.Template is null).Select(s => s.Location.Row).Distinct().ToList() : [];
            C(changed.OrderBy(x => x).SequenceEqual(expectChanged.OrderBy(x => x)),
              $"{t[(t.LastIndexOf('/') + 1)..]}: {a.Count} vanilla rows, {a.Count - changed.Count} identical{(changed.Count > 0 ? $", changed (expected: location track lists) {string.Join(", ", changed)}" : "")}; added {string.Join(", ", added)}");
            foreach (var k in changed)
            {
                // a changed location row must equal the vanilla row except for its Tracks list, which gains the stage tracks
                var va = Newtonsoft.Json.Linq.JObject.Parse(a[k]); var vb = Newtonsoft.Json.Linq.JObject.Parse(b[k]);
                var ta = (Newtonsoft.Json.Linq.JArray)va["Tracks"]!; var tb = (Newtonsoft.Json.Linq.JArray)vb["Tracks"]!;
                va.Remove("Tracks"); vb.Remove("Tracks");
                var extra = tb.Skip(ta.Count).Select(x => (string)x["Row"]!).ToList();
                C(Newtonsoft.Json.Linq.JToken.DeepEquals(va, vb) && Newtonsoft.Json.Linq.JToken.DeepEquals(ta, new Newtonsoft.Json.Linq.JArray(tb.Take(ta.Count))),
                  $"  {k}: only Tracks changed; vanilla tracks kept in order, appended {string.Join(", ", extra)}");
            }
            foreach (var k in added) I($"{k}: {Short(b[k])}");
        }
        foreach (var s in specs)
        {
            var tr = Rows(mod, DbTables[1]);
            var vr = Rows(mod, DbTables[2]);
            var lv = Rows(mod, DbTables[0]);
            C(tr.ContainsKey(s.Id) && lv.ContainsKey(s.Id) && s.Variants.All(v => vr.ContainsKey(v.Row)), $"{s.Id}: level, track and {s.Variants.Count} variant rows present");
            foreach (var v in s.Variants)
            {
                var j = Newtonsoft.Json.Linq.JObject.Parse(vr[v.Row]);
                var name = (string?)j["Name"]?["CultureInvariantString"] ?? (string?)j["Name"]?["SourceString"] ?? j["Name"]?.ToString(Formatting.None);
                C(name == v.Name && (v.Length is null || Math.Abs((float)j["Length"]! - v.Length.Value) < 1e-4)
                  && (string)j["Track"]!["Row"]! == s.Id,
                  $"  {v.Row}: Name '{name}', Length {(float)j["Length"]!}, Track {(string)j["Track"]!["Row"]!}");
            }
            var levelPath = (string)Newtonsoft.Json.Linq.JObject.Parse(lv[s.Id])["AssetPathName"]!;
            var subPath = (string)Newtonsoft.Json.Linq.JObject.Parse(lv[s.Id])["SubPathString"]!;
            var vanillaShape = Newtonsoft.Json.Linq.JObject.Parse(lv.Values.First())["SubPathString"]!.ToString();
            C(levelPath == $"{s.Level}.{s.Level[(s.Level.LastIndexOf('/') + 1)..]}" && subPath == vanillaShape,
              $"  DT_Levels[{s.Id}] -> {levelPath} (subpath '{subPath}', vanilla rows '{vanillaShape}')");
            CheckLevel(mod, van, s, C, I);
        }

        if (o.Named.TryGetValue("out", out var outFile)) File.WriteAllLines(outFile, log.Append(fails == 0 ? "ALL PASS" : $"{fails} FAILED"));
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails == 0 ? 0 : 4;
    }

    static string Short(string json) => json.Length > 400 ? json[..400] + "…" : json;

    static Dictionary<string, string> Rows(IFileProvider p, string table)
    {
        var dt = p.LoadPackage(table + ".uasset").GetExports().OfType<UDataTable>().Single();
        return dt.RowMap.ToDictionary(kv => kv.Key.Text, kv => JsonConvert.SerializeObject(
            kv.Value.Properties.Count == 1 && kv.Value.Properties[0].Name.Text == "Value" ? (object)kv.Value.Properties[0].Tag! : kv.Value, Formatting.None));
    }

    static void CheckLevel(IFileProvider mod, IFileProvider van, StageSpec s, Action<bool, string> C, Action<string> I)
    {
        var levelPkg = mod.LoadPackage(s.Level + ".umap");
        var exports = levelPkg.GetExports().ToList();
        var world = exports.SingleOrDefault(e => e.ExportType == "World");
        C(world is not null && world.Name == s.Level[(s.Level.LastIndexOf('/') + 1)..], $"{s.Id}: level loads ({exports.Count} exports), World object '{world?.Name}'");

        var streaming = exports.Where(e => e.ExportType == "WorldPartitionLevelStreamingDynamic").ToList();
        int okCells = 0, backOk = 0;
        var bad = new List<string>();
        foreach (var st in streaming)
        {
            var pkgName = st.GetOrDefault<FName>("PackageNameToLoad").Text;
            var worldAsset = st.GetOrDefault<FSoftObjectPath>("WorldAsset");
            if (!pkgName.StartsWith(s.Level + "/") || !mod.TryLoadPackage(pkgName + ".umap", out var cell)) { bad.Add(pkgName); continue; }
            if (worldAsset.AssetPathName.Text != $"{pkgName}.{world!.Name}") { bad.Add($"{pkgName} WorldAsset {worldAsset.AssetPathName}"); continue; }
            okCells++;
            var lvl = cell.GetExports().Single(e => e.ExportType == "Level");
            var back = lvl.GetOrDefault<FSoftObjectPath>("WorldPartitionRuntimeCell");
            var cellName = pkgName[(pkgName.LastIndexOf('/') + 1)..];
            if (back.AssetPathName.Text == $"{s.Level}.{world.Name}" && back.SubPathString.EndsWith("." + cellName)
                && exports.Any(e => e.Name == cellName && e.ExportType == "WorldPartitionRuntimeLevelStreamingCell")) backOk++;
            else bad.Add($"{pkgName} runtime cell {back.AssetPathName}:{back.SubPathString}");
        }
        C(streaming.Count > 0 && okCells == streaming.Count, $"  {streaming.Count} streaming cells: {okCells} load from the stage folder with matching WorldAsset{(bad.Count > 0 ? "; bad: " + string.Join("; ", bad.Take(4)) : "")}");
        C(backOk == streaming.Count, $"  {backOk}/{streaming.Count} cells point back at this level's runtime cell objects");

        var avatar = exports.First(e => e.ExportType.Contains("TrackAvatar") && e.Properties.Any(p => p.Name.Text == "BakedData"));
        var baked = avatar.GetOrDefault<FSoftObjectPath>("BakedData");
        var bakedPkg = baked.AssetPathName.Text[..baked.AssetPathName.Text.LastIndexOf('.')];
        bool bakedExists = mod.TryGetGameFile(bakedPkg + ".uasset", out _);
        C(bakedExists, $"  TrackAvatar.BakedData -> {baked.AssetPathName} ({(bakedExists ? (van.TryGetGameFile(bakedPkg + ".uasset", out _) ? "vanilla package" : "stage package") : "MISSING")})");

        // every other soft/hard reference that still points into vanilla content is fine; references to the source level are not
        var leftovers = levelPkg is CUE4Parse.UE4.Assets.IoPackage lp ? lp.NameMap.Select(n => n.Name).Where(n => n is not null && n.StartsWith("/Game/Levels/")).Cast<string>().ToList() : [];
        I($"level name map entries still under /Game/Levels: {string.Join(", ", leftovers.Take(8))}{(leftovers.Count > 8 ? " …" : "")}");
    }
}
