using System.Buffers.Binary;
using System.IO.Compression;
using AcrRef;

namespace AcrAdditive;

/// Main-menu images of a new location (docs/menu-images.md): the location tile (DT_TracksLocations.BackgroundImage, cropped
/// from DR2's loading art) and the rally plate logo (DT_TracksLocations.Logo, drawn by scripts/menu-location-logos.ps1).
/// Both live in the registry container under /Game/AcrXs/Locations/<Row>/, with rows AcrXs<Row> appended to
/// DT_TracksLocationsBackgrounds / DT_TracksLocationsLogos. Also the surface rows a location's stages copy.
internal static class MenuLocations
{
    public sealed record Image(string Kind, string Table, string File, string Field, string Template, int W, int H);

    public static readonly Image Tile = new("Tile", "TracksLocationsBackgrounds", "TrackSelection/DT_TracksLocationsBackgrounds", "BackgroundImage",
        "/Game/Data/UITextures/Tracks/T_WalesRally", 406, 737);
    public static readonly Image Logo = new("Logo", "TracksLocationsLogos", "TrackSelection/DT_TracksLocationsLogos", "Logo",
        "/Game/Data/UITextures/Tracks/T_RallyWales", 485, 218);
    public static readonly Image[] All = [Tile, Logo];

    /// Vanilla rows whose surface data a location / its variants copy (SurfaceTypesData; SurfaceTypes, CarSetupDefaultSurfaceType, TireCompounds).
    public static readonly Dictionary<string, (string location, string variant)> Surfaces = new()
    {
        ["Gravel"] = ("Wales", "WelesS4HafrenSouthFullForward"),
        ["Tarmac"] = ("Alsace", "AlsaceS2MunsterFullForward"),
        ["Snow"] = ("Livigno", "LivignoTestTrack01FullForward"),
    };
    public static readonly string[] VariantSurfaceFields = ["SurfaceTypes", "CarSetupDefaultSurfaceType", "TireCompounds"];

    public static string Row(string location) => $"AcrXs{location}";
    public static string Package(string location, string kind) => $"/Game/AcrXs/Locations/{location}/T_{location}_{kind}";
    public static string ObjectPath(string location, string kind) => $"{Package(location, kind)}.T_{location}_{kind}";

    /// Generated images of a location the registry creates (Template set): a tile unless a vanilla Tile row is named, a logo unless a vanilla Logo is.
    public static IEnumerable<Image> For(StageSpec.LocationSpec l)
    {
        if (l.Template is null) yield break;
        if (l.Tile is null && l.TileArt is not null) yield return Tile;
        if (l.Logo is null && l.GenLogo) yield return Logo;
    }

    /// One LocationSpec per new location row (every spec of a location carries the same block).
    public static List<StageSpec.LocationSpec> NewLocations(IEnumerable<StageSpec> specs) =>
        specs.Where(s => s.Location.Template is not null).GroupBy(s => s.Location.Row).Select(g => g.First().Location).ToList();

    public static bool Any(IEnumerable<StageSpec> specs) => NewLocations(specs).Any(l => For(l).Any());

    /// `menu-locations --legacy <root with the templates> --out <mod root> --art-dir <dir of DR2 loading .tpk> --logo-dir <dir of <Row>.png>
    /// [--preview <dir>] <stage spec dir|json>...`: the tile / logo textures of every new location.
    public static int Run(Opts o)
    {
        var specs = o.Positional.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.json") : [p]).Select(p => StageSpec.Load(p)).ToList();
        var preview = o.Named.GetValueOrDefault("preview");
        if (preview is not null) Directory.CreateDirectory(preview);
        foreach (var l in NewLocations(specs))
            foreach (var img in For(l))
            {
                byte[] rgba;
                string from;
                if (img == Tile)
                {
                    from = Path.Combine(o.Named["art-dir"], l.TileArt + ".tpk");
                    var (src, sw, sh, _) = Menu.Tpk(from);
                    rgba = MenuArt.ArtCrop(src, sw, sh, img.W, img.H);
                }
                else
                {
                    from = Path.Combine(o.Named["logo-dir"], l.Row + ".png");
                    int w, h;
                    (rgba, w, h) = ReadPng(from);
                    if (w != img.W || h != img.H) throw new InvalidDataException($"{from}: {w}x{h}, expected {img.W}x{img.H}");
                }
                var path = UiTexture.WriteRgba(o.Named["legacy"], img.Template, Package(l.Row, img.Kind), img.W, img.H, rgba, o.Named["out"]);
                Console.WriteLine($"location {l.Row} {img.Kind}: {path} ({img.W}x{img.H}, from {Path.GetFileName(from)})");
                if (preview is not null) Dds.WritePng(Path.Combine(preview, $"{l.Row}.{img.Kind}.png"), img.W, img.H, rgba);
            }
        return 0;
    }

    /// 8-bit RGB / RGBA, non-interlaced PNG (what System.Drawing writes) as RGBA8.
    public static (byte[] rgba, int w, int h) ReadPng(string path)
    {
        var b = File.ReadAllBytes(path);
        int p = 8, w = 0, h = 0, type = 0;
        using var idat = new MemoryStream();
        while (p < b.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
            var tag = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            var data = b.AsSpan(p + 8, len);
            if (tag == "IHDR")
            {
                w = BinaryPrimitives.ReadInt32BigEndian(data); h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                type = data[9];
                if (data[8] != 8 || (type != 2 && type != 6) || data[12] != 0) throw new NotSupportedException($"{path}: only 8-bit RGB / RGBA non-interlaced");
            }
            else if (tag == "IDAT") idat.Write(data);
            else if (tag == "IEND") break;
            p += 12 + len;
        }
        int bpp = type == 6 ? 4 : 3, stride = w * bpp;
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[(stride + 1) * h];
        z.ReadExactly(raw);
        var cur = new byte[stride]; var prev = new byte[stride];
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, up = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = (byte)(cur[i] + f switch
                {
                    0 => 0, 1 => a, 2 => up, 3 => (a + up) / 2,
                    4 => Paeth(a, up, c),
                    _ => throw new InvalidDataException($"{path}: filter {f}"),
                });
            }
            for (int x = 0; x < w; x++)
            {
                int k = (y * w + x) * 4;
                o[k] = cur[x * bpp]; o[k + 1] = cur[x * bpp + 1]; o[k + 2] = cur[x * bpp + 2]; o[k + 3] = bpp == 4 ? cur[x * bpp + 3] : (byte)255;
            }
            (prev, cur) = (cur, prev);
        }
        return (o, w, h);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
