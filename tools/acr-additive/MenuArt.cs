using System.Buffers.Binary;
using AcrRef;
using Newtonsoft.Json.Linq;

namespace AcrAdditive;

/// The route of a stage IR (work/ir/<stage>/stage.json) between the start and finish lines, for the menu images.
internal sealed class StageRoute
{
    public readonly List<(double s, double x, double y, double z)> Points = new();   // s = metres from the start line
    public readonly double Length;
    public readonly double[] SectorEnds;                                             // metres from the start line, last = Length

    public StageRoute(string stageJson)
    {
        var ir = JObject.Parse(File.ReadAllText(stageJson));
        var cl = ((JArray)ir["centreline"]!).Select(p => (x: (double)p[0]!, y: (double)p[1]!, z: (double)p[2]!)).ToList();
        var gd = ((JArray)ir["gates"]!).Select(g => (double)g["distance"]!).ToList();
        if (gd.Count != cl.Count) throw new InvalidDataException($"{stageJson}: {cl.Count} centreline points, {gd.Count} gates");
        var timing = (JArray)ir["timing"]!;
        var start = timing.Single(t => (string)t["type"]! == "start");
        var finish = timing.Single(t => (string)t["type"]! == "finish");
        int g0 = (int)start["gate"]!, g1 = (int)finish["gate"]!;
        double s0 = gd[g0];
        for (int i = g0; i <= g1; i++) Points.Add((gd[i] - s0, cl[i].x, cl[i].y, cl[i].z));
        Length = Points[^1].s;

        // the level's race sectors: the DR2 split nearest 1/3 and 2/3 of the stage (StageBuild.cs, RaceSector1/2)
        var splits = timing.Where(t => (string)t["type"]! == "time").Select(t => (int)t["gate"]!).ToList();
        int Near(double frac) => splits.Count == 0 ? (int)Math.Round(g0 + frac * (g1 - g0)) : splits.OrderBy(g => Math.Abs(gd[g] - s0 - frac * Length)).First();
        int a = Near(1 / 3.0), b = Near(2 / 3.0);
        if (b == a) b = splits.Count > 1 ? splits[^1] : (int)Math.Round(g0 + 2 / 3.0 * (g1 - g0));
        SectorEnds = [gd[a] - s0, gd[b] - s0, Length];
    }

    public double MinY => Points.Min(p => p.y);
    public double MaxY => Points.Max(p => p.y);

    /// Heights resampled every `step` metres and smoothed over +-`window` metres.
    public List<(double s, double y)> Profile(double step = 25, double window = 40)
    {
        var res = new List<(double, double)>();
        for (double s = 0; s <= Length + 1e-6; s += Math.Min(step, Math.Max(1e-3, Length - s)))
        {
            var near = Points.Where(p => Math.Abs(p.s - s) <= window).ToList();
            res.Add((s, near.Count > 0 ? near.Average(p => p.y) : Points.MinBy(p => Math.Abs(p.s - s)).y));
            if (s >= Length) break;
        }
        return res;
    }
}

/// A straight-alpha RGBA canvas with anti-aliased strokes and fills (analytic coverage for lines and circles,
/// 4x4 supersampling for polygons).
internal sealed class Canvas
{
    public readonly int W, H;
    public readonly float[] Px;   // r, g, b, a in 0..1 (sRGB values, straight alpha)

    public Canvas(int w, int h) { W = w; H = h; Px = new float[w * h * 4]; }

    public readonly record struct Rgba(float R, float G, float B, float A = 1)
    {
        public static Rgba Hex(int rgb, float a = 1) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    }

    void Blend(int x, int y, Rgba c, float cov)
    {
        if (x < 0 || y < 0 || x >= W || y >= H || cov <= 0) return;
        int k = (y * W + x) * 4;
        float sa = c.A * Math.Min(1, cov), da = Px[k + 3], oa = sa + da * (1 - sa);
        if (oa <= 0) return;
        Px[k] = (c.R * sa + Px[k] * da * (1 - sa)) / oa;
        Px[k + 1] = (c.G * sa + Px[k + 1] * da * (1 - sa)) / oa;
        Px[k + 2] = (c.B * sa + Px[k + 2] * da * (1 - sa)) / oa;
        Px[k + 3] = oa;
    }

