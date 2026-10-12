using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcrRef;

/// terrain-compare --ir <stage IR> [--ground-offset m] [--report json]: DR2 collision ground vs DR2 render terrain
/// (render/terrain.glb), both placed in physics metres (x, z plan, y up) and rasterised on a 1 m plan grid inside the
/// host physics bounds. Per surface: triangles, steep share (|n_y| < 0.3), triangles hanging over another layer;
/// holes (cells without any up-facing surface) near the route; render minus collision top height near the centreline.
internal static class TerrainCompare
{
    const double Cell = 1;

    sealed class Grid
    {
        public readonly int W, H;
        public readonly float[] Top, Bottom; public readonly byte[] Layers;
        public Grid(int w, int h)
        {
            W = w; H = h; Top = new float[w * h]; Bottom = new float[w * h]; Layers = new byte[w * h];
            Array.Fill(Top, float.NegativeInfinity); Array.Fill(Bottom, float.PositiveInfinity);
        }
    }

    record Tri(Splines.V3 A, Splines.V3 B, Splines.V3 C, string Mat);

    static IEnumerable<Tri> Load(string path, StageBuild.Placement pl)
    {
        foreach (var prim in new Glb(path).Primitives())
        {
            var P = new Splines.V3[prim.Positions.Length / 3];
            for (int v = 0; v < P.Length; v++) P[v] = pl.ToPhysics(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]);
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3)
            {
                var (a, b, c) = (P[prim.Indices[t]], P[prim.Indices[t + 1]], P[prim.Indices[t + 2]]);
                double cx = (a.X + b.X + c.X) / 3, cz = (a.Z + b.Z + c.Z) / 3;
                if (cx < StageBuild.PhysMinX || cx > StageBuild.PhysMaxX || cz < StageBuild.PhysMinZ || cz > StageBuild.PhysMaxZ) continue;
                yield return new Tri(a, b, c, prim.Material);
            }
        }
    }

    static double NormalY(Tri t)
    {
        var u = t.B - t.A; var v = t.C - t.A;
        double nx = u.Y * v.Z - u.Z * v.Y, ny = u.Z * v.X - u.X * v.Z, nz = u.X * v.Y - u.Y * v.X;
        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return l == 0 ? 0 : Math.Abs(ny) / l;
    }

    /// Calls f(cell index, height) for each grid cell centre inside the triangle's plan projection.
    static void Raster(Grid g, Tri t, Action<int, float> f)
    {
        double x0 = Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)), x1 = Math.Max(t.A.X, Math.Max(t.B.X, t.C.X));
        double z0 = Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z)), z1 = Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z));
        int i0 = Math.Max(0, (int)Math.Ceiling((x0 - StageBuild.PhysMinX) / Cell - 0.5)), i1 = Math.Min(g.W - 1, (int)Math.Floor((x1 - StageBuild.PhysMinX) / Cell - 0.5));
        int j0 = Math.Max(0, (int)Math.Ceiling((z0 - StageBuild.PhysMinZ) / Cell - 0.5)), j1 = Math.Min(g.H - 1, (int)Math.Floor((z1 - StageBuild.PhysMinZ) / Cell - 0.5));
        double d = (t.B.X - t.A.X) * (t.C.Z - t.A.Z) - (t.C.X - t.A.X) * (t.B.Z - t.A.Z);
        if (Math.Abs(d) < 1e-9) return;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double px = StageBuild.PhysMinX + (i + 0.5) * Cell, pz = StageBuild.PhysMinZ + (j + 0.5) * Cell;
                double l1 = ((px - t.A.X) * (t.C.Z - t.A.Z) - (t.C.X - t.A.X) * (pz - t.A.Z)) / d;
                double l2 = ((t.B.X - t.A.X) * (pz - t.A.Z) - (px - t.A.X) * (t.B.Z - t.A.Z)) / d;
                if (l1 < -1e-6 || l2 < -1e-6 || l1 + l2 > 1 + 1e-6) continue;
                f(j * g.W + i, (float)(t.A.Y + l1 * (t.B.Y - t.A.Y) + l2 * (t.C.Y - t.A.Y)));
            }
    }

    static (Grid, JsonObject) Survey(List<Tri> tris, int w, int h, string label)
    {
        var g = new Grid(w, h);
        foreach (var t in tris)
        {
            if (NormalY(t) < 0.3) continue;
            Raster(g, t, (k, y) =>
            {
                g.Top[k] = Math.Max(g.Top[k], y); g.Bottom[k] = Math.Min(g.Bottom[k], y);
                if (g.Layers[k] < 255) g.Layers[k]++;
            });
        }
        // a triangle "hangs over" when its centre is more than 1.5 m above the lowest up-facing surface there
        var r = new JsonObject();
        foreach (var grp in tris.GroupBy(t => t.Mat).OrderBy(x => x.Key))
        {
            int steep = 0, over = 0, n = 0;
            foreach (var t in grp)
            {
                n++;
                bool st = NormalY(t) < 0.3;
                if (st) steep++;
                double cx = (t.A.X + t.B.X + t.C.X) / 3, cz = (t.A.Z + t.B.Z + t.C.Z) / 3, cy = (t.A.Y + t.B.Y + t.C.Y) / 3;
                int i = (int)((cx - StageBuild.PhysMinX) / Cell), j = (int)((cz - StageBuild.PhysMinZ) / Cell);
                if (i < 0 || j < 0 || i >= w || j >= h) continue;
                int k = j * w + i;
                if (g.Layers[k] > 0 && cy > g.Bottom[k] + 1.5) over++;
            }
            r[grp.Key] = $"{n} triangles, {steep} steep (|n_y| < 0.3), {over} more than 1.5 m above the lowest surface below them";
        }
        Console.WriteLine($"{label}: {tris.Count} triangles in the physics bounds");
        foreach (var kv in r) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        return (g, r);
    }

    /// legacy-export-diff --a <.umap/.uasset> --b <...>: exports whose name, class or data differ, and import/name counts.
    public static int ExportDiff(Options o)
    {
        var a = new LegacyPackage(o.Named["a"]); var b = new LegacyPackage(o.Named["b"]);
        Console.WriteLine($"exports {a.ExportCount} / {b.ExportCount}, imports {a.ImportCount} / {b.ImportCount}, names {a.Names.Count} / {b.Names.Count}");
        int same = 0;
        for (int e = 0; e < Math.Min(a.ExportCount, b.ExportCount); e++)
        {
            if (a.ExportName(e) == b.ExportName(e) && a.ExportClass(e) == b.ExportClass(e) && a.GetExportData(e).AsSpan().SequenceEqual(b.GetExportData(e))) { same++; continue; }
            Console.WriteLine($"  export {e} {a.ExportName(e)} ({a.ExportClass(e)}): {a.GetExportData(e).Length} -> {b.GetExportData(e).Length} bytes, {b.ExportName(e)} ({b.ExportClass(e)})");
        }
        Console.WriteLine($"{same} exports identical");
        return 0;
    }

    /// ground-lod-test --ir <dir> --visuals <visuals.toml> [--ground-offset m]: GroundLod.Simplify on the whole render
    /// terrain inside the physics bounds (no road cut), for timing and triangle counts.
    public static int LodTest(Options o)
    {
        var ir = StageBuild.Ir.Load(o.Named["ir"]);
        var pl = StageBuild.Solve(ir, double.Parse(o.Named.GetValueOrDefault("ground-offset", "0"), CultureInfo.InvariantCulture));
        var cfg = Toml.Load(o.Named["visuals"]);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var s = new ProcMesh.Section();
        foreach (var prim in new Glb(Path.Combine(ir.Dir, "render", "terrain.glb")).Primitives())
        {
            int b = s.Pos.Count;
            for (int v = 0; v < prim.Positions.Length / 3; v++)
            {
                var p = pl.ToPhysics(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]);
                s.Pos.Add(new((float)(p.X * 100), (float)(p.Z * 100), (float)(p.Y * 100)));
            }
            foreach (var i in prim.Indices) s.Idx.Add((uint)(b + i));
        }
        Console.WriteLine($"loaded {s.Idx.Count / 3} triangles in {clock.Elapsed.TotalSeconds:0.0} s");
        var route = new GroundLod.RouteDistance(ir.Centre.Select(pl.ToUe).Select(q => new System.Numerics.Vector2((float)q.X, (float)q.Y)));
        var (_, idx, rep) = GroundLod.Simplify(s, route, GroundLod.Tolerance(cfg));
        Console.WriteLine(rep.ToJsonString());
        return 0;
    }

    public static int Run(Options o)
    {
        var ir = StageBuild.Ir.Load(o.Named["ir"]);
        var pl = StageBuild.Solve(ir, double.Parse(o.Named.GetValueOrDefault("ground-offset", "0"), CultureInfo.InvariantCulture));
        int w = (int)Math.Ceiling((StageBuild.PhysMaxX - StageBuild.PhysMinX) / Cell), h = (int)Math.Ceiling((StageBuild.PhysMaxZ - StageBuild.PhysMinZ) / Cell);

        var col = Load(Path.Combine(ir.Dir, "collision.glb"), pl).ToList();
        var ren = Load(Path.Combine(ir.Dir, "render", "terrain.glb"), pl).ToList();
        var (cg, cr) = Survey(col, w, h, "collision");
        var (rg, rr) = Survey(ren, w, h, "render terrain");

        // distance of each cell to the centreline (plan), only within 120 m
        var cl = ir.Centre.Select(c => pl.ToPhysics(c)).ToList();
        var along = new double[cl.Count];
        for (int i = 1; i < cl.Count; i++) along[i] = along[i - 1] + Math.Sqrt(Math.Pow(cl[i].X - cl[i - 1].X, 2) + Math.Pow(cl[i].Z - cl[i - 1].Z, 2));
        var dist = new float[w * h]; Array.Fill(dist, float.MaxValue);
        var near = new int[w * h];
        const double Reach = 120;
        for (int s = 0; s < cl.Count; s++)
        {
            int ic = (int)((cl[s].X - StageBuild.PhysMinX) / Cell), jc = (int)((cl[s].Z - StageBuild.PhysMinZ) / Cell), rc = (int)(Reach / Cell);
            for (int j = Math.Max(0, jc - rc); j <= Math.Min(h - 1, jc + rc); j++)
                for (int i = Math.Max(0, ic - rc); i <= Math.Min(w - 1, ic + rc); i++)
                {
                    double px = StageBuild.PhysMinX + (i + 0.5) * Cell - cl[s].X, pz = StageBuild.PhysMinZ + (j + 0.5) * Cell - cl[s].Z;
                    float dd = (float)Math.Sqrt(px * px + pz * pz);
                    if (dd < dist[j * w + i]) { dist[j * w + i] = dd; near[j * w + i] = s; }
                }
        }

        var rep = new JsonObject { ["collision"] = cr, ["render_terrain"] = rr };
        // render triangles by distance of their centre from the route (cells beyond 120 m: "far")
        {
            var bandsT = new SortedDictionary<string, int>();
            foreach (var t in ren)
            {
                double cx = (t.A.X + t.B.X + t.C.X) / 3, cz = (t.A.Z + t.B.Z + t.C.Z) / 3;
                int i = (int)((cx - StageBuild.PhysMinX) / Cell), j = (int)((cz - StageBuild.PhysMinZ) / Cell);
                double dd = i < 0 || j < 0 || i >= w || j >= h ? double.MaxValue : dist[j * w + i];
                string key = dd <= 30 ? "a 0-30" : dd <= 60 ? "b 30-60" : dd <= 120 ? "c 60-120" : "d >120";
                bandsT[key] = bandsT.GetValueOrDefault(key) + 1;
            }
            Console.WriteLine("render triangles by distance from the route: " + string.Join(", ", bandsT.Select(kv => $"{kv.Key[2..]} m {kv.Value}")));
        }
        // source winding: per mesh, triangles whose glb winding normal points down while most of the mesh points up
        // (the visuals builder forces every ground triangle to face up, which turns these away from the camera)
        foreach (var (label, list) in new[] { ("collision", col), ("render", ren) })
        {
            double Ny(Tri t) { var u = t.B - t.A; var v = t.C - t.A; return u.Z * v.X - u.X * v.Z; }
            var major = list.GroupBy(t => t.Mat).ToDictionary(g => g.Key, g => Math.Sign(g.Sum(t => Math.Sign(Ny(t)))));
            var down = list.Where(t => Math.Sign(Ny(t)) == -major[t.Mat]).ToList();
            var bins = new SortedDictionary<int, int>();
            foreach (var t in down)
            {
                double cx = (t.A.X + t.B.X + t.C.X) / 3, cz = (t.A.Z + t.B.Z + t.C.Z) / 3;
                int i = (int)((cx - StageBuild.PhysMinX) / Cell), j = (int)((cz - StageBuild.PhysMinZ) / Cell);
                if (i < 0 || j < 0 || i >= w || j >= h || dist[j * w + i] > 60) continue;
                int bin = (int)(along[near[j * w + i]] / 100);
                bins[bin] = bins.GetValueOrDefault(bin) + 1;
            }
            Console.WriteLine($"{label}: {down.Count} triangles wound against their mesh (facing down); within 60 m of the route by 100 m: " +
                              string.Join(", ", bins.Select(kv => $"{kv.Key * 100}: {kv.Value}")));
        }
        // --at "x,z;x,z": plan points in physics metres (= UE X, Y in m): up-facing surface layers in both meshes
        foreach (var pt in (o.Named.GetValueOrDefault("at") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var xz = pt.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var near3 = new List<string>();
            for (int dj = -2; dj <= 2; dj++)
                for (int di = -2; di <= 2; di++)
                {
                    int k = ((int)((xz[1] - StageBuild.PhysMinZ) / Cell) + dj) * w + (int)((xz[0] - StageBuild.PhysMinX) / Cell) + di;
                    near3.Add($"{(cg.Layers[k] > 0 ? "C" : ".")}{(rg.Layers[k] > 0 ? "R" : ".")}");
                }
            Console.WriteLine($"at ({pt}): 5x5 m around, C = collision ground, R = render terrain: {string.Join(" ", near3)}");
        }
        var bands = new[] { 10.0, 30, 60, 120 };
        var holes = new JsonObject();
        foreach (var b in bands)
        {
            int cells = 0, colHole = 0, renHole = 0, both = 0, colMulti = 0, renMulti = 0;
            for (int k = 0; k < w * h; k++)
            {
                if (dist[k] > b) continue;
                cells++;
                bool ch = cg.Layers[k] == 0, rh = rg.Layers[k] == 0;
                if (ch) colHole++; if (rh) renHole++; if (ch && rh) both++;
                if (cg.Layers[k] > 0 && cg.Top[k] - cg.Bottom[k] > 1.5) colMulti++;
                if (rg.Layers[k] > 0 && rg.Top[k] - rg.Bottom[k] > 1.5) renMulti++;
            }
            holes[$"within_{b}_m"] = $"{cells} cells of 1 m2: no collision ground {colHole}, no render ground {renHole}, neither {both}; " +
                                     $"layers more than 1.5 m apart: collision {colMulti}, render {renMulti}";
        }
        rep["coverage"] = holes;
        foreach (var kv in holes) Console.WriteLine($"coverage {kv.Key}: {kv.Value}");

        // collision holes near the route, clustered by 10 m along the route: where are they
        var clusters = new SortedDictionary<int, (int n, double lat, int renCovered)>();
        for (int k = 0; k < w * h; k++)
        {
            if (dist[k] > 60 || cg.Layers[k] != 0) continue;
            int bin = (int)(along[near[k]] / 50);
            var c = clusters.GetValueOrDefault(bin);
            clusters[bin] = (c.n + 1, Math.Max(c.lat, dist[k]), c.renCovered + (rg.Layers[k] > 0 ? 1 : 0));
        }
        var holeList = new JsonArray();
        foreach (var (bin, c) in clusters.Where(kv => kv.Value.n >= 5))
            holeList.Add($"{bin * 50}-{bin * 50 + 50} m along: {c.n} m2 without collision ground within 60 m (render covers {c.renCovered})");
        rep["collision_holes_within_60_m_by_50_m"] = holeList;
        Console.WriteLine($"collision holes within 60 m of the route, per 50 m: {holeList.Count} bins with >= 5 m2");
        foreach (var x in holeList.Take(40)) Console.WriteLine($"  {x}");

        // alignment: render top minus collision top, by distance from the centreline
        var align = new JsonObject();
        foreach (var (lo, hi) in new[] { (0.0, 3.0), (3.0, 10.0), (10.0, 30.0), (30.0, 120.0) })
        {
            var d = new List<double>();
            for (int k = 0; k < w * h; k++)
                if (dist[k] >= lo && dist[k] < hi && cg.Layers[k] > 0 && rg.Layers[k] > 0) d.Add(rg.Top[k] - cg.Top[k]);
            d.Sort();
            if (d.Count == 0) continue;
            double P(double q) => d[(int)Math.Min(d.Count - 1, q * d.Count)];
            align[$"{lo}-{hi}_m"] = $"n {d.Count}: mean {d.Average():0.000} m, p1 {P(0.01):0.000}, p50 {P(0.5):0.000}, p99 {P(0.99):0.000}, |d| > 0.3 m: {d.Count(x => Math.Abs(x) > 0.3)}";
        }
        rep["render_minus_collision_top"] = align;
        foreach (var kv in align) Console.WriteLine($"render - collision top {kv.Key}: {kv.Value}");

        // shared triangles: render triangles whose three vertices (mm) are those of a collision triangle, by surface
        static (long, long, long) Key(Splines.V3 p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000), (long)Math.Round(p.Z * 1000));
        static string TriKey(Tri t) { var k = new[] { Key(t.A), Key(t.B), Key(t.C) }.OrderBy(x => x).ToArray(); return string.Join(";", k); }
        var colByKey = new Dictionary<string, string>();
        foreach (var t in col) colByKey.TryAdd(TriKey(t), t.Mat);
        var colVerts = col.SelectMany(t => new[] { Key(t.A), Key(t.B), Key(t.C) }).ToHashSet();
        var shared = new JsonObject();
        foreach (var grp in ren.GroupBy(t => t.Mat.Split('!')[0]))
        {
            var m = grp.Select(t => colByKey.GetValueOrDefault(TriKey(t), "none")).GroupBy(x => x).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}");
            int vShared = grp.Count(t => colVerts.Contains(Key(t.A)) && colVerts.Contains(Key(t.B)) && colVerts.Contains(Key(t.C)));
            shared[grp.Key] = $"{grp.Count()} triangles; same triangle in collision: {string.Join(", ", m)}; all 3 vertices in collision: {vShared}";
        }
        var renKeys = ren.Select(TriKey).ToHashSet();
        foreach (var grp in col.GroupBy(t => t.Mat))
            shared[$"collision {grp.Key}"] = $"{grp.Count()} triangles, {grp.Count(t => renKeys.Contains(TriKey(t)))} also in the render terrain";
        rep["shared_triangles"] = shared;
        foreach (var kv in shared) Console.WriteLine($"shared {kv.Key}: {kv.Value}");

        if (o.Named.GetValueOrDefault("report") is { } path)
            File.WriteAllText(path, rep.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
