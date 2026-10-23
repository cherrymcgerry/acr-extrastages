using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Texture;
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
    static readonly string[] BaseTables =
    [
        "/Game/Data/Database/Main/Common/DT_Levels",
        "/Game/Data/Database/Main/TrackSelection/DT_Tracks",
        "/Game/Data/Database/Main/TrackSelection/DT_TracksVariants",
        "/Game/Data/Database/Main/TrackSelection/DT_TracksLocations",
    ];
    const string TexturesTable = "/Game/Data/Database/Main/TrackSelection/DT_TracksTextures";

    public static int Run(Opts o)
    {
        var log = new List<string>();
        int fails = 0;
        void C(bool ok, string what) { if (!ok) fails++; var l = $"{(ok ? "PASS" : "FAIL")}  {what}"; log.Add(l); Console.WriteLine(l); }
        void I(string what) { log.Add($"      {what}"); Console.WriteLine($"      {what}"); }

        using var van = Game.Open(new Opts { Game = o.Named["vanilla"], Usmap = o.Usmap });
        using var mod = Game.Open(o);
        var irRoot = o.Named.GetValueOrDefault("ir-root");   // stage display names come from the DR2 localisation in the IR (StageSpec.ResolveNames)
        var specs = Directory.GetFiles(o.Named["spec"], "*.json").Select(p => StageSpec.Load(p, irRoot)).ToList();
        // DT_TracksTextures / DT_TracksLocationsBackgrounds / DT_TracksLocationsLogos join the overridden tables only when stages or
        // new locations have generated images (append-only, like the others)
        var DbTables = Registry.AllTables(specs).Select(x => "/Game/Data/Database/Main/" + x.file).ToArray();
        var newLocations = MenuLocations.NewLocations(specs);
        var locationPackages = newLocations.SelectMany(l => MenuLocations.For(l).Select(i => MenuLocations.Package(l.Row, i.Kind))).OrderBy(x => x).ToList();

        // ---- containers and package ids ----
        var vIndex = Game.PackageIndex(van);
        var mIndex = Game.PackageIndex(mod);
        var ours = mod.MountedVfs.OfType<IoStoreReader>().Where(r => r.Name.StartsWith("zz_acrxs_", StringComparison.OrdinalIgnoreCase)).ToList();
        C(ours.Count >= 2, $"additive containers mounted: {string.Join(", ", ours.Select(r => r.Name))}");
        foreach (var r in ours)
        {
            var h = r.ContainerHeader!;
            var pkgs = h.PackageIds.Select(id => (id, name: r.PackageIdIndex.TryGetValue(id, out var f) ? Game.PackageName(f.Path) : "?")).ToList();
            var overrides = pkgs.Where(x => vIndex.ContainsKey(x.id.id)).Select(x => x.name).OrderBy(x => x).ToList();
            bool registry = r.Name.Contains("registry", StringComparison.OrdinalIgnoreCase);
            if (registry)
            {
                C(overrides.SequenceEqual(DbTables.OrderBy(x => x)),
                  $"{r.Name}: {pkgs.Count} packages, overrides exactly the DB tables ({string.Join(", ", overrides.Select(x => x[(x.LastIndexOf('/') + 1)..]))})");
                var own = pkgs.Where(x => !vIndex.ContainsKey(x.id.id)).Select(x => x.name).OrderBy(x => x).ToList();
                C(own.SequenceEqual(locationPackages), $"{r.Name}: other packages are exactly the {locationPackages.Count} location tiles / logos{(own.SequenceEqual(locationPackages) ? "" : ": " + string.Join(", ", own.Except(locationPackages).Concat(locationPackages.Except(own)).Take(5)))}");
            }
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
            var expectChanged = t.EndsWith("DT_TracksLocations") ? specs.Select(s => s.Location.Row).Distinct().Where(a.ContainsKey).ToList() : [];   // every spec location row that exists in vanilla gains its stage tracks (also ones with a Template, e.g. Greece)
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
            if (s.Menu is not null) CheckMenu(mod, van, mIndex, s, tr, vr, C, I);
            if (s.Location.Surface is { } surface)
            {
                var src = Newtonsoft.Json.Linq.JObject.Parse(Rows(van, DbTables[2])[MenuLocations.Surfaces[surface].variant]);
                foreach (var v in s.Variants)
                {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(vr[v.Row]);
                    C(MenuLocations.VariantSurfaceFields.All(f => Newtonsoft.Json.Linq.JToken.DeepEquals(j[f], src[f])),
                      $"  {v.Row}: {surface} surfaces / tyres as {MenuLocations.Surfaces[surface].variant} ({j["TireCompounds"]?["Row"]})");
                }
            }
        }
        foreach (var l in newLocations) CheckLocation(mod, van, mIndex, l, DbTables[3], C);

        if (o.Named.TryGetValue("out", out var outFile)) File.WriteAllLines(outFile, log.Append(fails == 0 ? "ALL PASS" : $"{fails} FAILED"));
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails == 0 ? 0 : 4;
    }

    /// Menu block: DT_TracksTextures rows -> the stage container's UI textures (present, not vanilla, Texture2D of the template's
    /// size and format), the track / variant rows point at those rows, and the sector lengths add up to the stage length.
    static void CheckMenu(DefaultFileProvider mod, DefaultFileProvider van, Dictionary<ulong, List<Game.Pkg>> mIndex, StageSpec s,
        Dictionary<string, string> tr, Dictionary<string, string> vr, Action<bool, string> C, Action<string> I)
    {
        string Ref(string json, string field) => (string)Newtonsoft.Json.Linq.JObject.Parse(json)[field]!["Row"]!;
        if (s.Menu!.Images)
        {
            var tex = Rows(mod, TexturesTable);
            var container = $"zz_acrxs_s_{s.Id.ToLowerInvariant()}_P";
            foreach (var img in Menu.For(s))
            {
                var key = Menu.TextureRow(s.Id, img.Kind);
                var asset = tex.TryGetValue(key, out var j) ? (string)Newtonsoft.Json.Linq.JObject.Parse(j)["AssetPathName"]! : null;
                var pkg = Menu.Package(s.Id, img.Kind);
                var owners = mIndex.TryGetValue(FPackageId.FromName(pkg).id, out var l) ? l.Select(x => x.Container).ToList() : [];
                string what = "missing";
                bool ok = asset == Menu.ObjectPath(s.Id, img.Kind) && owners.Count == 1 && owners[0].StartsWith(container, StringComparison.OrdinalIgnoreCase)
                          && !van.TryGetGameFile(pkg + ".uasset", out _);
                if (ok && mod.TryLoadPackage(pkg + ".uasset", out var p) && p.GetExports().OfType<UTexture2D>().SingleOrDefault() is { } t2d)
                {
                    var pd = t2d.PlatformData;
                    what = $"{pd.PixelFormat} {pd.SizeX}x{pd.SizeY}, {pd.Mips.Length} mip, {pd.Mips[0].BulkData?.Data?.Length ?? 0} bytes";
                    ok = pd.PixelFormat.ToString() == img.Format && pd.SizeX == img.W && pd.SizeY == img.H && pd.Mips[0].BulkData?.Data?.Length == img.Bytes
                         && t2d.Name == pkg[(pkg.LastIndexOf('/') + 1)..];
                }
                else ok = false;
                C(ok, $"  menu {img.Kind}: DT_TracksTextures[{key}] -> {asset} in {string.Join(",", owners)}: {what}");
            }
            bool art = s.Menu.Art is not null;
            string Want(string kind, string vanillaRef) => art ? Menu.TextureRow(s.Id, kind) : vanillaRef;
            var tTemplate = Rows(van, BaseTables[1])[s.Track.Template];
            C(Ref(tr[s.Id], "Map") == Menu.TextureRow(s.Id, "Map")
              && Ref(tr[s.Id], "TrackImage") == Want("Backdrop", Ref(tTemplate, "TrackImage"))
              && Ref(tr[s.Id], "LobbyTrackImage") == Want("Backdrop", Ref(tTemplate, "LobbyTrackImage")),
              $"  DT_Tracks[{s.Id}]: Map -> {Ref(tr[s.Id], "Map")}, TrackImage -> {Ref(tr[s.Id], "TrackImage")}, LobbyTrackImage -> {Ref(tr[s.Id], "LobbyTrackImage")}");
            var vRows = Rows(van, BaseTables[2]);
            foreach (var v in s.Variants)
                C(Ref(vr[v.Row], "MapSmall") == Menu.TextureRow(s.Id, "MapSmall") && Ref(vr[v.Row], "ElevationMap") == Menu.TextureRow(s.Id, "Elevation")
                  && Ref(vr[v.Row], "VariantImage") == Want("Banner", Ref(vRows[v.Template], "VariantImage"))
                  && Ref(vr[v.Row], "LobbyVariantImage") == Want("Lobby", Ref(vRows[v.Template], "LobbyVariantImage")),
                  $"  {v.Row}: MapSmall -> {Ref(vr[v.Row], "MapSmall")}, ElevationMap -> {Ref(vr[v.Row], "ElevationMap")}, " +
                  $"VariantImage -> {Ref(vr[v.Row], "VariantImage")}, LobbyVariantImage -> {Ref(vr[v.Row], "LobbyVariantImage")}");
        }
        foreach (var v in s.Variants)
        {
            var j = Newtonsoft.Json.Linq.JObject.Parse(vr[v.Row]);
            var km = ((Newtonsoft.Json.Linq.JArray)j["Sectors"]!).Select(x => (double)x["Length"]!).ToList();
            C(km.Count == 3 && Math.Abs(km.Sum() - (double)j["Length"]!) < 0.16,
              $"  {v.Row}: sectors {string.Join(" + ", km.Select(x => x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))} km vs Length {(double)j["Length"]!:0.000}; elevation {j["Elevation"]?["X"]}..{j["Elevation"]?["Y"]}, start {j["ElevationAtStart"]}");
        }
    }

    /// A location the registry created: nation, tile and logo (a vanilla row, or a generated texture in the registry container and
    /// not in vanilla), surface data as the vanilla location of that surface.
    static void CheckLocation(DefaultFileProvider mod, DefaultFileProvider van, Dictionary<ulong, List<Game.Pkg>> mIndex, StageSpec.LocationSpec l,
        string locationsTable, Action<bool, string> C)
    {
        var loc = Newtonsoft.Json.Linq.JObject.Parse(Rows(mod, locationsTable)[l.Row]);
        if (l.Country is not null)
            C((string)loc["Country"]!["Row"]! == l.Country && Rows(van, "/Game/Data/Database/Main/Common/DT_Countries").ContainsKey(l.Country),
              $"  location {l.Row}: Country -> {loc["Country"]!["Row"]} (vanilla row)");
        var generated = MenuLocations.For(l).ToList();
        foreach (var img in MenuLocations.All)
        {
            var table = "/Game/Data/Database/Main/" + img.File;
            var got = (string)loc[img.Field]!["Row"]!;
            var vanillaRow = img == MenuLocations.Tile ? l.Tile : l.Logo;
            if (!generated.Contains(img))
            {
                if (vanillaRow is not null)
                    C(got == vanillaRow && Rows(van, table).ContainsKey(vanillaRow), $"  location {l.Row}.{img.Field} -> {img.Table}[{got}] (vanilla row)");
                continue;
            }
            var asset = Rows(mod, table).TryGetValue(got, out var j) ? (string)Newtonsoft.Json.Linq.JObject.Parse(j)["AssetPathName"]! : null;
            var pkg = MenuLocations.Package(l.Row, img.Kind);
            var owners = mIndex.TryGetValue(FPackageId.FromName(pkg).id, out var o) ? o.Select(x => x.Container).ToList() : [];
            string what = "missing";
            bool ok = got == MenuLocations.Row(l.Row) && asset == MenuLocations.ObjectPath(l.Row, img.Kind) && owners.Count == 1
                      && owners[0].StartsWith("zz_acrxs_registry", StringComparison.OrdinalIgnoreCase) && !van.TryGetGameFile(pkg + ".uasset", out _);
            if (ok && mod.TryLoadPackage(pkg + ".uasset", out var p) && p.GetExports().OfType<UTexture2D>().SingleOrDefault() is { } t2d)
            {
                var pd = t2d.PlatformData;
                what = $"{pd.PixelFormat} {pd.SizeX}x{pd.SizeY}, {pd.Mips[0].BulkData?.Data?.Length ?? 0} bytes";
                ok = pd.PixelFormat.ToString() == "PF_B8G8R8A8" && pd.SizeX == img.W && pd.SizeY == img.H && pd.Mips[0].BulkData?.Data?.Length == img.W * img.H * 4;
            }
            else ok = false;
            C(ok, $"  location {l.Row}.{img.Field} -> {img.Table}[{got}] -> {asset} in {string.Join(",", owners)}: {what}");
        }
        if (l.Surface is { } surface)
        {
            var src = Newtonsoft.Json.Linq.JObject.Parse(Rows(van, locationsTable)[MenuLocations.Surfaces[surface].location]);
            C(Newtonsoft.Json.Linq.JToken.DeepEquals(loc["SurfaceTypesData"], src["SurfaceTypesData"]),
              $"  location {l.Row}: SurfaceTypesData {surface} as {MenuLocations.Surfaces[surface].location}");
        }
    }

    static string Short(string json) => json.Length > 400 ? json[..400] + "…" : json;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IFileProvider, Dictionary<string, Dictionary<string, string>>> RowCache = new();

    static Dictionary<string, string> Rows(IFileProvider p, string table)
    {
        var cache = RowCache.GetOrCreateValue(p);
        if (cache.TryGetValue(table, out var hit)) return hit;
        var dt = p.LoadPackage(table + ".uasset").GetExports().OfType<UDataTable>().Single();
        return cache[table] = dt.RowMap.ToDictionary(kv => kv.Key.Text, kv => JsonConvert.SerializeObject(
            kv.Value.Properties.Count == 1 && kv.Value.Properties[0].Name.Text == "Value" ? (object)kv.Value.Properties[0].Tag! : kv.Value, Formatting.None));
    }

    /// The cell's single Level export, deserializing only that one: a full GetExports() also decodes the cell's actors, and CUE4Parse
    /// needs minutes for one ProceduralMeshComponent of a road cell (its vertex arrays are tagged structs).
    static UObject LevelExport(CUE4Parse.UE4.Assets.IPackage cell)
    {
        if (cell is not CUE4Parse.UE4.Assets.IoPackage io) return cell.GetExports().Single(e => e.ExportType == "Level");
        return Enumerable.Range(0, io.ExportMap.Length)
            .Where(i => io.ExportMap[i].ClassIndex.IsScriptImport && io.ResolveObjectIndex(io.ExportMap[i].ClassIndex)?.Name.Text == "Level")
            .Select(i => io.ExportsLazy[i].Value).Single();
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
            var lvl = LevelExport(cell);
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