    static double SegDist(double px, double py, (double x, double y) a, (double x, double y) b)
    {
        double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((px - a.x) * dx + (py - a.y) * dy) / l2, 0, 1);
        double ex = px - (a.x + t * dx), ey = py - (a.y + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// Polyline of the given width with round joins and caps; overlapping segments do not darken (max coverage).
    public void Stroke(IReadOnlyList<(double x, double y)> pts, double width, Rgba c)
    {
        var cov = new Dictionary<int, float>();
        double r = width / 2;
        for (int i = 0; i + 1 < pts.Count || (pts.Count == 1 && i == 0); i++)
        {
            var a = pts[i]; var b = pts.Count == 1 ? a : pts[i + 1];
            int x0 = (int)Math.Floor(Math.Min(a.x, b.x) - r - 1), x1 = (int)Math.Ceiling(Math.Max(a.x, b.x) + r + 1);
            int y0 = (int)Math.Floor(Math.Min(a.y, b.y) - r - 1), y1 = (int)Math.Ceiling(Math.Max(a.y, b.y) + r + 1);
            for (int y = Math.Max(0, y0); y <= Math.Min(H - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(W - 1, x1); x++)
                {
                    float v = (float)Math.Clamp(r - SegDist(x + 0.5, y + 0.5, a, b) + 0.5, 0, 1);
                    if (v <= 0) continue;
                    int k = y * W + x;
                    if (!cov.TryGetValue(k, out var old) || v > old) cov[k] = v;
                }
            if (pts.Count == 1) break;
        }
        foreach (var (k, v) in cov) Blend(k % W, k / W, c, v);
    }

    public void Circle(double cx, double cy, double radius, Rgba c)
    {
        for (int y = (int)(cy - radius - 1); y <= (int)(cy + radius + 1); y++)
            for (int x = (int)(cx - radius - 1); x <= (int)(cx + radius + 1); x++)
            {
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                Blend(x, y, c, (float)Math.Clamp(radius - d + 0.5, 0, 1));
            }
    }

    public void Polygon(IReadOnlyList<(double x, double y)> poly, Rgba c)
    {
        int x0 = (int)Math.Floor(poly.Min(p => p.x)), x1 = (int)Math.Ceiling(poly.Max(p => p.x));
        int y0 = (int)Math.Floor(poly.Min(p => p.y)), y1 = (int)Math.Ceiling(poly.Max(p => p.y));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                        if (Inside(poly, x + (sx + 0.5) / 4, y + (sy + 0.5) / 4)) inside++;
                Blend(x, y, c, inside / 16f);
            }
    }

    static bool Inside(IReadOnlyList<(double x, double y)> p, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].y > y) != (p[j].y > y) && x < (p[j].x - p[i].x) * (y - p[i].y) / (p[j].y - p[i].y) + p[i].x) c = !c;
        return c;
    }

    public void Rect(double x0, double y0, double x1, double y1, Rgba c) => Polygon([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], c);

    /// 8-bit straight-alpha RGBA.
    public byte[] Rgba8()
    {
        var o = new byte[W * H * 4];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)Math.Clamp((int)Math.Round(Px[i] * 255), 0, 255);
        return o;
    }

    /// This canvas over a solid background (previews).
    public byte[] OnBackground(int rgb)
    {
        var o = Rgba8();
        float br = ((rgb >> 16) & 255), bg = ((rgb >> 8) & 255), bb = rgb & 255;
        for (int i = 0; i < o.Length; i += 4)
        {
            float a = o[i + 3] / 255f;
            o[i] = (byte)(o[i] * a + br * (1 - a)); o[i + 1] = (byte)(o[i + 1] * a + bg * (1 - a)); o[i + 2] = (byte)(o[i + 2] * a + bb * (1 - a)); o[i + 3] = 255;
        }
        return o;
    }
}

