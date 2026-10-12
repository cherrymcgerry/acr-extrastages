using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// DR2 object collision (stage.json `objects`) -> ACR BakedSimpleCollisionData entries and small mesh colliders.
///
/// Host conventions (verified on the Hafren South BakedData, see docs/decisions.md D7):
/// - one shape per FSimpleColliderAggregatedData entry; Center world UE cm (doubles), Rotation an FRotator in degrees;
/// - FKBoxElem X/Y/Z are full extents along the rotator's local axes;
/// - FKSphylElem: Radius, Length = cylinder part (total = Length + 2 Radius), axis = the rotator's local Y
///   (all 15188 host tree trunks have local Y vertical under FRotationMatrix);
/// - RestOffset 0, Name None, bContributeToMass true, CollisionEnabled 3 (QueryAndPhysics).
/// DR2 ground surface code under a point (physics XZ metres): highest collision.glb triangle, via a uniform grid.
internal sealed class SurfaceGrid
{
    const float Cell = 4;
    readonly Dictionary<(int, int), List<int>> _cells = new();
    readonly List<(Vector3 a, Vector3 b, Vector3 c, int mat)> _tris = new();
    public readonly List<string> Materials = new();

    public SurfaceGrid(string collisionGlb, StageBuild.Placement pl)
    {
        foreach (var prim in new Glb(collisionGlb).Primitives())
        {
            int mat = Materials.Count;
            Materials.Add(prim.Material);
            int nv = prim.Positions.Length / 3;
            var pv = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                var p = pl.ToPhysics(prim.Positions[i * 3], prim.Positions[i * 3 + 1], prim.Positions[i * 3 + 2]);
                pv[i] = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            }
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3)
            {
                var a = pv[prim.Indices[t]]; var b = pv[prim.Indices[t + 1]]; var c = pv[prim.Indices[t + 2]];
                int id = _tris.Count;
                _tris.Add((a, b, c, mat));
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
                int z0 = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Cell), z1 = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Cell);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!_cells.TryGetValue((x, z), out var l)) _cells[(x, z)] = l = new List<int>();
                        l.Add(id);
                    }
            }
        }
    }

    /// Surface code of the highest triangle under (x, z) physics metres, or null.
    public string? At(double x, double z) => Top(x, z).mat;

    /// Height (physics metres) of the highest triangle under (x, z), or null.
    public double? HeightAt(double x, double z) => Top(x, z) is { mat: not null } t ? t.y : null;

    (string? mat, double y) Top(double x, double z)
    {
        if (!_cells.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var l)) return (null, 0);
        float best = float.MinValue; int mat = -1;
        foreach (var id in l)
        {
            var (a, b, c, m) = _tris[id];
            float den = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(den) < 1e-9f) continue;
            float l1 = ((b.Z - c.Z) * ((float)x - c.X) + (c.X - b.X) * ((float)z - c.Z)) / den;
            float l2 = ((c.Z - a.Z) * ((float)x - c.X) + (a.X - c.X) * ((float)z - c.Z)) / den;
            float l3 = 1 - l1 - l2;
            if (l1 < 0 || l2 < 0 || l3 < 0) continue;
            float y = l1 * a.Y + l2 * b.Y + l3 * c.Y;
            if (y > best) { best = y; mat = m; }
        }
        return mat < 0 ? (null, 0) : (Materials[mat], best);
    }
}

/// The road corridor rule of ObjectColliders: how deep (cm) a point reaches inside the gate edges over the road surface
/// itself (not verge grass) within a height band above the centreline.
internal sealed class RoadProbe
{
    public readonly RoadCorridor Road;
    readonly SurfaceGrid _surfaces;
    readonly HashSet<string> _roadSurfaces;
    public readonly double MaxPen, HMin, HMax;   // cm

    public RoadProbe(RoadCorridor road, SurfaceGrid surfaces, Toml cfg)
    {
        Road = road; _surfaces = surfaces;
        _roadSurfaces = cfg.Arr("road", "surfaces").Cast<string>().ToHashSet();
        MaxPen = cfg.Num("road", "max_penetration_m") * 100; HMin = cfg.Num("road", "car_height_min_m") * 100; HMax = cfg.Num("road", "car_height_max_m") * 100;
    }

    public bool OnRoadSurface(double x, double y) => _surfaces.At(x / 100, y / 100) is { } s && _roadSurfaces.Contains(s);

