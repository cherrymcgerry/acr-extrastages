using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// Render-only geometry for the DR2 objects, appended to the visuals mesh as one section per class colour, so the
/// driver sees what is solid. Solid = the shapes ObjectColliders kept (the same list that goes into BakedData).
///   - tree trunks: the kept trunk shapes themselves (capsule -> prism, cone, box), plus a soft crown proxy
///   - other instances with kept shapes: the DR2 render mesh (render/objects.glb, one mesh per template, template-local
///     vertices placed by the instance transform) if it is at most max_mesh_triangles, else the kept shapes
///   - everything not solid (soft vegetation, cloth, light breakables, objects dropped from the road corridor): an
///     octahedron over the render mesh's bounds in the "soft" colour
internal static class VisualObjects
{
    public static readonly string[] Classes = ["trunks", "wood", "metal", "concrete", "rock", "plastic", "other", "soft"];

    static string ClassOf(string wall) => wall switch
    {
        "PM_WallWoodTreeTrunks" => "trunks",
        _ when wall.StartsWith("PM_WallWood") => "wood",
        _ when wall.StartsWith("PM_WallMetal") => "metal",
        "PM_WallConcrete" => "concrete",
        "PM_WallRock" => "rock",
        _ when wall.StartsWith("PM_WallPlastic") => "plastic",
        "PM_WallBush" => "soft",
        _ => "other",
    };

    sealed record Mesh(Vector3[] Pos, uint[] Idx, Vector3 Min, Vector3 Max);

    public static (Dictionary<string, ProcMesh.Section> sections, JsonObject report) Build(
        StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, ObjectColliders.Result objs, Func<V3, bool> inPhysicsBounds)
    {
        int maxMeshTris = (int)cfg.Num("objects", "max_mesh_triangles");
        int sides = (int)cfg.Num("objects", "prism_sides");
        var sec = Classes.ToDictionary(c => c, _ => new ProcMesh.Section());
        var counts = Classes.ToDictionary(c => c, _ => 0);

        var meshes = new Dictionary<string, Mesh>();
        foreach (var g in new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().GroupBy(p => p.Mesh))
        {
            var pos = new List<Vector3>(); var idx = new List<uint>();
            foreach (var p in g)
            {
                uint b = (uint)pos.Count;
                for (int i = 0; i < p.Positions.Length; i += 3) pos.Add(new Vector3(p.Positions[i], p.Positions[i + 1], p.Positions[i + 2]));
                idx.AddRange(p.Indices.Select(i => i + b));
            }
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in pos) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            meshes[g.Key] = new Mesh(pos.ToArray(), idx.ToArray(), mn, mx);
        }

        var kept = objs.Kept.GroupBy(s => s.Instance).ToDictionary(g => g.Key, g => g.ToList());
        var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        var templates = stage["templates"]!.AsObject();
        var treeClasses = cfg.Arr("objects", "tree_shape_classes").Cast<string>().ToHashSet();
        int instances = 0, outside = 0, noMesh = 0, meshInstances = 0, proxyInstances = 0, softInstances = 0, treeInstances = 0;
        int alignChecked = 0, misaligned = 0;
        var perTemplate = new SortedDictionary<string, string>();

        V3 Ue(Vector3 dr2) => pl.ToUe([dr2.X, dr2.Y, dr2.Z]);

