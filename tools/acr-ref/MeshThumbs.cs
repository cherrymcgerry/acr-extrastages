using System.Numerics;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace AcrRef;

/// `acr-ref mesh-thumbs --filter <regex> --out <png> [--limit N]`: a contact sheet of ACR static meshes (LOD0, the
/// Nanite fallback for Nanite meshes), flat shaded one colour per material section, seen from the front-left above,
/// to choose props by shape. A red tick marks the mesh's +X side, a green one +Y. Cells are numbered in the order
/// printed (row by row, 4 per row).
internal static class MeshThumbs
{
    public static int Run(Options o)
    {
        if (o.Filter is null || o.Out is null) { Console.Error.WriteLine("mesh-thumbs needs --filter and --out"); return 1; }
        using var p = Program.Open(o);
        var re = new Regex(o.Filter, RegexOptions.IgnoreCase);
        const int cw = 360, ch = 270, cols = 4;
        var cells = new List<(string name, byte[] px)>();
        foreach (var path in p.Files.Keys.Where(k => k.EndsWith(".uasset") && re.IsMatch(k)).Order())
        {
            if (cells.Count >= (o.Limit ?? 24)) break;
            UStaticMesh? mesh;
            try { mesh = p.LoadPackage(path).GetExports().OfType<UStaticMesh>().FirstOrDefault(); }
            catch (Exception e) { Console.WriteLine($"{path}: {e.GetType().Name}"); continue; }
            if (mesh?.RenderData?.LODs is not { Length: > 0 } lods || lods[0].PositionVertexBuffer?.Verts is not { Length: > 0 } pos) continue;
            var idx = lods[0].IndexBuffer!;
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in pos) { mn = Vector3.Min(mn, new Vector3(v.X, v.Y, v.Z)); mx = Vector3.Max(mx, new Vector3(v.X, v.Y, v.Z)); }
            var c = (mn + mx) / 2; float r = Math.Max(30, (mx - mn).Length() / 2);
            var eye = c + Vector3.Normalize(new Vector3(-1.0f, -0.8f, 0.6f)) * r * 2.4f;
            var ras = new Raster(cw, ch, eye, c, 45) { FogStartCm = 1e9f, FogEndCm = 2e9f, FarCm = 1e9f };
            var colours = new[] { new Vector3(0.75f, 0.72f, 0.65f), new Vector3(0.45f, 0.6f, 0.8f), new Vector3(0.85f, 0.6f, 0.35f), new Vector3(0.5f, 0.75f, 0.45f), new Vector3(0.7f, 0.5f, 0.75f) };
            foreach (var s in lods[0].Sections!)
                for (int t = (int)s.FirstIndex; t + 2 < s.FirstIndex + s.NumTriangles * 3; t += 3)
                {
                    Vector3 V(int k) { var q = pos[(int)idx[t + k]]; return new Vector3(q.X, q.Y, q.Z); }
                    ras.Triangle(V(0), V(1), V(2), default, default, default, null, colours[s.MaterialIndex % colours.Length], false, 1);
                }
            void Tick(Vector3 at, Vector3 colour)
            {
                float k = r * 0.06f;
                ras.Triangle(at + new Vector3(-k, 0, 0), at + new Vector3(k, 0, 0), at + new Vector3(0, 0, 3 * k), default, default, default, null, colour, false, 1);
                ras.Triangle(at + new Vector3(0, -k, 0), at + new Vector3(0, k, 0), at + new Vector3(0, 0, 3 * k), default, default, default, null, colour, false, 1);
            }
            Tick(new Vector3(mx.X + r * 0.1f, c.Y, mn.Z), new Vector3(0.9f, 0.1f, 0.1f));
            Tick(new Vector3(c.X, mx.Y + r * 0.1f, mn.Z), new Vector3(0.1f, 0.8f, 0.1f));
            var name = path[(path.LastIndexOf('/') + 1)..^".uasset".Length];
            Console.WriteLine($"{cells.Count,3}: {name}  {(mx.X - mn.X) / 100:0.00} x {(mx.Y - mn.Y) / 100:0.00} x {(mx.Z - mn.Z) / 100:0.00} m (z {mn.Z / 100:0.00}..{mx.Z / 100:0.00}), {idx.Length / 3} tris");
            cells.Add((name, ras.Rgba));
        }
        if (cells.Count == 0) { Console.WriteLine("no meshes"); return 1; }
        int rows = (cells.Count + cols - 1) / cols, w = cw * cols, h = ch * rows;
        var sheet = new byte[w * h * 4];
        for (int i = 0; i < cells.Count; i++)
            for (int y = 0; y < ch; y++)
                Buffer.BlockCopy(cells[i].px, y * cw * 4, sheet, (((i / cols) * ch + y) * w + (i % cols) * cw) * 4, cw * 4);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);
        Dds.WritePng(o.Out, w, h, sheet);
        Console.WriteLine($"{cells.Count} meshes -> {o.Out}");
        return 0;
    }
}