    /// Depth inside the corridor of a point (plus radius), counted only within [z + hMin, z + hMax] and over road surface.
    public double Depth(V3 q, double radius, double hMin, double hMax, bool surfaceOnly = true)
    {
        var (c, z, _) = Road.QueryNear(q.X, q.Y);
        double cl = c - radius;
        if (cl >= 0 || q.Z - radius >= z + hMax || q.Z + radius <= z + hMin) return 0;
        if (!surfaceOnly) return -cl;
        foreach (var (dx, dy) in new[] { (0.0, 0.0), (1, 0), (-1, 0), (0, 1), (0, -1) })
            if (OnRoadSurface(q.X + dx * radius, q.Y + dy * radius)) return -cl;
        return 0;
    }

    public double Depth(V3 q, double radius = 0) => Depth(q, radius, HMin, HMax);

    /// Deepest sample of a triangle (barycentric grid, at most step cm apart); triangles whose plan circle around the
    /// centroid stays outside the corridor are skipped.
    public double TriangleDepth(V3 a, V3 b, V3 c, double step, double hMin, double hMax, bool surfaceOnly = true)
    {
        var g = (a + b + c) * (1.0 / 3);
        double reach = Math.Max(Plan(a - g), Math.Max(Plan(b - g), Plan(c - g)));
        if (Road.QueryNear(g.X, g.Y).clearance > reach) return 0;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Max(Plan(b - a), Math.Max(Plan(c - b), Plan(a - c))) / step));
        double best = 0;
        for (int i = 0; i <= n; i++)
            for (int j = 0; i + j <= n; j++)
            {
                double u = (double)i / n, v = (double)j / n;
                best = Math.Max(best, Depth(a * (1 - u - v) + b * u + c * v, 0, hMin, hMax, surfaceOnly));
            }
        return best;
    }

    static double Plan(V3 d) => Math.Sqrt(d.X * d.X + d.Y * d.Y);
}

internal static class ObjectColliders
{
    public sealed class Box { public V3 Center; public double[] Rot = new double[3]; public double X, Y, Z; }
    public sealed class Capsule { public V3 Center; public double[] Rot = new double[3]; public double Radius, Length; }

    public sealed class Shape
    {
        public string Material = "", Template = "", Instance = "", Kind = "";
        public Box? Box;
        public Capsule? Capsule;
        public List<Vector3>? MeshPhysics;     // cone: vertices in physics metres
        public List<uint>? MeshTris;
        public List<V3> FootprintUe = new();   // UE cm samples for the road check
        public double FootprintRadius;         // cm added around the samples
    }

    public sealed class Result
    {
        public List<Shape> Kept = new();
        public JsonObject Report = new();
        public RoadProbe Probe = null!;
    }

    // ---- frames ----