        foreach (var inst in stage["instances"]!.AsArray())
        {
            instances++;
            var tname = inst!["template"]!.GetValue<string>();
            var m = ObjectColliders.Trs(inst["transform"]);
            var origin = Vector3.Transform(Vector3.Zero, m);
            if (!inPhysicsBounds(pl.ToPhysics(origin.X, origin.Y, origin.Z))) { outside++; continue; }
            meshes.TryGetValue(tname, out var mesh);
            var shapes = kept.GetValueOrDefault(inst["id"]!.GetValue<string>()) ?? [];
            bool tree = shapes.Any(s => s.Material == "PM_WallWoodTreeTrunks") || templates[tname]?["bodies"]?.AsArray()
                .SelectMany(b => b!["shapes"]!.AsArray()).Any(s => treeClasses.Contains(s!["shape_class"]?.GetValue<string>() ?? "")) == true;

            if (tree && shapes.Count > 0)
            {
                treeInstances++;
                foreach (var s in shapes) AddShape(sec[ClassOf(s.Material)], s, sides, pl);
                counts["trunks"]++;
                if (mesh is not null) { AddOctahedron(sec["soft"], CrownBox(mesh, m, Ue)); counts["soft"]++; }
                perTemplate.TryAdd(tname, "tree: trunk shapes + soft crown");
            }
            else if (shapes.Count > 0 && mesh is not null && mesh.Idx.Length / 3 <= maxMeshTris)
            {
                meshInstances++;
                var cls = shapes.GroupBy(s => ClassOf(s.Material)).OrderByDescending(g => g.Count()).First().Key;
                AddMesh(sec[cls], mesh, m, pl, Ue);
                var (bmn, bmx) = WorldBox(mesh.Min, mesh.Max, m, Ue);
                foreach (var s in shapes)
                {
                    var ctr = s.Box?.Center ?? s.Capsule?.Center ?? s.FootprintUe.Aggregate((a, b) => a + b) * (1.0 / s.FootprintUe.Count);
                    alignChecked++;
                    if (ctr.X < bmn.X - 100 || ctr.X > bmx.X + 100 || ctr.Y < bmn.Y - 100 || ctr.Y > bmx.Y + 100 || ctr.Z < bmn.Z - 100 || ctr.Z > bmx.Z + 100)
                        misaligned++;
                }
                counts[cls]++;
                perTemplate.TryAdd(tname, $"render mesh ({mesh.Idx.Length / 3} triangles), {cls}");
            }
            else if (shapes.Count > 0)
            {
                proxyInstances++;
                foreach (var s in shapes) { AddShape(sec[ClassOf(s.Material)], s, sides, pl); counts[ClassOf(s.Material)]++; }
                perTemplate.TryAdd(tname, mesh is null ? "kept shapes (no render mesh)" : $"kept shapes (render mesh {mesh.Idx.Length / 3} triangles > {maxMeshTris})");
            }
            else if (mesh is not null)
            {
                softInstances++;
                AddOctahedron(sec["soft"], WorldBox(mesh.Min, mesh.Max, m, Ue));
                counts["soft"]++;
                perTemplate.TryAdd(tname, "not solid: soft proxy");
            }
            else noMesh++;
        }

