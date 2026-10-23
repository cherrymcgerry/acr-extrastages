using AcrRef;
using Newtonsoft.Json.Linq;

namespace AcrAdditive;

/// `menu-options --ir <work/ir/stage> --id <AcrXsXX> --out <dir> [--loading <DR2 loading .tpk>...]`: PNG previews of the
/// photo-style menu images (location tile, track background, variant banner, lobby thumbnail) from two local sources, for
/// choosing a style; nothing is cooked:
///  - dr2art: DR2's own loading-screen art of the location (frontend/streamed_textures/loading/<location>_route_N.tpk);
///  - topdown: a plan view of the stage's ground bake (render/ground_bake low tiles, 0.5 m/px) around the route.
internal static class MenuOptions
{
    /// ACR's photo slots (DB field, size) as measured on the vanilla textures.
    static readonly (string slot, int w, int h)[] Slots =
    [
        ("LocationTile", 406, 737),        // DT_TracksLocations.BackgroundImage (DT_TracksLocationsBackgrounds)
        ("TrackImage", 1325, 922),         // DT_Tracks.TrackImage / LobbyTrackImage
        ("VariantImage", 1720, 448),       // DT_TracksVariants.VariantImage (vanilla: BC7)
        ("LobbyVariantImage", 170, 100),   // DT_TracksVariants.LobbyVariantImage
    ];

    public static int Run(Opts o)
    {
        var id = o.Named["id"];
        var outDir = o.Named["out"];
        Directory.CreateDirectory(outDir);
        var ir = o.Named["ir"];
        var r = new StageRoute(Path.Combine(ir, "stage.json"));

        foreach (var tpk in o.Multi("loading"))
        {
            var (px, w, h, _) = Menu.Tpk(tpk);
            var name = Path.GetFileNameWithoutExtension(tpk);
            foreach (var (slot, sw, sh) in Slots)
                Save(outDir, $"{id}.dr2art.{name}.{slot}", MenuArt.Resize(px, w, h, MenuArt.CoverCrop(w, h, sw, sh), sw, sh), sw, sh);
        }

        var manifest = Path.Combine(ir, "render", "ground_bake", "manifest.json");
        if (File.Exists(manifest))
        {
            var (img, w, h) = TopDown(r, manifest, 1.0, 150);
            Save(outDir, $"{id}.topdown.full", img, w, h);
            foreach (var (slot, sw, sh) in Slots)
            {
                // centre the crop on the middle of the route
                var mid = r.Points[r.Points.Count / 2];
                double cx = (mid.x - r.Points.Min(p => p.x) + 150) / w, cy = (mid.z - r.Points.Min(p => p.z) + 150) / h;
                Save(outDir, $"{id}.topdown.{slot}", MenuArt.Resize(img, w, h, MenuArt.CoverCrop(w, h, sw, sh, cx, cy), sw, sh), sw, sh);
            }
        }
        else Console.WriteLine($"{id}: no ground bake ({manifest}); the top-down option needs one");
        return 0;
    }

    static void Save(string dir, string name, byte[] rgba, int w, int h)
    {
        var f = Path.Combine(dir, name + ".png");
        Dds.WritePng(f, w, h, rgba);
        Console.WriteLine($"{f} ({w}x{h})");
    }

    /// Plan view (rows grow with Z, as MenuArt.Frame) of the low ground-bake tiles over the route's box plus a margin.
    static (byte[] img, int w, int h) TopDown(StageRoute r, string manifest, double mPerPx, double margin)
    {
        var m = JObject.Parse(File.ReadAllText(manifest));
        var dir = Path.GetDirectoryName(manifest)!;
        var tiles = ((JArray)m["tiles"]!).Where(t => (string)t["tier"]! == "low").Select(t =>
        {
            var b = t["bounds"]!;
            var dds = new Dds(Path.Combine(dir, (string)t["dds"]!));
            int mip = Enumerable.Range(0, dds.Mips.Count).First(i => (double)t["size_m"]! / dds.MipWidth(i) >= mPerPx * 0.99 || i == dds.Mips.Count - 1);
            return (x0: (double)b["min_x"]!, z0: (double)b["min_z"]!, size: (double)t["size_m"]!, px: dds.Decode(mip)!, n: dds.MipWidth(mip));
        }).ToList();
        double x0 = r.Points.Min(p => p.x) - margin, z0 = r.Points.Min(p => p.z) - margin;
        int w = (int)((r.Points.Max(p => p.x) + margin - x0) / mPerPx), h = (int)((r.Points.Max(p => p.z) + margin - z0) / mPerPx);
        var img = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double wx = x0 + (x + 0.5) * mPerPx, wz = z0 + (y + 0.5) * mPerPx;
                int k = (y * w + x) * 4;
                img[k + 3] = 255;
                foreach (var t in tiles)
                {
                    double u = (wx - t.x0) / t.size, v = (wz - t.z0) / t.size;
                    if (u < 0 || v < 0 || u >= 1 || v >= 1) continue;
                    int s = ((int)(v * t.n) * t.n + (int)(u * t.n)) * 4;
                    if (t.px[s] + t.px[s + 1] + t.px[s + 2] == 0) continue;   // outside the baked corridor
                    img[k] = t.px[s]; img[k + 1] = t.px[s + 1]; img[k + 2] = t.px[s + 2];
                    break;
                }
            }
        return (img, w, h);
    }
}
