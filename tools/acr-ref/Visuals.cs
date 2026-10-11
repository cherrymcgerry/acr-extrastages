using System.Numerics;
using System.Text.Json.Nodes;

namespace AcrRef;

/// Interim visuals without the editor: the forward gameplay cell's start-trigger ProceduralMeshComponent (loaded with
/// variant 1/2, see ProcMesh) is rebuilt in world space with the DR2 ground (terrain + road surface), edge posts along
/// both road edges and start/finish gates. Render only: sections have no collision (the car uses BakedData).
/// Sections: 0 hidden dummy (keeps slot 0 for any runtime SetMaterial(0)), 1 terrain, 2 road, 3 edge posts, 4 gates.
internal static class Visuals
{
    public static JsonObject Build(LegacyPackage cell, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg)
    {
        var r = new JsonObject();
        int e = Enumerable.Range(0, cell.ExportCount).Single(i => cell.ExportClass(i) == "ProceduralMeshComponent"
            && cell.ExportOuter(i) > 0 && cell.ExportName(cell.ExportOuter(i) - 1).StartsWith("BC_StartSequenceTrigger_C"));
        var comp = ProcMesh.Parse(cell.GetExportData(e), e);
        if (!ProcMesh.RoundTrips(comp, out var why)) throw new InvalidDataException($"start trigger mesh sections do not round trip: {why}");

        var roadCodes = cfg.Arr("road", "surfaces").Cast<string>().ToHashSet();
        Vector3 PhysToUe(double x, double y, double z) => new((float)(x * 100), (float)(z * 100), (float)(y * 100));

        // ground: DR2 collision triangles inside the physics bounds, per material class
        var terrain = new ProcMesh.Section(); var road = new ProcMesh.Section();
        var glb = new Glb(Path.Combine(ir.Dir, "collision.glb"));
        foreach (var prim in glb.Primitives())
        {
            var target = roadCodes.Contains(prim.Material) ? road : terrain;
            int nv = prim.Positions.Length / 3;
            var map = new int[nv]; Array.Fill(map, -1);
            int Add(uint v)
            {
                if (map[v] >= 0) return map[v];
                var p = pl.ToPhysics(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]);
                var u = PhysToUe(p.X, p.Y, p.Z);
                map[v] = target.Pos.Count;
                target.Pos.Add(u); target.Nrm.Add(Vector3.Zero); target.Uv.Add(new Vector2(u.X / 400, u.Y / 400));
                return map[v];
            }
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3)
            {
                uint a0 = prim.Indices[t], b0 = prim.Indices[t + 1], c0 = prim.Indices[t + 2];
                double cx = 0, cz = 0;
                foreach (var v in new[] { a0, b0, c0 }) { var p = pl.ToPhysics(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]); cx += p.X / 3; cz += p.Z / 3; }
                if (cx < StageBuild.PhysMinX || cx > StageBuild.PhysMaxX || cz < StageBuild.PhysMinZ || cz > StageBuild.PhysMaxZ) continue;
                AddTri(target, Add(a0), Add(b0), Add(c0));
            }
        }
        foreach (var s in new[] { terrain, road }) for (int i = 0; i < s.Nrm.Count; i++) s.Nrm[i] = s.Nrm[i] == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(s.Nrm[i]);

        // edge posts every spacing_m along the DR2 gates, both sides; start/finish gates
        double spacing = cfg.Num("markers", "spacing_m"), w = cfg.Num("markers", "post_width_m") * 100, h = cfg.Num("markers", "post_height_m") * 100;
        var posts = new ProcMesh.Section(); var gates = new ProcMesh.Section();
        double next = 0; int postCount = 0;
        var postCentres = new JsonArray();
        foreach (var g in ir.Gates)
        {
            if (g.distance < next) continue;
            next = g.distance + spacing;
            foreach (var edge in new[] { g.left, g.right })
            {
                var b = pl.ToUe(edge);
                var basePt = new Vector3((float)b.X, (float)b.Y, (float)b.Z);
                AddBox(posts, basePt - new Vector3((float)w / 2, (float)w / 2, 30), basePt + new Vector3((float)w / 2, (float)w / 2, (float)h));
                postCount++;
                if (postCentres.Count < 4) postCentres.Add($"{basePt.X:0},{basePt.Y:0},{basePt.Z:0}");
            }
        }
        double gh = cfg.Num("markers", "gate_height_m") * 100;
        foreach (var type in new[] { "start", "finish" })
        {
            var tm = ir.Timing.First(t => t.type == type);
            var g = ir.Gates[tm.gate];
            var l = pl.ToUe(g.left); var rr = pl.ToUe(g.right);
            var L = new Vector3((float)l.X, (float)l.Y, (float)l.Z); var R = new Vector3((float)rr.X, (float)rr.Y, (float)rr.Z);
            float pw = 40;
            AddBox(gates, L - new Vector3(pw, pw, 50), L + new Vector3(pw, pw, (float)gh));
            AddBox(gates, R - new Vector3(pw, pw, 50), R + new Vector3(pw, pw, (float)gh));
            // banner: an axis-aligned-in-plan box would not follow the road; use a quad pair across the top instead
            AddBanner(gates, L + new Vector3(0, 0, (float)gh - 80), R + new Vector3(0, 0, (float)gh - 80), 80);
        }

        // dummy: one tiny triangle 50 m below the start gate
        var start = pl.ToUe(ir.Gates[ir.Timing.First(t => t.type == "start").gate].crossing);
        var dummy = new ProcMesh.Section();
        var d0 = new Vector3((float)start.X, (float)start.Y, (float)start.Z - 5000);
        foreach (var off in new[] { Vector3.Zero, new Vector3(1, 0, 0), new Vector3(0, 1, 0) }) { dummy.Pos.Add(d0 + off); dummy.Nrm.Add(Vector3.UnitZ); dummy.Uv.Add(Vector2.Zero); }
        dummy.Idx.AddRange([0u, 2u, 1u]);

        int Mat(string key)
        {
            var path = cfg.Str("materials", key);
            if (path.Length == 0) return 0;
            var name = path[(path.LastIndexOf('/') + 1)..];
            int pkgImp = cell.AddImport("/Script/CoreUObject", "Package", 0, path);
            return cell.AddImport("/Script/Engine", "MaterialInstanceConstant", pkgImp, name);
        }
        var mats = new List<int> { 0, Mat("terrain"), Mat("road"), Mat("posts"), Mat("gates") };
        var sections = new List<ProcMesh.Section> { dummy, terrain, road, posts, gates };
        var data = ProcMesh.BuildComponent(comp, sections, mats);
        cell.SetExportData(e, data);

        r["component"] = $"{cell.ExportName(e)} of {cell.ExportName(cell.ExportOuter(e) - 1)} (export {e})";
        r["terrain"] = $"{terrain.Pos.Count} vertices, {terrain.Idx.Count / 3} triangles";
        r["road"] = $"{road.Pos.Count} vertices, {road.Idx.Count / 3} triangles (codes {string.Join(", ", roadCodes)})";
        r["edge_posts"] = postCount;
        r["edge_posts_first"] = postCentres;
        r["gates"] = "start + finish: 2 posts + banner each";
        r["materials"] = new JsonArray(mats.Select(m => (JsonNode)(m == 0 ? "default" : cell.ImportName(-m - 1))).ToArray());
        r["export_bytes"] = $"{comp.Data.Length} -> {data.Length}";
        var (mn, mx) = ProcMesh.Bounds(sections.SelectMany(s => s.Pos));
        r["bounds_m"] = $"({mn.X / 100:0},{mn.Y / 100:0},{mn.Z / 100:0})..({mx.X / 100:0},{mx.Y / 100:0},{mx.Z / 100:0})";
        Console.WriteLine($"visuals: terrain {r["terrain"]}, road {r["road"]}, {postCount} edge posts, export {r["export_bytes"]} bytes");
        return r;
    }

    /// Offline check of the rebuilt component in a built (or round-tripped) cell.
    public static int Check(LegacyPackage cell, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, TextWriter w)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }
        int e = Enumerable.Range(0, cell.ExportCount).Single(i => cell.ExportClass(i) == "ProceduralMeshComponent"
            && cell.ExportOuter(i) > 0 && cell.ExportName(cell.ExportOuter(i) - 1).StartsWith("BC_StartSequenceTrigger_C"));
        var c = ProcMesh.Parse(cell.GetExportData(e), e);
        C(c.PropsEnd + 8 == c.Data.Length, $"visuals: start-trigger mesh export parses to its end ({c.Data.Length} bytes)");
        C(c.Slots.TryGetValue(142, out var hid) && hid.zero && c.Slots.ContainsKey(136) && c.Slots.ContainsKey(133),
          "visuals: bHiddenInGame = false, bVisible = true, absolute transform (world-space vertices)");
        int p = c.Slots[3].at; int n = BitConverter.ToInt32(c.Data, p); p += 4;
        var secs = new List<ProcMesh.Section>();
        for (int i = 0; i < n; i++) secs.Add(ProcMesh.ReadSection(c.Data, ref p, out _));
        C(n == 5 && secs.All(s => !s.Collision), $"visuals: {n} sections, none with collision ({string.Join(", ", secs.Select(s => $"{s.Pos.Count} v / {s.Idx.Count / 3} t"))})");
        C(secs.All(s => s.Idx.All(i => i < s.Pos.Count)) && secs.All(s => s.Idx.Count % 3 == 0), "visuals: all indices in range");
        var mats = c.Slots.TryGetValue(7, out var m) && !m.zero ? BitConverter.ToInt32(c.Data, m.at) : 0;
        C(mats == 5, $"visuals: {mats} material slots (dummy, terrain = default, road, posts, gates)");

        // posts: 20 vertices each, centred on a gate edge point
        double spacing = cfg.Num("markers", "spacing_m");
        var edges = new List<Vector2>(); double next = 0;
        foreach (var g in ir.Gates)
        {
            if (g.distance < next) continue;
            next = g.distance + spacing;
            foreach (var edge in new[] { g.left, g.right }) { var b = pl.ToUe(edge); edges.Add(new Vector2((float)b.X, (float)b.Y)); }
        }
        var posts = secs[3];
        int count = posts.Pos.Count / 20; double worst = 0;
        for (int k = 0; k < Math.Min(count, edges.Count); k++)
        {
            var ctr = Vector2.Zero;
            for (int v = 0; v < 20; v++) ctr += new Vector2(posts.Pos[k * 20 + v].X, posts.Pos[k * 20 + v].Y) / 20;
            worst = Math.Max(worst, (ctr - edges[k]).Length());
        }
        double width = ir.Gates.Average(g => Math.Sqrt(Math.Pow(g.left[0] - g.right[0], 2) + Math.Pow(g.left[2] - g.right[2], 2)));
        C(count == edges.Count && posts.Pos.Count == count * 20 && worst < 1,
          $"visuals: {count} edge posts every {spacing} m on both gate edges (max offset {worst:0.00} cm; mean road width {width:0.0} m)");

        // ground: the terrain + road sections cover the placed DR2 centreline
        var ground = secs[1].Pos.Concat(secs[2].Pos).ToList();
        var (mn, mx) = ProcMesh.Bounds(ground);
        var cl = ir.Centre.Select(pl.ToUe).ToList();
        C(cl.All(q => q.X >= mn.X && q.X <= mx.X && q.Y >= mn.Y && q.Y <= mx.Y) && secs[2].Idx.Count > 0,
          $"visuals: ground mesh {ground.Count} vertices covers all {cl.Count} centreline points; road section {secs[2].Idx.Count / 3} triangles");
        return failures;
    }

    /// Adds a triangle facing up (UE front face: cross(b - a, c - a) points against the normal) and accumulates normals.
    static void AddTri(ProcMesh.Section s, int a, int b, int c)
    {
        var cr = Vector3.Cross(s.Pos[b] - s.Pos[a], s.Pos[c] - s.Pos[a]);
        if (cr.Z > 0) { (b, c) = (c, b); cr = -cr; }
        var up = -cr;
        s.Nrm[a] += up; s.Nrm[b] += up; s.Nrm[c] += up;
        s.Idx.Add((uint)a); s.Idx.Add((uint)b); s.Idx.Add((uint)c);
    }

    static void Quad(ProcMesh.Section s, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n)
    {
        int b = s.Pos.Count;
        foreach (var p in new[] { p0, p1, p2, p3 }) { s.Pos.Add(p); s.Nrm.Add(n); }
        s.Uv.AddRange([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        void T(int i, int j, int k)
        {
            var cr = Vector3.Cross(s.Pos[b + j] - s.Pos[b + i], s.Pos[b + k] - s.Pos[b + i]);
            if (Vector3.Dot(cr, n) > 0) (j, k) = (k, j);
            s.Idx.Add((uint)(b + i)); s.Idx.Add((uint)(b + j)); s.Idx.Add((uint)(b + k));
        }
        T(0, 1, 2); T(0, 2, 3);
    }

    /// Axis-aligned box: top and four sides (outward normals).
    static void AddBox(ProcMesh.Section s, Vector3 mn, Vector3 mx)
    {
        Vector3 P(float x, float y, float z) => new(x, y, z);
        Quad(s, P(mn.X, mn.Y, mx.Z), P(mx.X, mn.Y, mx.Z), P(mx.X, mx.Y, mx.Z), P(mn.X, mx.Y, mx.Z), Vector3.UnitZ);
        Quad(s, P(mn.X, mn.Y, mn.Z), P(mx.X, mn.Y, mn.Z), P(mx.X, mn.Y, mx.Z), P(mn.X, mn.Y, mx.Z), -Vector3.UnitY);
        Quad(s, P(mx.X, mn.Y, mn.Z), P(mx.X, mx.Y, mn.Z), P(mx.X, mx.Y, mx.Z), P(mx.X, mn.Y, mx.Z), Vector3.UnitX);
        Quad(s, P(mx.X, mx.Y, mn.Z), P(mn.X, mx.Y, mn.Z), P(mn.X, mx.Y, mx.Z), P(mx.X, mx.Y, mx.Z), Vector3.UnitY);
        Quad(s, P(mn.X, mx.Y, mn.Z), P(mn.X, mn.Y, mn.Z), P(mn.X, mn.Y, mx.Z), P(mn.X, mx.Y, mx.Z), -Vector3.UnitX);
    }

    /// Vertical double-sided strip between two points (height hgt, centred on them).
    static void AddBanner(ProcMesh.Section s, Vector3 a, Vector3 b, float hgt)
    {
        var along = Vector3.Normalize(b - a);
        var n = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitZ));
        var up = new Vector3(0, 0, hgt / 2);
        Quad(s, a - up, b - up, b + up, a + up, n);
        Quad(s, a - up, b - up, b + up, a + up, -n);
    }
}