    internal static Matrix4x4 Trs(JsonNode? t)
    {
        if (t is null) return Matrix4x4.Identity;
        if (t["matrix"] is JsonArray m && m.Count == 16)
        {
            var f = m.Select(x => x!.GetValue<float>()).ToArray();
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
        var s = t["scale"] is JsonArray sa ? new Vector3(sa[0]!.GetValue<float>(), sa[1]!.GetValue<float>(), sa[2]!.GetValue<float>()) : Vector3.One;
        var q = t["rotation"] is JsonArray ra ? new Quaternion(ra[0]!.GetValue<float>(), ra[1]!.GetValue<float>(), ra[2]!.GetValue<float>(), ra[3]!.GetValue<float>()) : Quaternion.Identity;
        var p = t["position"] is JsonArray pa ? new Vector3(pa[0]!.GetValue<float>(), pa[1]!.GetValue<float>(), pa[2]!.GetValue<float>()) : Vector3.Zero;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(p);
    }

    /// DR2 direction -> UE direction (R_y then (x, z, y) swap; determinant -1).
    static V3 Dir(StageBuild.Placement pl, Vector3 v)
    {
        double r = pl.ThetaDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        double px = c * v.X + s * v.Z, pz = -s * v.X + c * v.Z, py = v.Y;
        return new V3(px, pz, py);
    }

    static V3 Norm(V3 v) { double l = v.Length; return l < 1e-12 ? v : v * (1 / l); }
    static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// UE FRotationMatrix(pitch, yaw, roll) axes (rows X, Y, Z).
    public static (V3 X, V3 Y, V3 Z) RotationAxes(double pitch, double yaw, double roll)
    {
        double d = Math.PI / 180, SP = Math.Sin(pitch * d), CP = Math.Cos(pitch * d), SY = Math.Sin(yaw * d), CY = Math.Cos(yaw * d), SR = Math.Sin(roll * d), CR = Math.Cos(roll * d);
        return (new V3(CP * CY, CP * SY, SP),
                new V3(SR * SP * CY - CR * SY, SR * SP * SY + CR * CY, -SR * CP),
                new V3(-(CR * SP * CY + SR * SY), CY * SR - CR * SP * SY, CR * CP));
    }

    /// FMatrix::Rotator for orthonormal axes with X x Y = Z; verified by reconstruction.
    public static double[] ToRotator(V3 x, V3 y, V3 z)
    {
        double pitch = Math.Atan2(x.Z, Math.Sqrt(x.X * x.X + x.Y * x.Y)) * 180 / Math.PI;
        double yaw = Math.Atan2(x.Y, x.X) * 180 / Math.PI;
        var syAxis = new V3(-Math.Sin(yaw * Math.PI / 180), Math.Cos(yaw * Math.PI / 180), 0);
        double roll = Math.Atan2(Dot(z, syAxis), Dot(y, syAxis)) * 180 / Math.PI;
        var (rx, ry, rz) = RotationAxes(pitch, yaw, roll);
        if ((rx - x).Length > 1e-4 || (ry - y).Length > 1e-4 || (rz - z).Length > 1e-4)
            throw new InvalidOperationException($"rotator reconstruction failed for X {x} Y {y} Z {z}");
        return [pitch, yaw, roll];
    }

    /// Orthonormal right-handed frame with the given primary axis exactly and the secondary as close as possible.
    static (V3 a, V3 b) Ortho(V3 primary, V3 secondary)
    {
        var a = Norm(primary);
        var b = secondary - a * Dot(secondary, a);
        if (b.Length < 1e-6) b = Math.Abs(a.Z) < 0.9 ? Cross(a, new V3(0, 0, 1)) : Cross(a, new V3(1, 0, 0));
        return (a, Norm(b));
    }

    // ---- build ----

    public static Result Build(string stageJson, StageBuild.Placement pl, Toml cfg, RoadCorridor road,
                               Func<V3, bool> inPhysicsBounds, SurfaceGrid surfaces, IReadOnlyList<V3> centreUe)
    {
        var res = new Result();
        // which DR2 surfaces the road is made of, measured across the centreline (evidence for [road] surfaces)
        var hist = new SortedDictionary<string, int[]>();
        double[] offs = [0, 2, 4, 6];
        for (int i = 0; i + 1 < centreUe.Count; i++)
        {
            var a = centreUe[i]; var b = centreUe[i + 1];
            double hx = b.X - a.X, hy = b.Y - a.Y, hl = Math.Sqrt(hx * hx + hy * hy);
            if (hl < 1) continue;
            double rx = -hy / hl, ry = hx / hl;
            for (int k = 0; k < offs.Length; k++)
                foreach (var side in offs[k] == 0 ? new[] { 1.0 } : [-1.0, 1.0])
                {
                    double x = (a.X + rx * offs[k] * 100 * side) / 100, z = (a.Y + ry * offs[k] * 100 * side) / 100;
                    var sname = surfaces.At(x, z) ?? "(none)";
                    if (!hist.TryGetValue(sname, out var h)) hist[sname] = h = new int[offs.Length];
                    h[k]++;
                }
        }
        res.Report["surface_across_road"] = new JsonObject(hist.Select(kv => KeyValuePair.Create(kv.Key,
            (JsonNode?)string.Join(", ", offs.Select((o, k) => $"{o:0} m: {kv.Value[k]}")))));
        var j = JsonNode.Parse(File.ReadAllText(stageJson))!["objects"]!;
        var templates = j["templates"]!.AsObject();
        var skipClasses = cfg.Arr("skip", "shape_classes").Cast<string>().ToHashSet();
        var skipBreaks = cfg.Arr("skip", "breaks_on_impulse").Cast<string>().ToHashSet();
        var probe = res.Probe = new RoadProbe(road, surfaces, cfg);
        double maxPen = probe.MaxPen;
        int coneSeg = (int)cfg.Num("shapes", "cone_segments");
        var wallOverride = cfg["template_wall"];

        var skipped = new SortedDictionary<string, int>();
        var keptPer = new SortedDictionary<string, int>();
        var dropped = new SortedDictionary<string, (int n, double pen)>();
        int instances = 0, shapesSeen = 0, outside = 0;
        double worstKeptPen = 0;
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;

        foreach (var inst in j["instances"]!.AsArray())
        {
            instances++;
            var tname = inst!["template"]!.GetValue<string>();
            var mi = Trs(inst["transform"]);
            foreach (var body in templates[tname]!["bodies"]!.AsArray())
                foreach (var sh in body!["shapes"]!.AsArray())
                {
                    shapesSeen++;
                    var wall = sh!["acr_wall"]?.GetValue<string>() ?? "none";
                    var cls = sh["shape_class"]?.GetValue<string>() ?? "";
                    var brk = sh["breaks_on_impulse"]?.GetValue<string>();
                    var kind = sh["kind"]!.GetValue<string>();
                    if (wall == "none") { Skip("acr_wall none"); continue; }
                    if (wallOverride.TryGetValue(tname, out var ov) && ov is string ovs) wall = ovs;
                    if (skipClasses.Contains(cls)) { Skip($"shape class {cls}"); continue; }
                    if (brk is not null && skipBreaks.Contains(brk)) { Skip($"breaks on {brk}"); continue; }
                    if (body["static"]?.GetValue<bool>() == false) { Skip("not static"); continue; }

                    var m = Trs(sh["transform"]) * mi;
                    var dims = sh["dimensions"]!;
                    double D(string k) => dims[k]!.GetValue<double>();
                    var o = Vector3.Transform(Vector3.Zero, m);
                    var ex = Vector3.TransformNormal(Vector3.UnitX, m);
                    var ey = Vector3.TransformNormal(Vector3.UnitY, m);
                    var ez = Vector3.TransformNormal(Vector3.UnitZ, m);
                    var centre = pl.ToUe([o.X, o.Y, o.Z]);
                    var phys = pl.ToPhysics(o.X, o.Y, o.Z);
                    if (!inPhysicsBounds(phys)) { outside++; continue; }
                    V3 ux = Dir(pl, ex), uy = Dir(pl, ey), uz = Dir(pl, ez);

                    var s = new Shape { Material = wall, Template = tname, Instance = inst["id"]!.GetValue<string>(), Kind = kind };
                    switch (kind)
                    {
                        case "box":
                        {
                            var (bx, by) = Ortho(ux, uz);
                            var bz = Cross(bx, by);
                            s.Box = new Box { Center = centre, Rot = ToRotator(bx, by, bz), X = D("width") * ex.Length() * 100, Y = D("length") * ez.Length() * 100, Z = D("height") * ey.Length() * 100 };
                            for (int a = -1; a <= 1; a++) for (int b = -1; b <= 1; b++) for (int c = -1; c <= 1; c++)
                                s.FootprintUe.Add(centre + bx * (a * s.Box.X / 2) + by * (b * s.Box.Y / 2) + bz * (c * s.Box.Z / 2));
                            break;
                        }
                        case "cylinder":
                        case "sphere":
                        {
                            double r = kind == "sphere" ? D("radius") * (ex.Length() + ey.Length() + ez.Length()) / 3 : D("radius") * (ex.Length() + ez.Length()) / 2;
                            double h = kind == "sphere" ? 2 * r : D("height") * ey.Length();
                            var (cy, cx) = Ortho(kind == "sphere" ? new V3(0, 0, 1) : uy, kind == "sphere" ? new V3(1, 0, 0) : ux);
                            var cz = Cross(cx, cy);
                            if (h >= 2 * r)
                            {
                                s.Capsule = new Capsule { Center = centre, Rot = ToRotator(cx, cy, cz), Radius = r * 100, Length = (h - 2 * r) * 100 };
                                foreach (var k in new[] { -1.0, 0, 1 }) s.FootprintUe.Add(centre + cy * (k * h * 50));
                                s.FootprintRadius = r * 100;
                                s.Kind = kind == "sphere" ? "sphere->capsule" : "cylinder->capsule";
                            }
                            else
                            {
                                // squat cylinder: box 2r x h x 2r with local Z along the axis
                                var (bz, bx) = Ortho(uy, ux);
                                var by = Cross(bz, bx);
                                s.Box = new Box { Center = centre, Rot = ToRotator(bx, by, bz), X = 2 * r * 100, Y = 2 * r * 100, Z = h * 100 };
                                for (int a = -1; a <= 1; a++) for (int b = -1; b <= 1; b++) for (int c = -1; c <= 1; c++)
                                    s.FootprintUe.Add(centre + bx * (a * r * 100) + by * (b * r * 100) + bz * (c * h * 50));
                                s.Kind = "cylinder->box";
                            }
                            break;
                        }
                        case "cone":
                        {
                            double R = D("baseRadius"), H = D("fullHeight");
                            var local = new List<Vector3>();
                            for (int i = 0; i < coneSeg; i++)
                            {
                                double a = 2 * Math.PI * i / coneSeg;
                                local.Add(new Vector3((float)(R * Math.Cos(a)), (float)(-H / 4), (float)(R * Math.Sin(a))));
                            }
                            local.Add(new Vector3(0, (float)(3 * H / 4), 0));
                            s.MeshPhysics = new List<Vector3>();
                            foreach (var lv in local)
                            {
                                var w = Vector3.Transform(lv, m);
                                var p = pl.ToPhysics(w.X, w.Y, w.Z);
                                s.MeshPhysics.Add(new Vector3((float)p.X, (float)p.Y, (float)p.Z));
                                s.FootprintUe.Add(pl.ToUe([w.X, w.Y, w.Z]));
                            }
                            s.MeshTris = new List<uint>();
                            uint apex = (uint)coneSeg;
                            for (uint i = 0; i < coneSeg; i++)
                            {
                                uint n = (uint)((i + 1) % coneSeg);
                                s.MeshTris.AddRange([i, n, apex, i, apex, n]);        // both windings
                                if (i > 0 && n > 0) s.MeshTris.AddRange([0, i, n, 0, n, i]);
                            }
                            break;
                        }
                        default:
                            Skip($"kind {kind}");
                            continue;
                    }

                    // road corridor check at car height
                    double pen = Math.Max(s.FootprintUe.Max(q => probe.Depth(q, s.FootprintRadius)),
                                          VisualObjects.ShapeDepth(s, probe, probe.HMin, probe.HMax));
                    if (pen > maxPen)
                    {
                        var key = $"{tname} ({wall})";
                        var cur = dropped.GetValueOrDefault(key);
                        dropped[key] = (cur.n + 1, Math.Max(cur.pen, pen / 100));
                        continue;
                    }
                    worstKeptPen = Math.Max(worstKeptPen, pen);
                    res.Kept.Add(s);
                    var kk = $"{wall} {s.Kind}";
                    keptPer[kk] = keptPer.GetValueOrDefault(kk) + 1;
                }
        }

        var rep = res.Report;
        rep["instances"] = instances;
        rep["shapes_seen"] = shapesSeen;
        rep["outside_physics_bounds"] = outside;
        rep["skipped"] = new JsonObject(skipped.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        rep["dropped_on_road"] = new JsonObject(dropped.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)$"{kv.Value.n} (max penetration {kv.Value.pen:0.##} m)")));
        rep["dropped_on_road_total"] = dropped.Values.Sum(v => v.n);
        rep["kept_max_edge_overlap_m"] = Math.Round(worstKeptPen / 100, 3);
        rep["kept"] = new JsonObject(keptPer.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        rep["kept_total"] = res.Kept.Count;
        return res;
    }

