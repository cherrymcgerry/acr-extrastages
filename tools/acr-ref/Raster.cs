using System.Collections.Concurrent;
using System.Numerics;

namespace AcrRef;

/// Small CPU rasterizer for `preview`: perspective camera, z-buffer, perspective-correct UVs, per-triangle mip choice,
/// alpha test, Lambert sun + ambient, distance fog, and a per-pixel category id for coverage metrics.
/// World space is UE (cm, Z up). Deterministic and dependency-free so it runs the same on Windows and Linux.
internal sealed class Raster
{
    public readonly int W, H;
    public readonly byte[] Rgba;
    public readonly float[] Depth;
    public readonly byte[] Cat;

    readonly Matrix4x4 _view;
    readonly float _f, _aspect, _near;
    readonly Vector3 _eye;
    public float FogStartCm = 30_000, FogEndCm = 250_000;
    public static readonly Vector3 Sky = new(0.62f, 0.74f, 0.88f);
    static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.35f, -0.45f, 0.82f));

    public Raster(int w, int h, Vector3 eye, Vector3 target, float fovDeg = 70)
    {
        W = w; H = h; _eye = eye;
        Rgba = new byte[w * h * 4];
        Depth = new float[w * h];
        Cat = new byte[w * h];
        Array.Fill(Depth, float.MaxValue);
        for (int y = 0; y < h; y++)
        {
            // sky gradient, lighter at the horizon
            var c = Vector3.Lerp(Sky, new Vector3(0.36f, 0.52f, 0.78f), y < h / 2 ? 1 - y / (h / 2f) : 0);
            for (int x = 0; x < w; x++) Put(x, y, c);
        }
        _view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitZ);
        _f = 1 / MathF.Tan(fovDeg * MathF.PI / 360);
        _aspect = (float)w / h;
        _near = 20; // cm
    }

    void Put(int x, int y, Vector3 c)
    {
        int i = (y * W + x) * 4;
        Rgba[i] = (byte)Math.Clamp(c.X * 255, 0, 255);
        Rgba[i + 1] = (byte)Math.Clamp(c.Y * 255, 0, 255);
        Rgba[i + 2] = (byte)Math.Clamp(c.Z * 255, 0, 255);
        Rgba[i + 3] = 255;
    }

    /// Distance from the eye beyond which nothing is drawn (cm).
    public float FarCm = 300_000;

    /// True when a world-space sphere can be visible (in front of the camera, within the far distance, in the frustum).
    public bool SphereVisible(Vector3 c, float r)
    {
        var v = Vector3.Transform(c, _view);
        float z = -v.Z;
        if (z + r < _near || z - r > FarCm) return false;
        float sx = _f / _aspect, sy = _f;
        // side planes (conservative, through the camera)
        if (v.X * sx - r * MathF.Sqrt(1 + sx * sx) > z) return false;
        if (-v.X * sx - r * MathF.Sqrt(1 + sx * sx) > z) return false;
        if (v.Y * sy - r * MathF.Sqrt(1 + sy * sy) > z) return false;
        if (-v.Y * sy - r * MathF.Sqrt(1 + sy * sy) > z) return false;
        return true;
    }

    struct Vert { public Vector3 V; public Vector2 Uv; }

    /// Draws one world-space triangle. tex null = flat colour; alphaTest = discard texels with alpha < 0.5.
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, Tex? tex, Vector3 colour, bool alphaTest, byte cat)
    {
        var n = Vector3.Cross(b - a, c - a);
        float nl = n.Length();
        if (nl == 0) return;
        n /= nl;
        // two-sided lighting: foliage cards and DR2's mixed winding
        float lit = 0.5f + 0.6f * MathF.Abs(Vector3.Dot(n, Sun));
        Span<Vert> poly = stackalloc Vert[4];
        Span<Vert> tmp = stackalloc Vert[3];
        tmp[0] = new Vert { V = Vector3.Transform(a, _view), Uv = ua };
        tmp[1] = new Vert { V = Vector3.Transform(b, _view), Uv = ub };
        tmp[2] = new Vert { V = Vector3.Transform(c, _view), Uv = uc };
        // view space looks down -Z; clip against z = -near
        int count = 0;
        for (int i = 0; i < 3; i++)
        {
            var p = tmp[i]; var q = tmp[(i + 1) % 3];
            bool pin = -p.V.Z >= _near, qin = -q.V.Z >= _near;
            if (pin) poly[count++] = p;
            if (pin != qin)
            {
                float t = (-_near - p.V.Z) / (q.V.Z - p.V.Z);
                poly[count++] = new Vert { V = Vector3.Lerp(p.V, q.V, t), Uv = Vector2.Lerp(p.Uv, q.Uv, t) };
            }
        }
        if (count < 3) return;
        if (-poly[0].V.Z > FarCm && -poly[1].V.Z > FarCm && -poly[2].V.Z > FarCm) return;
        for (int i = 1; i + 1 < count; i++) Raster2(poly[0], poly[i], poly[i + 1], tex, colour * lit, alphaTest, cat);
    }

    void Raster2(Vert a, Vert b, Vert c, Tex? tex, Vector3 shade, bool alphaTest, byte cat)
    {
        // project: x right, y down
        Vector3 P(Vector3 v) { float iz = 1 / -v.Z; return new((v.X * _f / _aspect * iz * 0.5f + 0.5f) * W, (0.5f - v.Y * _f * iz * 0.5f) * H, iz); }
        var pa = P(a.V); var pb = P(b.V); var pc = P(c.V);
        float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pc.X - pa.X) * (pb.Y - pa.Y);
        if (MathF.Abs(area) < 1e-6f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
        int x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
        int y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
        if (x0 > x1 || y0 > y1) return;

        // mip: texels per pixel from the triangle's UV area against its screen area
        int mip = 0;
        if (tex is not null)
        {
            float uvArea = MathF.Abs((b.Uv.X - a.Uv.X) * (c.Uv.Y - a.Uv.Y) - (c.Uv.X - a.Uv.X) * (b.Uv.Y - a.Uv.Y)) * tex.W * tex.H;
            float ratio = uvArea / MathF.Max(MathF.Abs(area), 1e-3f);
            mip = Math.Clamp((int)MathF.Floor(0.5f * MathF.Log2(MathF.Max(ratio, 1))), 0, tex.Mips.Count - 1);
        }
        float inv = 1 / area;
        // perspective-correct attributes: interpolate u/z, v/z, 1/z
        var ua = a.Uv * pa.Z; var ub = b.Uv * pb.Z; var uc = c.Uv * pc.Z;
        for (int y = y0; y <= y1; y++)
        {
            float py = y + 0.5f;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float w0 = ((pb.X - px) * (pc.Y - py) - (pc.X - px) * (pb.Y - py)) * inv;
                float w1 = ((pc.X - px) * (pa.Y - py) - (pa.X - px) * (pc.Y - py)) * inv;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float iz = w0 * pa.Z + w1 * pb.Z + w2 * pc.Z;
                float z = 1 / iz;
                int idx = y * W + x;
                if (z >= Depth[idx]) continue;
                Vector3 col = shade;
                if (tex is not null)
                {
                    var uv = (ua * w0 + ub * w1 + uc * w2) * z;
                    var t = tex.Sample(mip, uv);
                    if (alphaTest && t.W < 0.5f) continue;
                    col = new Vector3(t.X, t.Y, t.Z) * shade;
                }
                float fog = Math.Clamp((z - FogStartCm) / (FogEndCm - FogStartCm), 0, 1);
                Depth[idx] = z;
                Cat[idx] = cat;
                Put(x, y, Vector3.Lerp(col, Sky, fog * 0.85f));
            }
        }
    }
}