        foreach (var s in sec.Values)
            for (int i = 0; i < s.Nrm.Count; i++) s.Nrm[i] = s.Nrm[i] == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(s.Nrm[i]);
        var r = new JsonObject
        {
            ["instances"] = instances, ["outside_physics_bounds"] = outside, ["trees"] = treeInstances,
            ["render_mesh_instances"] = meshInstances, ["shape_proxy_instances"] = proxyInstances, ["soft_proxy_instances"] = softInstances,
            ["without_render_mesh_or_shapes"] = noMesh,
            ["collider_centres_checked_against_render_bounds"] = alignChecked,
            ["collider_centres_outside_render_bounds_plus_1m"] = misaligned,
            ["solid_instances_drawn"] = treeInstances + meshInstances + proxyInstances,
            ["solid_instances_total"] = objs.Kept.Select(s => s.Instance).Distinct().Count(),
            ["sections"] = new JsonObject(Classes.Where(c => sec[c].Idx.Count > 0).Select(c => KeyValuePair.Create(c,
                (JsonNode?)$"{counts[c]} objects, {sec[c].Pos.Count} vertices, {sec[c].Idx.Count / 3} triangles"))),
            ["triangles"] = sec.Values.Sum(s => s.Idx.Count / 3),
            ["templates"] = new JsonObject(perTemplate.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
        return (sec, r);
    }

    /// Kept collider shape in UE cm: box (full extents along the rotator axes), capsule as an n-sided prism over its full
    /// length along local Y, cone from its physics vertices.
    static void AddShape(ProcMesh.Section s, ObjectColliders.Shape sh, int sides, StageBuild.Placement pl)
    {
        if (sh.Box is { } b)
        {
            var (ax, ay, az) = ObjectColliders.RotationAxes(b.Rot[0], b.Rot[1], b.Rot[2]);
            var c = b.Center;
            var corners = new List<V3>();
            foreach (var (i, j, k) in new[] { (-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1) })
                corners.Add(c + ax * (i * b.X / 2) + ay * (j * b.Y / 2) + az * (k * b.Z / 2));
            AddHull(s, corners, [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)], c);
        }
        else if (sh.Capsule is { } cp)
        {
            var (ax, ay, az) = ObjectColliders.RotationAxes(cp.Rot[0], cp.Rot[1], cp.Rot[2]);
            double half = cp.Length / 2 + cp.Radius;
            var pts = new List<V3>();
            foreach (var end in new[] { -half, half })
                for (int i = 0; i < sides; i++)
                {
                    double a = 2 * Math.PI * i / sides;
                    pts.Add(cp.Center + ay * end + ax * (Math.Cos(a) * cp.Radius) + az * (Math.Sin(a) * cp.Radius));
                }
            var faces = new List<(int, int, int, int)>();
            for (int i = 0; i < sides; i++) faces.Add((i, (i + 1) % sides, sides + (i + 1) % sides, sides + i));
            AddHull(s, pts, faces, cp.Center);
            for (int i = 1; i + 1 < sides; i++) { Tri(s, pts[0], pts[i], pts[i + 1], cp.Center); Tri(s, pts[sides], pts[sides + i], pts[sides + i + 1], cp.Center); }
        }
        else if (sh.MeshPhysics is { } mp && sh.MeshTris is { } mt)
        {
            var ue = mp.Select(p => new V3(p.X * 100, p.Z * 100, p.Y * 100)).ToList();
            var ctr = new V3(ue.Average(v => v.X), ue.Average(v => v.Y), ue.Average(v => v.Z));
            for (int t = 0; t + 2 < mt.Count; t += 6) Tri(s, ue[(int)mt[t]], ue[(int)mt[t + 1]], ue[(int)mt[t + 2]], ctr);
        }
    }

    static void AddHull(ProcMesh.Section s, List<V3> p, List<(int a, int b, int c, int d)> quads, V3 centre)
    {
        foreach (var (a, b, c, d) in quads) { Tri(s, p[a], p[b], p[c], centre); Tri(s, p[a], p[c], p[d], centre); }
    }

    /// Triangle facing away from `inside` (UE front face: cross(b - a, c - a) points against the normal).
    static void Tri(ProcMesh.Section s, V3 a, V3 b, V3 c, V3 inside)
    {
        Vector3 A = new((float)a.X, (float)a.Y, (float)a.Z), B = new((float)b.X, (float)b.Y, (float)b.Z), C = new((float)c.X, (float)c.Y, (float)c.Z);
        var cr = Vector3.Cross(B - A, C - A);
        var outward = (A + B + C) / 3 - new Vector3((float)inside.X, (float)inside.Y, (float)inside.Z);
        if (Vector3.Dot(cr, outward) > 0) { (B, C) = (C, B); cr = -cr; }
        var n = cr.LengthSquared() > 0 ? Vector3.Normalize(-cr) : Vector3.UnitZ;
        int i0 = s.Pos.Count;
        foreach (var v in new[] { A, B, C }) { s.Pos.Add(v); s.Nrm.Add(n); s.Uv.Add(Vector2.Zero); }
        s.Idx.AddRange([(uint)i0, (uint)i0 + 1, (uint)i0 + 2]);
    }

