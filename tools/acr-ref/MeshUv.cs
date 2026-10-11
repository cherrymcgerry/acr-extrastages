using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace AcrRef;

/// `acr-ref mesh-uv <StaticMesh package> [--limit N]`: LOD0 UV channels and vertex colours of a static mesh (ranges,
/// world metres per UV unit, sample vertices), to copy a host mesh's UV layout onto the visuals mesh.
internal static class MeshUv
{
    public static int Run(Options o)
    {
        if (o.Positional.Count != 1) { Console.Error.WriteLine("mesh-uv needs one StaticMesh package path"); return 1; }
        using var p = Program.Open(o);
        var pkg = p.LoadPackage(o.Positional[0]);
        var mesh = pkg.GetExports().OfType<UStaticMesh>().First();
        var lod = mesh.RenderData!.LODs[0];
        var pos = lod.PositionVertexBuffer!.Verts;
        var vb = lod.VertexBuffer!;
        int nuv = vb.NumTexCoords;
        var colours = lod.ColorVertexBuffer?.Data;
        var idx = lod.IndexBuffer!;
        Console.WriteLine($"{mesh.Name}: {pos.Length} vertices, {idx.Length / 3} triangles, {nuv} UV channels, colours {(colours?.Length ?? 0)}");
        foreach (var s in lod.Sections!)
            Console.WriteLine($"  section material {s.MaterialIndex} {mesh.StaticMaterials?[s.MaterialIndex].MaterialSlotName}: {s.NumTriangles} triangles");
        var mn = pos.Aggregate((a, b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)));
        var mx = pos.Aggregate((a, b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        Console.WriteLine($"  bounds m: ({mn.X / 100:0.0},{mn.Y / 100:0.0},{mn.Z / 100:0.0})..({mx.X / 100:0.0},{mx.Y / 100:0.0},{mx.Z / 100:0.0})");
        for (int c = 0; c < nuv; c++)
        {
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            for (int i = 0; i < pos.Length; i++)
            {
                var uv = vb.UV[i].UV[c];
                u0 = Math.Min(u0, uv.U); u1 = Math.Max(u1, uv.U); v0 = Math.Min(v0, uv.V); v1 = Math.Max(v1, uv.V);
            }
            // metres per UV unit along U and V: gradient of world position w.r.t. U and V per triangle (median)
            var mu = new List<double>(); var mv = new List<double>();
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = (int)idx[t], b = (int)idx[t + 1], d = (int)idx[t + 2];
                var A = vb.UV[a].UV[c]; var B = vb.UV[b].UV[c]; var D = vb.UV[d].UV[c];
                double du1 = B.U - A.U, dv1 = B.V - A.V, du2 = D.U - A.U, dv2 = D.V - A.V, det = du1 * dv2 - du2 * dv1;
                if (Math.Abs(det) < 1e-12) continue;
                var e1 = pos[b] - pos[a]; var e2 = pos[d] - pos[a];
                var dpdu = (e1 * (float)dv2 - e2 * (float)dv1) / (float)det;
                var dpdv = (e2 * (float)du1 - e1 * (float)du2) / (float)det;
                mu.Add(dpdu.Size() / 100); mv.Add(dpdv.Size() / 100);
            }
            double Med(List<double> l) { if (l.Count == 0) return double.NaN; l.Sort(); return l[l.Count / 2]; }
            Console.WriteLine($"  UV{c}: U {u0:0.###}..{u1:0.###}, V {v0:0.###}..{v1:0.###}; median metres per unit U {Med(mu):0.###}, V {Med(mv):0.###}");
        }
        int limit = o.Limit ?? 24;
        for (int k = 0; k < limit; k++)
        {
            int i = (int)((long)k * (pos.Length - 1) / Math.Max(1, limit - 1));
            var uvs = string.Join("  ", Enumerable.Range(0, nuv).Select(c => $"uv{c} {vb.UV[i].UV[c].U,8:0.###},{vb.UV[i].UV[c].V,8:0.###}"));
            var col = colours is { Length: > 0 } ? $"  rgba {colours[i].R},{colours[i].G},{colours[i].B},{colours[i].A}" : "";
            Console.WriteLine($"  v{i,6} ({pos[i].X / 100,8:0.0},{pos[i].Y / 100,8:0.0},{pos[i].Z / 100,7:0.0})  {uvs}{col}");
        }
        return 0;
    }
}