/// ACR's track selection map and elevation images, measured on the vanilla textures (work/acr/menu-preview/vanilla-*):
///  - variant map (DT_TracksVariants.MapSmall), 800x800, transparent: route in red (222,24,22) ~8.5 px, start = red ring with
///    a white centre (r 12.5), finish = red dot (r 11) with a chequered flag, a white navigation arrow by the start;
///  - track map (DT_Tracks.Map), 800x800: every variant of the track in white, same framing as the variant maps (the
///    menu lays the variant over it);
///  - elevation (DT_TracksVariants.ElevationMap), 306x142: profile in red (220,26,22) ~4.5 px over the full width, white
///    dashed guides (2 px, ~5 on / 4 off) at y 1, 67 and 129, solid white base line at y 139-140.
internal static class MenuArt
{
    static readonly Canvas.Rgba Red = Canvas.Rgba.Hex(0xDE1816), RedProfile = Canvas.Rgba.Hex(0xDC1A16), White = Canvas.Rgba.Hex(0xFFFFFF), Black = Canvas.Rgba.Hex(0x000000);

    /// Plan view seen from above (+Y up, right-handed: screen right = +X, screen up = -Z, so image rows grow with Z),
    /// fitted into size x size.
    public static Func<double, double, (double x, double y)> Frame(StageRoute r, int size, double margin)
    {
        double x0 = r.Points.Min(p => p.x), x1 = r.Points.Max(p => p.x), z0 = r.Points.Min(p => p.z), z1 = r.Points.Max(p => p.z);
        double scale = (size - 2 * margin) / Math.Max(x1 - x0, z1 - z0);
        double ox = (size - (x1 - x0) * scale) / 2, oy = (size - (z1 - z0) * scale) / 2;
        return (x, z) => (ox + (x - x0) * scale, oy + (z - z0) * scale);
    }

    public static Canvas TrackMap(StageRoute r, int size = 800)
    {
        var f = Frame(r, size, 60);
        var c = new Canvas(size, size);
        c.Stroke(r.Points.Select(p => f(p.x, p.z)).ToList(), 8.5, White);
        return c;
    }