    // ---- serialization (FSimpleColliderAggregatedData) ----

    static void Vec(BinaryWriter w, V3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static void Rot(BinaryWriter w, double[] r) { w.Write(r[0]); w.Write(r[1]); w.Write(r[2]); }

    public static byte[] Entry(Shape s, int physMaterial, byte[] ownerBytes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int arraySlot = s.Box is not null ? 0 : 2;
        Unv.WriteHeader(w, [new(arraySlot, false), new(3, false), new(4, false)]);
        w.Write(1);
        if (s.Box is { } b)
        {
            Unv.WriteHeader(w, Enumerable.Range(0, 9).Select(i => new Unv.Slot(i, i is 5 or 6)).ToList());
            Vec(w, b.Center); Rot(w, b.Rot);
            w.Write((float)b.X); w.Write((float)b.Y); w.Write((float)b.Z);
        }
        else
        {
            var c = s.Capsule!;
            Unv.WriteHeader(w, Enumerable.Range(0, 8).Select(i => new Unv.Slot(i, i is 4 or 5 || (i == 3 && c.Length == 0))).ToList());
            Vec(w, c.Center); Rot(w, c.Rot);
            w.Write((float)c.Radius);
            if (c.Length != 0) w.Write((float)c.Length);
        }
        w.Write((byte)1);   // bContributeToMass
        w.Write((byte)3);   // CollisionEnabled QueryAndPhysics
        w.Write(physMaterial);
        w.Write(ownerBytes);
        return ms.ToArray();
    }
}