    /// Render mesh placed by the instance transform. glTF front faces are CCW (outward cross product); the DR2 -> UE map
    /// mirrors, so each triangle's order is fixed against its outward normal mapped into UE.
    static void AddMesh(ProcMesh.Section s, Mesh mesh, Matrix4x4 m, StageBuild.Placement pl, Func<Vector3, V3> ue)
    {
        int b = s.Pos.Count;
        var w = new Vector3[mesh.Pos.Length];
        for (int i = 0; i < mesh.Pos.Length; i++)
        {
            w[i] = Vector3.Transform(mesh.Pos[i], m);
            var u = ue(w[i]);
            s.Pos.Add(new Vector3((float)u.X, (float)u.Y, (float)u.Z)); s.Nrm.Add(Vector3.Zero); s.Uv.Add(Vector2.Zero);
        }
        for (int t = 0; t + 2 < mesh.Idx.Length; t += 3)
        {
            uint i0 = mesh.Idx[t], i1 = mesh.Idx[t + 1], i2 = mesh.Idx[t + 2];
            var outDr2 = Vector3.Cross(w[i1] - w[i0], w[i2] - w[i0]);
            if (outDr2.LengthSquared() == 0) continue;
            var o0 = ue(w[i0]); var o1 = ue(w[i0] + Vector3.Normalize(outDr2));
            var outUe = new Vector3((float)(o1.X - o0.X), (float)(o1.Y - o0.Y), (float)(o1.Z - o0.Z));
            int a = b + (int)i0, bb = b + (int)i1, c = b + (int)i2;
            var cr = Vector3.Cross(s.Pos[bb] - s.Pos[a], s.Pos[c] - s.Pos[a]);
            if (Vector3.Dot(cr, outUe) > 0) (bb, c) = (c, bb);
            s.Nrm[a] += outUe; s.Nrm[bb] += outUe; s.Nrm[c] += outUe;
            s.Idx.AddRange([(uint)a, (uint)bb, (uint)c]);
        }
    }

    /// UE-space axis-aligned box of a local box under the instance transform.
    static (V3 mn, V3 mx) WorldBox(Vector3 lmn, Vector3 lmx, Matrix4x4 m, Func<Vector3, V3> ue)
    {
        var mn = new V3(double.MaxValue, double.MaxValue, double.MaxValue); var mx = new V3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var x in new[] { lmn.X, lmx.X }) foreach (var y in new[] { lmn.Y, lmx.Y }) foreach (var z in new[] { lmn.Z, lmx.Z })
        {
            var u = ue(Vector3.Transform(new Vector3(x, y, z), m));
            mn = new V3(Math.Min(mn.X, u.X), Math.Min(mn.Y, u.Y), Math.Min(mn.Z, u.Z));
            mx = new V3(Math.Max(mx.X, u.X), Math.Max(mx.Y, u.Y), Math.Max(mx.Z, u.Z));
        }
        return (mn, mx);
    }

    /// Crown: the upper 70 % of the tree's render bounds (DR2 local Y is up).
    static (V3 mn, V3 mx) CrownBox(Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue)
    {
        float y0 = mesh.Min.Y + (mesh.Max.Y - mesh.Min.Y) * 0.3f;
        return WorldBox(new Vector3(mesh.Min.X, y0, mesh.Min.Z), mesh.Max, m, ue);
    }

    static void AddOctahedron(ProcMesh.Section s, (V3 mn, V3 mx) box)
    {
        var (mn, mx) = box;
        var c = (mn + mx) * 0.5;
        var top = new V3(c.X, c.Y, mx.Z); var bot = new V3(c.X, c.Y, mn.Z);
        V3[] ring = [new(mn.X, c.Y, c.Z), new(c.X, mn.Y, c.Z), new(mx.X, c.Y, c.Z), new(c.X, mx.Y, c.Z)];
        for (int i = 0; i < 4; i++) { Tri(s, ring[i], ring[(i + 1) % 4], top, c); Tri(s, ring[i], ring[(i + 1) % 4], bot, c); }
    }
}
