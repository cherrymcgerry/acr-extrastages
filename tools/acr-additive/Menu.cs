using System.Text.RegularExpressions;
using AcrRef;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Newtonsoft.Json.Linq;

namespace AcrAdditive;

/// Menu images of the track selection screens (docs/menu-images.md).
internal static class Menu
{
    const string Db = "/Game/Data/Database/Main/";

    /// Texture tables a track / variant / location row points into, by row field.
    public static readonly (string table, string field, string texTable)[] ImageFields =
    [
        ("TracksLocations", "Logo", "TracksLocationsLogos"),
        ("TracksLocations", "BackgroundImage", "TracksLocationsBackgrounds"),
        ("Tracks", "Map", "TracksTextures"),
        ("Tracks", "TrackImage", "TracksTextures"),
        ("Tracks", "LobbyTrackImage", "TracksTextures"),
        ("TracksVariants", "VariantImage", "TracksTextures"),
        ("TracksVariants", "LobbyVariantImage", "TracksTextures"),
        ("TracksVariants", "MapSmall", "TracksTextures"),
        ("TracksVariants", "ElevationMap", "TracksTextures"),
    ];

    public static readonly Dictionary<string, string> TableFiles = new()
    {
        ["TracksLocations"] = "TrackSelection/DT_TracksLocations",
        ["Tracks"] = "TrackSelection/DT_Tracks",
        ["TracksVariants"] = "TrackSelection/DT_TracksVariants",
        ["TracksTextures"] = "TrackSelection/DT_TracksTextures",
        ["TracksLocationsLogos"] = "TrackSelection/DT_TracksLocationsLogos",
        ["TracksLocationsBackgrounds"] = "TrackSelection/DT_TracksLocationsBackgrounds",
        ["CountryFlags"] = "Common/DT_CountryFlags",
    };

    /// A generated image of a stage with Menu.Images, cloned from a vanilla UI texture of the same size and pixel format.
    /// Art images (cropped from the DR2 loading art, Menu.Art) are only made when the spec names one.
    public sealed record Image(string Kind, string Template, int W, int H, string Format, bool Art)
    {
        public int Bytes => Format == "PF_BC7" ? W * H : W * H * 4;
    }

    public static readonly Image[] Images =
    [
        new("Map", "/Game/Data/UITextures/Tracks/Tracks/Wales/HafrenNorth/T_HafrenNorthFullForward", 800, 800, "PF_B8G8R8A8", false),        // DT_Tracks.Map
        new("MapSmall", "/Game/Data/UITextures/Tracks/Tracks/Wales/HafrenNorth/T_HafrenNorthFullForward", 800, 800, "PF_B8G8R8A8", false),   // variant MapSmall
        new("Elevation", "/Game/Data/UITextures/Tracks/Tracks/Wales/T_HafrenSouthAltimetry", 306, 142, "PF_B8G8R8A8", false),               // variant ElevationMap
        new("Backdrop", "/Game/Data/UITextures/Tracks/T_HafrenSouthBackground", 1325, 922, "PF_B8G8R8A8", true),                            // DT_Tracks.TrackImage + LobbyTrackImage
        new("Banner", "/Game/Data/UITextures/Tracks/TracksImages/Wales/T_Severne", 1720, 448, "PF_BC7", true),                               // variant VariantImage
        new("Lobby", "/Game/Data/UITextures/Tracks/LobbyImages/SingleStage/Wales/T_Afon_Lobby", 170, 100, "PF_B8G8R8A8", true),             // variant LobbyVariantImage
    ];

    public static IEnumerable<Image> For(StageSpec s) => Images.Where(i => !i.Art || s.Menu?.Art is not null);
    public static string Package(string id, string kind) => $"/Game/AcrXs/Stages/{id}/Menu/T_{id}_{kind}";
    public static string ObjectPath(string id, string kind) => $"{Package(id, kind)}.T_{id}_{kind}";
    public static string TextureRow(string id, string kind) => $"{id}_{kind}";