/// RGBA texture with a box-filtered mip chain, sampled nearest with wrap.
internal sealed class Tex
{
    public readonly List<float[]> Mips = new();   // RGBA floats 0..1 per level
    public readonly List<(int w, int h)> Sizes = new();
    public int W => Sizes[0].w;
    public int H => Sizes[0].h;

    public Tex(byte[] rgba, int w, int h)
    {
        while (true)
        {
            var f = new float[w * h * 4];
            for (int i = 0; i < f.Length; i++) f[i] = rgba[i] / 255f;
            Mips.Add(f); Sizes.Add((w, h));
            if (w <= 1 && h <= 1) break;
            (rgba, w, h) = Dds.Half(rgba, w, h);
        }
    }

    public Vector4 Sample(int mip, Vector2 uv)
    {
        var (w, h) = Sizes[mip];
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        int x = Math.Min(w - 1, (int)(u * w)), y = Math.Min(h - 1, (int)(v * h));
        var m = Mips[mip];
        int i = (y * w + x) * 4;
        return new Vector4(m[i], m[i + 1], m[i + 2], m[i + 3]);
    }

    static readonly ConcurrentDictionary<string, Lazy<Tex?>> Cache = new();

    /// Loads a DDS (BC1-BC7 or uncompressed 32-bit), starting at the first mip no larger than maxSize. Null if unreadable.
    public static Tex? Load(string path, int maxSize = 512) => Cache.GetOrAdd(path, p => new Lazy<Tex?>(() =>
    {
        try
        {
            if (!File.Exists(p)) return null;
            var d = File.ReadAllBytes(p);
            var raw = RawDds(d);
            if (raw is not null) return Shrink(raw.Value.px, raw.Value.w, raw.Value.h, maxSize);
            var dds = new Dds(d, Path.GetFileName(p));
            int m = 0;
            while (m + 1 < dds.Mips.Count && Math.Max(dds.MipWidth(m), dds.MipHeight(m)) > maxSize) m++;
            int w = dds.MipWidth(m), h = dds.MipHeight(m);
            byte[] px = dds.Format == Dds.Fmt.BC7 ? DecodeBc7(dds.Mips[m], w, h) : dds.Decode(m)!;
            return new Tex(px, w, h);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    })).Value;

    static Tex Shrink(byte[] px, int w, int h, int maxSize)
    {
        while (Math.Max(w, h) > maxSize && w > 1 && h > 1) (px, w, h) = Dds.Half(px, w, h);
        return new Tex(px, w, h);
    }

    public static byte[] DecodeBc7(byte[] blocks, int w, int h)
    {
        var dec = new BCnEncoder.Decoder.BcDecoder();
        var px = dec.DecodeRaw(blocks, w, h, BCnEncoder.Shared.CompressionFormat.Bc7);
        var r = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { r[i * 4] = px[i].r; r[i * 4 + 1] = px[i].g; r[i * 4 + 2] = px[i].b; r[i * 4 + 3] = px[i].a; }
        return r;
    }

    /// Uncompressed 32-bit DDS (no FourCC, RGB bit count 32, RGBA or BGRA masks).
    static (byte[] px, int w, int h)? RawDds(byte[] d)
    {
        if (d.Length < 128 || BitConverter.ToUInt32(d, 0) != 0x20534444) return null;
        uint pfFlags = BitConverter.ToUInt32(d, 80), fourcc = BitConverter.ToUInt32(d, 84), bits = BitConverter.ToUInt32(d, 88);
        if ((pfFlags & 0x4) != 0 || fourcc != 0 || bits != 32) return null;
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
        uint rMask = BitConverter.ToUInt32(d, 92);
        bool bgra = rMask == 0x00FF0000;
        bool hasAlpha = (pfFlags & 0x1) != 0;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int s = 128 + i * 4;
            px[i * 4] = d[s + (bgra ? 2 : 0)]; px[i * 4 + 1] = d[s + 1]; px[i * 4 + 2] = d[s + (bgra ? 0 : 2)];
            px[i * 4 + 3] = hasAlpha ? d[s + 3] : (byte)255;
        }
        return (px, w, h);
    }
}
