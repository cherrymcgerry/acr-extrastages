using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// Render-only geometry for the DR2 objects, appended to the visuals mesh as one section per class colour, so the
/// driver sees what is solid. Solid = the shapes ObjectColliders kept (the same list that goes into BakedData).
///   - tree trunks: the kept trunk shapes themselves (capsule -> prism, cone, box); with vegetation "crowns" also a
///     small crown octahedron high up, kept clear of the road corridor in plan
///   - other instances with kept shapes: the DR2 render mesh (render/objects.glb, one mesh per template, template-local
///     vertices placed by the instance transform) if it is at most max_mesh_triangles and does not reach into the road
///     corridor beyond what the colliders may, else the kept shapes
///   - everything not solid (soft vegetation, cloth, light breakables, objects dropped from the road corridor): not
///     drawn; their render bounds are 9-17 m wide but low, so any proxy over them hides the road
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
        StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, ObjectColliders.Result objs, Func<V3, bool> inPhysicsBounds, string vegetation)
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
        int instances = 0, outside = 0, noMesh = 0, meshInstances = 0, proxyInstances = 0, notSolid = 0, treeInstances = 0;
        int alignChecked = 0, misaligned = 0, crownsDrawn = 0, crownsDropped = 0;
        var perTemplate = new SortedDictionary<string, string>();
        bool crowns = vegetation switch { "none" => false, "crowns" => true, _ => throw new ArgumentException($"vegetation must be none or crowns, not {vegetation}") };
        double overhang = cfg.Num("objects", "overhang_height_m") * 100;
        double meshPenLimit = objs.Probe.MaxPen + cfg.Num("objects", "road_overlap_tolerance_m") * 100;
        double maxOverBounds = cfg.Num("objects", "max_extent_over_bounds");
        var fallbacks = new SortedDictionary<string, (int n, double depth)>();
        bool Fallback(string t, double d) { var f = fallbacks.GetValueOrDefault(t); fallbacks[t] = (f.n + 1, Math.Max(f.depth, d)); return false; }
        var extents = new List<(string template, string id, double ext, double bounds)>();

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

            var before = sec.ToDictionary(kv => kv.Key, kv => kv.Value.Pos.Count);
            if (tree && shapes.Count > 0)
            {
                treeInstances++;
                foreach (var s in shapes) AddShape(sec[ClassOf(s.Material)], s, sides, pl);
                counts["trunks"]++;
                string crown = "no crown";
                if (crowns && mesh is not null)
                {
                    if (Crown(mesh, m, Ue, objs.Probe.Road, cfg) is { } box) { AddOctahedron(sec["soft"], box); counts["soft"]++; crownsDrawn++; crown = "small crown"; }
                    else crownsDropped++;
                }
                perTemplate.TryAdd(tname, $"tree: trunk shapes, {crown}");
            }
            else if (shapes.Count > 0 && mesh is not null && mesh.Idx.Length / 3 <= maxMeshTris
                     && MeshDepth(mesh, m, Ue, objs.Probe, overhang) is var depth && (depth <= meshPenLimit || Fallback(tname, depth)))
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
                perTemplate.TryAdd(tname, mesh is null ? "kept shapes (no render mesh)"
                    : mesh.Idx.Length / 3 > maxMeshTris ? $"kept shapes (render mesh {mesh.Idx.Length / 3} triangles > {maxMeshTris})"
                    : "kept shapes (render mesh reaches into the road corridor)");
            }
            else if (mesh is not null)
            {
                notSolid++;
                perTemplate.TryAdd(tname, "not solid: not drawn");
            }
            else noMesh++;

            // drawn plan extent of this instance, against its DR2 render bounds
            var dmn = new Vector3(float.MaxValue); var dmx = new Vector3(float.MinValue);
            foreach (var (cls, s) in sec)
                for (int i = before[cls]; i < s.Pos.Count; i++) { dmn = Vector3.Min(dmn, s.Pos[i]); dmx = Vector3.Max(dmx, s.Pos[i]); }
            if (dmn.X <= dmx.X)
            {
                double ext = Math.Max(dmx.X - dmn.X, dmx.Y - dmn.Y) / 100;
                double bounds = double.NaN;
                if (mesh is not null) { var (bmn, bmx) = WorldBox(mesh.Min, mesh.Max, m, Ue); bounds = Math.Max(bmx.X - bmn.X, bmx.Y - bmn.Y) / 100; }
                extents.Add((tname, inst["id"]!.GetValue<string>(), ext, bounds));
            }
        }
        // kept colliders above car height over the road (ObjectColliders only checks the car-height band)
        var overhead = new JsonArray();
        foreach (var s in objs.Kept)
        {
            double d = Math.Max(s.FootprintUe.Max(q => objs.Probe.Depth(q, s.FootprintRadius, objs.Probe.HMax, overhang)),
                                ShapeDepth(s, objs.Probe, objs.Probe.HMax, overhang));
            if (d <= meshPenLimit) continue;
            var lo = s.FootprintUe.Min(q => q.Z - objs.Probe.Road.QueryNear(q.X, q.Y).z) - s.FootprintRadius;
            var c = s.FootprintUe.Aggregate((a, b) => a + b) * (1.0 / s.FootprintUe.Count);
            double reach = s.FootprintUe.Max(q => (q - c).Length) + s.FootprintRadius;
            overhead.Add(new JsonObject
            {
                ["what"] = $"{s.Template} {s.Instance} {s.Kind} {s.Material}: {d / 100:0.0} m into the corridor, lowest {lo / 100:0.0} m above the centreline",
                ["at_cm"] = new JsonArray(Math.Round(c.X), Math.Round(c.Y), Math.Round(c.Z)), ["reach_cm"] = Math.Ceiling(reach),
            });
        }
        var largest = extents.OrderByDescending(x => x.ext).Take(8).ToList();
        var overBounds = extents.Where(x => !double.IsNaN(x.bounds) && x.ext > x.bounds * maxOverBounds + 1).ToList();

        foreach (var s in sec.Values)
            for (int i = 0; i < s.Nrm.Count; i++) s.Nrm[i] = s.Nrm[i] == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(s.Nrm[i]);
        var r = new JsonObject
        {
            ["instances"] = instances, ["outside_physics_bounds"] = outside, ["trees"] = treeInstances,
            ["vegetation"] = vegetation,
            ["render_mesh_instances"] = meshInstances, ["shape_proxy_instances"] = proxyInstances,
            ["not_solid_not_drawn"] = notSolid,
            ["crowns_drawn"] = crownsDrawn, ["crowns_dropped"] = crownsDropped,
            ["render_mesh_road_fallback"] = new JsonObject(fallbacks.Select(kv => KeyValuePair.Create(kv.Key,
                (JsonNode?)$"{kv.Value.n} (render mesh {kv.Value.depth / 100:0.00} m into the corridor -> shapes)"))),
            ["without_render_mesh_or_shapes"] = noMesh,
            ["colliders_overhead_in_corridor"] = overhead,
            ["largest_extent_m"] = largest.Count > 0 ? Math.Round(largest[0].ext, 2) : 0,
            ["largest"] = new JsonArray(largest.Select(x => (JsonNode)$"{x.template} {x.id}: {x.ext:0.0} m (DR2 bounds {x.bounds:0.0} m)").ToArray()),
            ["over_bounds"] = overBounds.Count,
            ["over_bounds_first"] = new JsonArray(overBounds.Take(8).Select(x => (JsonNode)$"{x.template} {x.id}: {x.ext:0.0} m vs bounds {x.bounds:0.0} m").ToArray()),
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

    /// Deepest reach of a collider shape's hull (as drawn) into the road corridor within [hMin, hMax] over road surface.
    /// Footprint samples alone miss leaning cones and boxes whose corners are below and above the band.
    public static double ShapeDepth(ObjectColliders.Shape sh, RoadProbe probe, double hMin, double hMax)
    {
        var s = new ProcMesh.Section();
        AddShape(s, sh, 8, null!);
        double best = 0;
        for (int t = 0; t + 2 < s.Idx.Count; t += 3)
        {
            V3 P(int k) { var v = s.Pos[(int)s.Idx[t + k]]; return new(v.X, v.Y, v.Z); }
            best = Math.Max(best, probe.TriangleDepth(P(0), P(1), P(2), 50, hMin, hMax));
        }
        return best;
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

    /// Crowns mode: a small octahedron high in the tree, centred over the render bounds; radius at most a fraction of the
    /// smaller plan extent of the scaled DR2 bounds, shrunk to keep crown_road_margin_m from the road edges (null:
    /// dropped). DR2 local Y is up.
    static (V3 mn, V3 mx)? Crown(Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue, RoadCorridor road, Toml cfg)
    {
        var (wmn, wmx) = WorldBox(mesh.Min, mesh.Max, m, ue);
        double sx = Vector3.TransformNormal(Vector3.UnitX, m).Length(), sz = Vector3.TransformNormal(Vector3.UnitZ, m).Length();
        double plan = Math.Min((mesh.Max.X - mesh.Min.X) * sx, (mesh.Max.Z - mesh.Min.Z) * sz) * 100;
        double r = Math.Min(cfg.Num("objects", "crown_max_radius_m") * 100, cfg.Num("objects", "crown_radius_factor") * plan / 2);
        double bottom = wmn.Z + (wmx.Z - wmn.Z) * cfg.Num("objects", "crown_bottom_fraction");
        if (bottom - wmn.Z < cfg.Num("objects", "crown_min_bottom_m") * 100) return null;
        var c = ue(Vector3.Transform((mesh.Min + mesh.Max) / 2, m));
        double clearance = road.QueryNear(c.X, c.Y).clearance - cfg.Num("objects", "crown_road_margin_m") * 100;
        r = Math.Min(r, clearance);
        if (r < cfg.Num("objects", "crown_min_radius_m") * 100) return null;
        return (new V3(c.X - r, c.Y - r, bottom), new V3(c.X + r, c.Y + r, wmx.Z));
    }

    /// Deepest reach of a placed render mesh into the road corridor (over road surface, car_height_min_m .. overhang).
    static double MeshDepth(Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue, RoadProbe probe, double overhang)
    {
        var (bmn, bmx) = WorldBox(mesh.Min, mesh.Max, m, ue);
        var bc = (bmn + bmx) * 0.5;
        if (probe.Road.QueryNear(bc.X, bc.Y).clearance > Math.Sqrt(Math.Pow(bmx.X - bmn.X, 2) + Math.Pow(bmx.Y - bmn.Y, 2)) / 2) return 0;
        var w = mesh.Pos.Select(p => ue(Vector3.Transform(p, m))).ToArray();
        double best = 0;
        for (int t = 0; t + 2 < mesh.Idx.Length; t += 3)
            best = Math.Max(best, probe.TriangleDepth(w[mesh.Idx[t]], w[mesh.Idx[t + 1]], w[mesh.Idx[t + 2]], 50, probe.HMin, overhang));
        return best;
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