    /// `menu-assets --ir <work/ir/stage> --id <AcrXsXX> --legacy <root with the templates> --out <mod root> [--art <DR2 loading .tpk>]
    /// [--preview <dir>]`: the track map, variant map and elevation profile (and with --art the track backdrop, variant banner and
    /// lobby thumbnail cropped from DR2's loading art) as UI textures under /Game/AcrXs/Stages/<id>/Menu/ (stage container).
    public static int Assets(Opts o)
    {
        var id = o.Named["id"];
        var r = new StageRoute(Path.Combine(o.Named["ir"], "stage.json"));
        var px = new Dictionary<string, byte[]>
        {
            ["Map"] = MenuArt.TrackMap(r).Rgba8(),
            ["MapSmall"] = MenuArt.VariantMap(r).Rgba8(),
            ["Elevation"] = MenuArt.Elevation(r).Rgba8(),
        };
        var artTpk = o.Named.GetValueOrDefault("art");
        if (artTpk is not null)
        {
            var (src, sw, sh, _) = Tpk(artTpk);
            foreach (var img in Images.Where(i => i.Art)) px[img.Kind] = MenuArt.ArtCrop(src, sw, sh, img.W, img.H);
        }
        var preview = o.Named.GetValueOrDefault("preview");
        if (preview is not null) Directory.CreateDirectory(preview);
        foreach (var img in Images.Where(i => px.ContainsKey(i.Kind)))
        {
            var rgba = px[img.Kind];
            if (rgba.Length != img.W * img.H * 4) throw new InvalidOperationException($"{img.Kind}: {rgba.Length} bytes, template {img.W}x{img.H}");
            var payload = img.Format == "PF_BC7" ? EncodeBc7(rgba, img.W, img.H) : Swap((byte[])rgba.Clone());
            var path = UiTexture.Write(o.Named["legacy"], img.Template, Package(id, img.Kind), img.W, img.H, img.Format, payload, o.Named["out"]);
            Console.WriteLine($"menu {img.Kind}: {path} ({img.W}x{img.H} {img.Format}, from {img.Template}{(img.Art ? $", art {Path.GetFileNameWithoutExtension(artTpk)}" : "")})");
            if (preview is null) continue;
            Dds.WritePng(Path.Combine(preview, $"{id}.{img.Kind}.png"), img.W, img.H, img.Format == "PF_BC7" ? Bc7(payload, img.W, img.H) : rgba);
            if (!img.Art) Dds.WritePng(Path.Combine(preview, $"{id}.{img.Kind}.ondark.png"), img.W, img.H, OnBackground(rgba, 0x28303C));
        }
        if (preview is not null)
        {
            // the menu draws the variant map over the track map
            var both = MenuArt.TrackMap(r);
            var v = px["MapSmall"];
            var comp = new Canvas(both.W, both.H);
            Array.Copy(both.Px, comp.Px, both.Px.Length);
            for (int i = 0; i < v.Length; i += 4)
            {
                float a = v[i + 3] / 255f;
                if (a <= 0) continue;
                for (int ch = 0; ch < 3; ch++) comp.Px[i + ch] = (v[i + ch] / 255f) * a + comp.Px[i + ch] * (1 - a);
                comp.Px[i + 3] = a + comp.Px[i + 3] * (1 - a);
            }
            Dds.WritePng(Path.Combine(preview, $"{id}.MapOverTrack.ondark.png"), comp.W, comp.H, comp.OnBackground(0x28303C));
        }
        Console.WriteLine($"menu facts: length {r.Length:0} m, DR2 height {r.MinY:0.0}..{r.MaxY:0.0} (start {r.Points[0].y:0.0}, climb {r.MaxY - r.MinY:0} m), sectors end at {string.Join(", ", r.SectorEnds.Select(s => $"{s:0}"))} m");
        return 0;
    }

    public static JObject Rows(IFileProvider p, string table)
    {
        var dt = p.LoadPackage(Db + TableFiles[table] + ".uasset").GetExports().OfType<UDataTable>().Single();
        var o = new JObject();
        foreach (var kv in dt.RowMap)
            o[kv.Key.Text] = JToken.FromObject(kv.Value.Properties.Count == 1 && kv.Value.Properties[0].Name.Text == "Value" ? (object)kv.Value.Properties[0].Tag! : kv.Value,
                Newtonsoft.Json.JsonSerializer.Create(new() { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore }));
        return o;
    }