    public static Canvas VariantMap(StageRoute r, int size = 800)
    {
        var f = Frame(r, size, 60);
        var c = new Canvas(size, size);
        var pts = r.Points.Select(p => f(p.x, p.z)).ToList();
        c.Stroke(pts, 8.5, Red);

        var (sx, sy) = pts[0];
        var (ex, ey) = pts[^1];
        // travel direction at the start (first ~60 px of the drawn line)
        var ahead = pts.FirstOrDefault(p => Math.Sqrt((p.x - sx) * (p.x - sx) + (p.y - sy) * (p.y - sy)) > 60, pts[^1]);
        double dx = ahead.x - sx, dy = ahead.y - sy, dl = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        dx /= dl; dy /= dl;
        // the arrow sits beside the start ring (on the side away from the route), pointing along the start direction
        double nx = -dy, ny = dx;
        double ax = sx - nx * 34 - dx * 6, ay = sy - ny * 34 - dy * 6;
        (double, double) A(double fwd, double side) => (ax + dx * fwd + nx * side, ay + dy * fwd + ny * side);
        var arrow = new List<(double, double)> { A(17, 0), A(-13, 11), A(-6, 0), A(-13, -11) };
        var outline = new List<(double, double)> { A(19.5, 0), A(-15, 13.5), A(-7.5, 0), A(-15, -13.5) };
        c.Polygon(outline, Black);
        c.Polygon(arrow, White);

        c.Circle(sx, sy, 12.5, Red);
        c.Circle(sx, sy, 9.5, White);
        c.Circle(ex, ey, 11, Red);

        // chequered flag (5 x 3 squares of 7 px with a white border) below-right of the finish, clear of the route end
        var end = pts.LastOrDefault(p => Math.Sqrt((p.x - ex) * (p.x - ex) + (p.y - ey) * (p.y - ey)) > 40, pts[0]);
        double bx = end.x - ex, by = end.y - ey;
        double fx = ex + (bx > 0 ? -50 : 14), fy = ey + (by > 0 ? -44 : 20);
        c.Rect(fx - 2, fy - 2, fx + 37, fy + 23, White);
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 5; i++)
                if ((i + j) % 2 == 0) c.Rect(fx + i * 7, fy + j * 7, fx + i * 7 + 7, fy + j * 7 + 7, Black);
        return c;
    }

    public static Canvas Elevation(StageRoute r, int w = 306, int h = 142)
    {
        var c = new Canvas(w, h);
        foreach (var y in new[] { 1, 67, 129 })
            for (int x = 0; x < w; x += 9) c.Rect(x, y, Math.Min(w, x + 5), y + 2, White);
        c.Rect(0, 139, w, 141, White);
        var prof = r.Profile();
        double lo = prof.Min(p => p.y), hi = prof.Max(p => p.y), span = Math.Max(hi - lo, 20);
        double mid = (lo + hi) / 2;
        // guides at y 1 / 129 frame the curve; keep the line 10 px inside them (flat stages stay near the middle)
        double top = 12, bottom = 118;
        var pts = prof.Select(p => (2 + p.s / r.Length * (w - 4), (top + bottom) / 2 - (p.y - mid) / span * (bottom - top))).ToList();
        c.Stroke(pts, 4.5, RedProfile);
        return c;
    }

    /// Area-average resample of an RGBA8 image to w x h after cropping the source rectangle.
    public static byte[] Resize(byte[] src, int sw, int sh, (double x, double y, double w, double h) crop, int w, int h)
    {
        var o = new byte[w * h * 4];
        double fx = crop.w / w, fy = crop.h / h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double ax = crop.x + x * fx, ay = crop.y + y * fy;
                int x0 = (int)ax, x1 = Math.Max(x0 + 1, (int)Math.Ceiling(ax + fx)), y0 = (int)ay, y1 = Math.Max(y0 + 1, (int)Math.Ceiling(ay + fy));
                var acc = new double[4]; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int k = (Math.Clamp(yy, 0, sh - 1) * sw + Math.Clamp(xx, 0, sw - 1)) * 4;
                        for (int ch = 0; ch < 4; ch++) acc[ch] += src[k + ch];
                        n++;
                    }
                for (int ch = 0; ch < 4; ch++) o[(y * w + x) * 4 + ch] = (byte)(acc[ch] / n);
            }
        return o;
    }

    /// DR2 loading art (2304x1296 painted aerial) for a w x h slot: the art's edges are blurred, so crop inside a 6% inset.
    public static byte[] ArtCrop(byte[] src, int sw, int sh, int w, int h)
    {
        double ix = sw * 0.06, iy = sh * 0.06;
        var (x, y, cw, ch) = CoverCrop((int)(sw - 2 * ix), (int)(sh - 2 * iy), w, h);
        var o = Resize(src, sw, sh, (x + ix, y + iy, cw, ch), w, h);
        for (int i = 3; i < o.Length; i += 4) o[i] = 255;
        return o;
    }

    /// Largest centred crop of the source with the target's aspect ratio.
    public static (double, double, double, double) CoverCrop(int sw, int sh, int w, int h, double cx = 0.5, double cy = 0.5)
    {
        double ta = (double)w / h, sa = (double)sw / sh;
        double cw = sa > ta ? sh * ta : sw, ch = sa > ta ? sh : sw / ta;
        return (Math.Clamp(cx * sw - cw / 2, 0, sw - cw), Math.Clamp(cy * sh - ch / 2, 0, sh - ch), cw, ch);
    }
}