    /// `db-rows --table </Game/... DataTable package> --out <file.json>`: every row of a DataTable as JSON.
    public static int DbRows(Opts o)
    {
        using var p = Game.Open(o);
        var dt = p.LoadPackage(o.Named["table"] + ".uasset").GetExports().OfType<UDataTable>().Single();
        var j = new JObject();
        foreach (var kv in dt.RowMap)
            j[kv.Key.Text] = JToken.FromObject(kv.Value, Newtonsoft.Json.JsonSerializer.Create(new() { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore }));
        File.WriteAllText(o.Named["out"], j.ToString());
        Console.WriteLine($"{dt.RowMap.Count} rows -> {o.Named["out"]}");
        return 0;
    }

    /// `menu-dump --track <DT_Tracks row> --out <dir> [--find-name <regex> --find-in <path regex>]`: PNG of every image the
    /// track, its variants and its location show, plus images.txt (field -> texture row -> asset, format, size).
    public static int Dump(Opts o)
    {
        using var p = Game.Open(o);
        var outDir = o.Named["out"];
        Directory.CreateDirectory(outDir);
        if (o.Named.TryGetValue("table", out var only))
        {
            // every row of one texture table, e.g. --table TracksLocationsBackgrounds
            var log1 = new List<string>();
            foreach (var (k, v) in Rows(p, only))
                log1.Add($"{only}[{k}] -> {v!["AssetPathName"]}: {SavePng(p, (string)v["AssetPathName"]!, Path.Combine(outDir, $"{only}.{k}.png"))}");
            File.WriteAllLines(Path.Combine(outDir, $"{only}.txt"), log1);
            foreach (var l in log1) Console.WriteLine(l);
            return 0;
        }
        var tracks = Rows(p, "Tracks");
        var track = (JObject)tracks[o.Named["track"]]!;
        var variants = Rows(p, "TracksVariants");
        var locations = Rows(p, "TracksLocations");
        var texTables = ImageFields.Select(f => f.texTable).Distinct().ToDictionary(t => t, t => Rows(p, t));
        var log = new List<string>();
        var rows = new List<(string table, string key, JObject row)> { ("TracksLocations", (string)track["Location"]!["Row"]!, (JObject)locations[(string)track["Location"]!["Row"]!]!), ("Tracks", o.Named["track"], track) };
        foreach (var v in (JArray)track["Variants"]!) rows.Add(("TracksVariants", (string)v["Row"]!, (JObject)variants[(string)v["Row"]!]!));
        foreach (var (table, key, row) in rows)
            foreach (var (t, field, texTable) in ImageFields.Where(f => f.table == table))
            {
                var texRow = (string?)row[field]?["Row"];
                var asset = texRow is null ? null : (string?)texTables[texTable][texRow]?["AssetPathName"];
                if (asset is null) { log.Add($"{table}[{key}].{field} -> {texTable}[{texRow}]: no asset"); continue; }
                var png = Path.Combine(outDir, $"{key}.{field}.png");
                log.Add($"{table}[{key}].{field} -> {texTable}[{texRow}] -> {asset}: {SavePng(p, asset, png)}");
            }
        if (o.Named.TryGetValue("find-name", out var find))
        {
            var re = new Regex(find, RegexOptions.IgnoreCase);
            var inRe = new Regex(o.Named.GetValueOrDefault("find-in", "^acr/Content/(Data|UI)/"), RegexOptions.IgnoreCase);
            foreach (var f in p.Files.Keys.Where(k => (k.EndsWith(".uasset") || k.EndsWith(".umap")) && inRe.IsMatch(k)).Order())
            {
                try
                {
                    if (p.LoadPackage(f) is CUE4Parse.UE4.Assets.IoPackage ip && ip.NameMap.Any(n => n.Name is { } s && re.IsMatch(s)))
                        log.Add($"name /{find}/ in {f}: {string.Join(", ", ip.NameMap.Select(n => n.Name).Where(s => s is not null && re.IsMatch(s)).Take(6))}");
                }
                catch (Exception e) { log.Add($"skip {f}: {e.GetType().Name}"); }
            }
        }
        File.WriteAllLines(Path.Combine(outDir, "images.txt"), log);
        foreach (var l in log) Console.WriteLine(l);
        return 0;
    }

    /// Mip 0 of a Texture2D as PNG; returns "PF WxH".
    public static string SavePng(IFileProvider p, string objectPath, string png)
    {
        var pkg = objectPath[..objectPath.LastIndexOf('.')];
        var tex = p.LoadPackage(pkg + ".uasset").GetExports().OfType<UTexture2D>().Single();
        var pd = tex.PlatformData;
        var m = pd.Mips[0];
        int w = m.SizeX, h = m.SizeY;
        var d = m.BulkData!.Data!;
        var pf = pd.PixelFormat.ToString();
        byte[] rgba = pf switch
        {
            "PF_B8G8R8A8" => Swap(d.AsSpan(0, w * h * 4).ToArray()),
            "PF_DXT1" => Dds.DecodeBlocks(Dds.Fmt.BC1, d, w, h),
            "PF_DXT5" => Dds.DecodeBlocks(Dds.Fmt.BC3, d, w, h),
            "PF_BC7" => Bc7(d, w, h),
            _ => throw new NotSupportedException(pf),
        };
        Dds.WritePng(png, w, h, rgba);
        return $"{pf} {w}x{h} sRGB {tex.SRGB}";
    }

    /// DR2 frontend tpk with the 0x66 header (docs/dr2-formats.md): width/height at 0x2C/0x30, payload length at 0x38,
    /// payload = the last `length` bytes. Format 49 is BC1 (loading screens), 38 is BC7 (route splines).
    public static (byte[] rgba, int w, int h, int fmt) Tpk(string path)
    {
        var b = File.ReadAllBytes(path);
        if (BitConverter.ToInt32(b, 0) != 0x66) throw new NotSupportedException($"{path}: not a v0x66 tpk");
        int fmt = BitConverter.ToInt32(b, 0x28), w = BitConverter.ToInt32(b, 0x2C), h = BitConverter.ToInt32(b, 0x30), len = BitConverter.ToInt32(b, 0x38);
        var payload = b.AsSpan(b.Length - len).ToArray();
        var rgba = fmt switch
        {
            49 => Dds.DecodeBlocks(Dds.Fmt.BC1, payload, w, h),
            38 => Raw(payload, w, h, CompressionFormat.Bc7),
            _ => throw new NotSupportedException($"{path}: tpk format {fmt}"),
        };
        return (rgba, w, h, fmt);
    }

    /// `tpk-png <file.tpk>... --out <dir>`
    public static int TpkPng(Opts o)
    {
        Directory.CreateDirectory(o.Named["out"]);
        foreach (var f in o.Positional)
        {
            var (px, w, h, fmt) = Tpk(f);
            var png = Path.Combine(o.Named["out"], Path.GetFileNameWithoutExtension(f) + ".png");
            Dds.WritePng(png, w, h, px);
            Console.WriteLine($"{f}: format {fmt} {w}x{h} -> {png}");
        }
        return 0;
    }

    public static byte[] Swap(byte[] bgra) { for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]); return bgra; }

    public static byte[] Bc7(byte[] d, int w, int h) => Raw(d, w, h, CompressionFormat.Bc7);

    public static byte[] EncodeBc7(byte[] rgba, int w, int h)
    {
        var enc = new BCnEncoder.Encoder.BcEncoder(CompressionFormat.Bc7);
        enc.OutputOptions.GenerateMipMaps = false;
        enc.OutputOptions.Quality = BCnEncoder.Encoder.CompressionQuality.Balanced;
        return enc.EncodeToRawBytes(rgba, w, h, BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
    }

    static byte[] OnBackground(byte[] rgba, int rgb)
    {
        var o = (byte[])rgba.Clone();
        byte br = (byte)(rgb >> 16), bg = (byte)(rgb >> 8), bb = (byte)rgb;
        for (int i = 0; i < o.Length; i += 4)
        {
            float a = o[i + 3] / 255f;
            o[i] = (byte)(o[i] * a + br * (1 - a)); o[i + 1] = (byte)(o[i + 1] * a + bg * (1 - a)); o[i + 2] = (byte)(o[i + 2] * a + bb * (1 - a)); o[i + 3] = 255;
        }
        return o;
    }
    public static byte[] Raw(byte[] d, int w, int h, CompressionFormat f)
    {
        var px = new BcDecoder().DecodeRaw(d, w, h, f);
        var o = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++) { o[i * 4] = px[i].r; o[i * 4 + 1] = px[i].g; o[i * 4 + 2] = px[i].b; o[i * 4 + 3] = px[i].a; }
        return o;
    }
}