/// UI Texture2D packages cloned from a vanilla UI texture of the same size and pixel format (PF_B8G8R8A8 or PF_BC7, NeverStream,
/// inline mips, TEXTUREGROUP_UI): the mip payloads are overwritten in place and the package / object renamed, so the
/// template's property block and platform data layout are kept byte for byte.
internal static class UiTexture
{
    public static string LegacyFile(string root, string package) =>
        Path.Combine(root, "acr", "Content", package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");

    /// Writes `package` (e.g. /Game/AcrXs/Stages/X/Menu/T_XMap) under outRoot from a one-mip template; `payload` is the mip in the
    /// template's format (BGRA bytes or BC7 blocks). Returns the object path.
    public static string Write(string legacyRoot, string template, string package, int w, int h, string format, byte[] payload, string outRoot) =>
        Write(legacyRoot, template, package, w, h, format, (level, _, _) => level == 0 ? payload : throw new InvalidDataException($"{template}: has more than one mip"), outRoot);

    /// B8G8R8A8 texture from RGBA8 mip 0; lower mips (as many as the template has) are 2x2 box-filtered.
    public static string WriteRgba(string legacyRoot, string template, string package, int w, int h, byte[] rgba, string outRoot)
    {
        var mips = new List<(byte[] px, int w, int h)> { (rgba, w, h) };
        return Write(legacyRoot, template, package, w, h, "PF_B8G8R8A8", (level, mw, mh) =>
        {
            while (mips.Count <= level) mips.Add(Half(mips[^1].px, mips[^1].w, mips[^1].h));
            if (mips[level].w != mw || mips[level].h != mh) throw new InvalidDataException($"{template}: mip {level} is {mw}x{mh}, not {mips[level].w}x{mips[level].h}");
            return Menu.Swap((byte[])mips[level].px.Clone());
        }, outRoot);
    }

    static (byte[], int, int) Half(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                for (int c = 0; c < 4; c++)
                {
                    int s = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(w - 1, x * 2 + dx), sy = Math.Min(h - 1, y * 2 + dy);
                            s += src[(sy * w + sx) * 4 + c]; n++;
                        }
                    o[(y * nw + x) * 4 + c] = (byte)(s / n);
                }
        return (o, nw, nh);
    }

    static string Write(string legacyRoot, string template, string package, int w, int h, string format, Func<int, int, int, byte[]> mip, string outRoot)
    {
        var pkg = new LegacyPackage(LegacyFile(legacyRoot, template));
        if (pkg.ExportCount != 1 || pkg.ExportClass(0) != "Texture2D") throw new InvalidDataException($"{template}: not a single Texture2D export");
        if (pkg.Names[1] != format || pkg.Names[2] != pkg.ExportName(0) || pkg.Names[3] != pkg.PackageName)
            throw new InvalidDataException($"{template}: name map is not [None, {format}, object, package, ...] ({string.Join(", ", pkg.Names.Take(4))})");
        var dr = pkg.DataResources();
        int count = dr.Length < 8 ? 0 : BinaryPrimitives.ReadInt32LittleEndian(dr.AsSpan(4));
        if (count < 1) throw new InvalidDataException($"{template}: no data resources");
        var data = pkg.GetExportData(0);
        for (int i = 0; i < count; i++)
        {
            var e = dr.AsSpan(8 + i * LegacyPackage.DataResourceSize, LegacyPackage.DataResourceSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(e[40..]) != 0x48) throw new InvalidDataException($"{template}: mip {i} is not inline (SingleUse | ForceInlinePayload)");
            long off = BinaryPrimitives.ReadInt64LittleEndian(e[4..]), size = BinaryPrimitives.ReadInt64LittleEndian(e[20..]);
            // the mip's SizeX / SizeY follow the payload: a template of another size would need its platform data rewritten
            int mw = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)(off + size))), mh = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)(off + size + 4)));
            if (i == 0 && (mw != w || mh != h)) throw new InvalidDataException($"{template}: mip 0 is {mw}x{mh}, not {w}x{h}");
            var payload = mip(i, mw, mh);
            if (size != payload.Length) throw new InvalidDataException($"{template}: mip {i} is {size} bytes, not {payload.Length} ({mw}x{mh} {format})");
            payload.CopyTo(data.AsSpan((int)off));
        }
        var obj = package[(package.LastIndexOf('/') + 1)..];
        pkg.SetExportData(0, data);
        pkg.RenameName(2, obj);
        pkg.RenameName(3, package);
        pkg.SetPackageName(package);
        pkg.SaveTo(LegacyFile(outRoot, package));
        return $"{package}.{obj}";
    }
}
