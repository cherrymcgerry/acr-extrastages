using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using Newtonsoft.Json;
using V3 = AcrRef.Splines.V3;

namespace AcrRef;

/// v1.13 native foliage: DR2's plants drawn as ACR's own instanced foliage ([native_foliage] in config/scenery.toml).
///
/// Hafren's persistent level holds PCG "stamp" actors whose HierarchicalInstancedStaticMeshComponents carry its
/// service-park trees (all tagged ServicePark, which ACR hides during a stage; the scenery step hides them too). The
/// configured host components are reused: each gets one ACR foliage mesh (a new StaticMesh import), and its instances
/// are rebuilt from the DR2 plants that map to that mesh. Species rules map DR2 templates to ACR meshes, sized to the
/// DR2 plant's height; wide clumps (bushes, grasses) get several ACR plants scattered inside their footprint, each on
/// the DR2 ground (collision.glb). Plants standing on a road surface are dropped. Not solid: BakedData is unchanged.
///
/// HISM native data rewritten per host (layout seen in the cooked 5.6 persistent level, export data):
///   SortedInstances (int32 count + indices) and NumBuiltInstances: identity, n;
///   BuiltInstanceBounds (FBox: 6 doubles + IsValid) and CacheMeshExtendedBounds (FBoxSphereBounds: 7 doubles);
///   InstanceCountToRender (n), InstancingRandomSeed, InstanceReorderTable (int32 count + indices): identity;
///   CachedBounds[0..2] (FBoxSphereBounds, component or world space): the new bounds;
///   PerInstanceSMData: bulk array (128, n) of row-major FMatrix (doubles) relative to the stamp's DefaultSceneRoot;
///   PerInstanceSMCustomData: bulk array (4, 0), then one int32 (0 on every host seen; kept as is);
///   ClusterTree: bulk array (64, nodes) of FClusterNode (BoundMin, FirstChild, BoundMax, LastChild, FirstInstance,
///     LastInstance, MinInstanceScale, MaxInstanceScale), built here: binary median splits down to leaf_instances,
///     breadth-first, children contiguous, instances stored in tree order.
internal static class NativeFoliage
{
    internal sealed record MeshInfo(string Path, string Name, V3 Origin, V3 Extent, double Radius)
    {
        public double Height => Extent.Z * 2;
        public double Width => Math.Max(Extent.X, Extent.Y) * 2;
    }

    sealed record Rule(Regex Template, string[] Meshes, int MaxPerPlant, string Text);

    /// One instance to place: world UE position of the mesh pivot (cm) and the world images of the mesh's local X, Y
    /// and Z unit axes (rotation times scale; must keep the handedness: (X x Y) . Z > 0).
    internal readonly record struct Plant(V3 At, V3 Ax, V3 Ay, V3 Az)
    {
        public static Plant Yawed(V3 at, double yawDeg, double sxy, double sz)
        {
            double c = Math.Cos(yawDeg * Math.PI / 180), s = Math.Sin(yawDeg * Math.PI / 180);
            return new(at, new V3(c * sxy, s * sxy, 0), new V3(-s * sxy, c * sxy, 0), new V3(0, 0, sz));
        }
    }

    internal static MeshInfo LoadMesh(CUE4Parse.FileProvider.IFileProvider provider, string path)
    {
        var mesh = provider.LoadPackage("acr/Content" + path["/Game".Length..] + ".uasset").GetExports().OfType<UStaticMesh>().First();
        var b = mesh.RenderData!.Bounds!;
        return new MeshInfo(path, path[(path.LastIndexOf('/') + 1)..], new V3(b.Origin.X, b.Origin.Y, b.Origin.Z),
                            new V3(b.BoxExtent.X, b.BoxExtent.Y, b.BoxExtent.Z), b.SphereRadius);
    }

    static List<Rule> Rules(Toml cfg) => cfg.Arr("native_foliage", "species").Cast<string>().Select(s =>
    {
        // "<template regex> => <mesh path>, <mesh path> [; clump <max plants>]"
        var m = Regex.Match(s, @"^\s*(.+?)\s*=>\s*([^;]+?)\s*(?:;\s*clump\s+(\d+))?\s*$");
        if (!m.Success) throw new ArgumentException($"[native_foliage] species entry not understood: {s}");
        return new Rule(new Regex(m.Groups[1].Value), m.Groups[2].Value.Split(',').Select(x => x.Trim()).ToArray(),
                        m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 1, s);
    }).ToList();

    static ulong Hash(string s)
    {
        ulong h = 1469598103934665603;
        foreach (char c in s) { h ^= c; h *= 1099511628211; }
        return h;
    }

    /// Host actors and components the scenery step must leave visible.
    public static (HashSet<string> actors, HashSet<string> comps) Hosts(Toml cfg)
    {
        var hosts = cfg.Arr("native_foliage", "hosts").Cast<string>().ToList();
        return (hosts.Select(h => h.Split('/')[0]).ToHashSet(), hosts.ToHashSet());
    }

    public static JsonObject Build(Options o, string persistentFile, Toml cfg, StageBuild.Ir ir, StageBuild.Placement pl, ObjectColliders.Result objs)
    {
        var rules = Rules(cfg);
        var hosts = cfg.Arr("native_foliage", "hosts").Cast<string>().Select(h => (actor: h.Split('/')[0], comp: h.Split('/')[1])).ToList();
        int leaf = (int)cfg.Num("native_foliage", "leaf_instances");
        double minOffset = cfg.Num("native_foliage", "min_centreline_offset_m") * 100;

        using var provider = Program.Open(o);
        var meshes = new Dictionary<string, MeshInfo>();
        foreach (var path in rules.SelectMany(r => r.Meshes).Distinct()) meshes[path] = LoadMesh(provider, path);
        if (meshes.Count > hosts.Count) throw new ArgumentException($"[native_foliage] {meshes.Count} meshes but only {hosts.Count} host components");

        // DR2 template bounds (template space, metres; Y up)
        var tb = new Dictionary<string, (double h, double w)>();
        foreach (var g in new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().GroupBy(p => p.Mesh))
        {
            float y0 = float.MaxValue, y1 = float.MinValue, x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in g)
                for (int i = 0; i < p.Positions.Length; i += 3)
                {
                    x0 = Math.Min(x0, p.Positions[i]); x1 = Math.Max(x1, p.Positions[i]);
                    y0 = Math.Min(y0, p.Positions[i + 1]); y1 = Math.Max(y1, p.Positions[i + 1]);
                    z0 = Math.Min(z0, p.Positions[i + 2]); z1 = Math.Max(z1, p.Positions[i + 2]);
                }
            tb[g.Key] = (y1 - y0, Math.Max(x1 - x0, z1 - z0));
        }

        // plants per mesh
        var ground = new SurfaceGrid(Path.Combine(ir.Dir, "collision.glb"), pl);
        var plants = meshes.Keys.ToDictionary(k => k, _ => new List<Plant>());
        var perRule = new Dictionary<string, (int dr2, int placed)>();
        int onRoad = 0, noGround = 0;
        var centre = ir.Centre.Select(pl.ToUe).ToList();
        double NearCentre(V3 p) => centre.Min(c => (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y));
        foreach (var inst in JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!["instances"]!.AsArray())
        {
            var tname = inst!["template"]!.GetValue<string>();
            var rule = rules.FirstOrDefault(r => r.Template.IsMatch(tname));
            if (rule is null || !tb.TryGetValue(tname, out var size)) continue;
            string id = inst["id"]!.GetValue<string>();
            var tr = inst["transform"]!;
            var pos = tr["position"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            double s = tr["scale"] is JsonArray sa ? sa.Select(x => x!.GetValue<double>()).Max() : 1;
            var rng = new Random((int)(Hash(id) & 0x7FFFFFFF));
            var mesh = meshes[rule.Meshes[(int)(Hash(id) % (ulong)rule.Meshes.Length)]];
            double h = size.h * s * 100, w = size.w * s * 100;
            double sz = Math.Clamp(h / mesh.Height, 0.3, 3);
            int count = rule.MaxPerPlant <= 1 ? 1 : Math.Clamp((int)Math.Round(w / (mesh.Width * sz)), 1, rule.MaxPerPlant);
            var c0 = pl.ToUe(pos);
            var pr = perRule.GetValueOrDefault(rule.Text);
            perRule[rule.Text] = (pr.dr2 + 1, pr.placed);
            for (int k = 0; k < count; k++)
            {
                double r = count == 1 ? 0 : Math.Max(0, w / 2 - mesh.Width * sz / 2) * Math.Sqrt(rng.NextDouble()), a = rng.NextDouble() * 2 * Math.PI;
                var at = new V3(c0.X + r * Math.Cos(a), c0.Y + r * Math.Sin(a), c0.Z);
                if (objs.Probe.OnRoadSurface(at.X, at.Y) || Math.Sqrt(NearCentre(at)) < minOffset) { onRoad++; continue; }
                if (count > 1)
                {
                    if (ground.HeightAt(at.X / 100, at.Y / 100) is not { } gy) { noGround++; continue; }
                    at = at with { Z = gy * 100 };
                }
                double v = 0.85 + 0.3 * rng.NextDouble();
                plants[mesh.Path].Add(Plant.Yawed(at with { Z = at.Z - 10 }, rng.NextDouble() * 360, sz * v, sz * v));
                pr = perRule[rule.Text];
                perRule[rule.Text] = (pr.dr2, pr.placed + 1);
            }
        }

        // hosts: one mesh each, in config order (most plants first)
        var pkg = new LegacyPackage(persistentFile);
        var exports = provider.LoadPackage("acr/Content/Levels/WalesS4HafrenSouth.umap").GetExports().ToList();
        if (exports.Count != pkg.ExportCount) throw new InvalidDataException("CUE4Parse export count differs from the legacy persistent level");
        int none = pkg.AddExportName("None");
        var used = new JsonArray();
        int hi = 0;
        var dropTags = cfg.Arr("native_foliage", "drop_tags").Cast<string>().ToList();
        foreach (var (path, list) in plants.Where(kv => kv.Value.Count > 0).OrderByDescending(kv => kv.Value.Count))
        {
            var (actorName, compName) = hosts[hi++];
            RewriteHost(pkg, exports, actorName, compName, meshes[path], list, leaf, dropTags, none);
            used.Add($"{actorName}/{compName}: {meshes[path].Name} x {list.Count}");
        }
        // hosts left over keep no instances visible: their actor is kept visible only if another host of it is used
        for (; hi < hosts.Count; hi++)
        {
            var (actorName, compName) = hosts[hi];
            RewriteHost(pkg, exports, actorName, compName, null, [], leaf, [], none);
            used.Add($"{actorName}/{compName}: unused (0 instances)");
        }
        pkg.SaveTo(persistentFile);
        int total = plants.Values.Sum(l => l.Count);
        Console.WriteLine($"native foliage: {total} ACR plants ({plants.Count(kv => kv.Value.Count > 0)} meshes) from DR2 vegetation, {onRoad} dropped on or near the road");
        return new JsonObject
        {
            ["plants"] = total,
            ["dropped_on_road_or_near_centreline"] = onRoad, ["dropped_no_ground"] = noGround,
            ["hosts"] = used,
            ["rules"] = new JsonObject(perRule.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)$"{kv.Value.dr2} DR2 plants -> {kv.Value.placed} ACR plants"))),
        };
    }

    /// Rebuilds host component compName of stamp actor actorName with the given instances (mesh null: none) and
    /// renames the drop tags on its actor (none: the "None" name index).
    internal static void RewriteHost(LegacyPackage pkg, List<CUE4Parse.UE4.Assets.Exports.UObject> exports, string actorName, string compName,
                                     MeshInfo? mesh, List<Plant> list, int leaf, IEnumerable<string> dropTags, int none)
    {
        int e = Enumerable.Range(0, pkg.ExportCount).Single(i => pkg.ExportName(i) == compName && pkg.ExportName(pkg.ExportOuter(i) - 1) == actorName);
        var comp = JsonNode.Parse(JsonConvert.SerializeObject(exports[e]))!;
        var root = exports.Single(x => x.Name == "DefaultSceneRoot" && x.Outer?.Name.ToString() == actorName);
        Rewrite(pkg, e, comp, JsonNode.Parse(JsonConvert.SerializeObject(root))!["Properties"]!, mesh, list, leaf);
        DropTags(pkg, pkg.ExportOuter(e) - 1, dropTags, none, actorName);
    }

    internal static void DropTags(LegacyPackage pkg, int actor, IEnumerable<string> tags, int none, string actorName)
    {
        foreach (var tag in tags)
        {
            int ni = pkg.FindName(tag);
            if (ni < 0) continue;
            var a = pkg.GetExportData(actor);
            var hits = Find(a, Ints(1, ni));
            if (hits.Count > 1) throw new InvalidDataException($"{actorName}: tag {tag} found {hits.Count} times");
            if (hits.Count == 1) { BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(hits[0] + 4), none); pkg.SetExportData(actor, a); }
        }
    }

    internal static List<int> Find(byte[] d, byte[] pat, int from = 0, int to = int.MaxValue)
    {
        var hits = new List<int>();
        int i = d.AsSpan(from).IndexOf(pat);
        i = i < 0 ? -1 : from + i;
        while (i >= 0 && i < to)
        {
            hits.Add(i);
            int next = d.AsSpan(i + 1).IndexOf(pat);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return hits;
    }

    internal static byte[] Ints(params int[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    internal static byte[] Doubles(params double[] v)
    {
        var b = new byte[v.Length * 8];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(i * 8), v[i]);
        return b;
    }

    internal static double[] Vec(JsonNode v) => [v["X"]!.GetValue<double>(), v["Y"]!.GetValue<double>(), v["Z"]!.GetValue<double>()];

    internal static List<int> FindDoubles(byte[] d, double[] vals, int to)
    {
        var hits = new List<int>();
        for (int i = 0; i + vals.Length * 8 <= Math.Min(d.Length, to); i++)
        {
            bool ok = true;
            for (int k = 0; k < vals.Length && ok; k++) ok = Math.Abs(BitConverter.ToDouble(d, i + k * 8) - vals[k]) <= Math.Max(0.01, Math.Abs(vals[k]) * 1e-6);
            if (ok) hits.Add(i);
        }
        return hits;
    }

    sealed class Node
    {
        public Vector3 Min, Max, SMin, SMax;
        public int FirstChild = -1, LastChild = -1, FirstInstance, LastInstance;
    }

    /// Rebuilds one host HISM with the given plants and mesh (see the class comment for the layout). mesh null: no
    /// instances (an unused host of a kept actor).
    static void Rewrite(LegacyPackage pkg, int e, JsonNode comp, JsonNode rootProps, MeshInfo? mesh, List<Plant> plants, int leafSize)
    {
        var d = pkg.GetExportData(e);
        var props = comp["Properties"]!;
        int nOld = ((JsonArray)comp["PerInstanceSMData"]!).Count, nodesOld = ((JsonArray)comp["ClusterTree"]!).Count;
        var sortedOld = ((JsonArray)props["SortedInstances"]!).Select(x => x!.GetValue<int>()).ToArray();
        var reorderOld = ((JsonArray)props["InstanceReorderTable"]!).Select(x => x!.GetValue<int>()).ToArray();
        int seed = props["InstancingRandomSeed"]!.GetValue<int>();

        // offsets in the current data
        int instHdr = Enumerable.Range(0, d.Length - 8 - 128).First(i => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i)) == 128
                                                                       && BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i + 4)) == nOld
                                                                       && BitConverter.ToDouble(d, i + 8 + 120) == 1.0);
        int sortedAt = Find(d, Ints([sortedOld.Length, .. sortedOld.Take(8)]), 0, instHdr).First();   // precedes the reorder table, which can start alike
        int numBuiltAt = sortedAt + 4 + 4 * sortedOld.Length;
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(numBuiltAt)) != nOld) throw new InvalidDataException("NumBuiltInstances not after SortedInstances");
        int reorderAt = Find(d, Ints([reorderOld.Length, .. reorderOld.Take(8)]), numBuiltAt, instHdr).First();
        int countAt = reorderAt - 8;
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(countAt)) != nOld || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(countAt + 4)) != seed)
            throw new InvalidDataException("InstanceCountToRender / InstancingRandomSeed not before InstanceReorderTable");
        int reorderEnd = reorderAt + 4 + 4 * reorderOld.Length;
        int customAt = instHdr + 8 + 128 * nOld;
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(customAt + 4)) != 0) throw new InvalidDataException("PerInstanceSMCustomData not empty");
        int extra = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(customAt + 8));   // 0 on every host seen
        int nodesHdr = customAt + 12;
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nodesHdr)) != 64 || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nodesHdr + 4)) != nodesOld || nodesHdr + 8 + 64 * nodesOld != d.Length)
            throw new InvalidDataException("ClusterTree not at the end of the export");

        // local frame of the stamp root (translation + yaw only)
        V3 rootLoc = rootProps["RelativeLocation"] is { } rl ? new(rl["X"]!.GetValue<double>(), rl["Y"]!.GetValue<double>(), rl["Z"]!.GetValue<double>()) : new(0, 0, 0);
        var rr = rootProps["RelativeRotation"];
        double rootYaw = rr?["Yaw"]?.GetValue<double>() ?? 0;
        if (rr is not null && new[] { "Pitch", "Roll" }.Any(k => Math.Abs(rr[k]!.GetValue<double>()) > 1e-3) || rootProps["RelativeScale3D"] is not null)
            throw new InvalidDataException("stamp root is pitched, rolled or scaled; only translation + yaw is supported");
        double rc = Math.Cos(rootYaw * Math.PI / 180), rs = Math.Sin(rootYaw * Math.PI / 180);
        V3 ToLocal(V3 w) { var q = w - rootLoc; return new(rc * q.X + rs * q.Y, -rs * q.X + rc * q.Y, q.Z); }
        V3 DirLocal(V3 q) => new(rc * q.X + rs * q.Y, -rs * q.X + rc * q.Y, q.Z);
        V3 ToWorld(V3 l) => new V3(rc * l.X - rs * l.Y, rs * l.X + rc * l.Y, l.Z) + rootLoc;

        // instances (local), their bounds, then the tree (instances reordered into tree order)
        int n = plants.Count;
        var mats = new double[n][];
        var bmin = new Vector3[n]; var bmax = new Vector3[n]; var scl = new Vector3[n];
        for (int k = 0; k < n; k++)
        {
            var p = plants[k];
            var l = ToLocal(p.At);
            V3 ax = DirLocal(p.Ax), ay = DirLocal(p.Ay), az = DirLocal(p.Az);
            mats[k] = [ax.X, ax.Y, ax.Z, 0, ay.X, ay.Y, ay.Z, 0, az.X, az.Y, az.Z, 0, l.X, l.Y, l.Z, 1];
            // instance bounds: the 8 corners of the mesh box, transformed
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            foreach (var sx in new[] { -1, 1 }) foreach (var sy in new[] { -1, 1 }) foreach (var sz2 in new[] { -1, 1 })
            {
                double qx = mesh!.Origin.X + sx * mesh.Extent.X, qy = mesh.Origin.Y + sy * mesh.Extent.Y, qz = mesh.Origin.Z + sz2 * mesh.Extent.Z;
                var w = new Vector3((float)(l.X + ax.X * qx + ay.X * qy + az.X * qz), (float)(l.Y + ax.Y * qx + ay.Y * qy + az.Y * qz), (float)(l.Z + ax.Z * qx + ay.Z * qy + az.Z * qz));
                lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
            }
            bmin[k] = lo; bmax[k] = hi;
            scl[k] = new Vector3((float)ax.Length, (float)ay.Length, (float)az.Length);
        }
        var order = Enumerable.Range(0, n).ToArray();
        var nodes = new List<Node>();
        if (n > 0)
        {
            var queue = new Queue<(int node, int lo, int hiEx)>();
            nodes.Add(new Node());
            queue.Enqueue((0, 0, n));
            while (queue.Count > 0)
            {
                var (ni, lo, hiEx) = queue.Dequeue();
                var nd = nodes[ni];
                nd.FirstInstance = lo; nd.LastInstance = hiEx - 1;
                nd.Min = new Vector3(float.MaxValue); nd.Max = new Vector3(float.MinValue);
                nd.SMin = new Vector3(float.MaxValue); nd.SMax = new Vector3(float.MinValue);
                for (int i = lo; i < hiEx; i++)
                {
                    nd.Min = Vector3.Min(nd.Min, bmin[order[i]]); nd.Max = Vector3.Max(nd.Max, bmax[order[i]]);
                    nd.SMin = Vector3.Min(nd.SMin, scl[order[i]]); nd.SMax = Vector3.Max(nd.SMax, scl[order[i]]);
                }
                if (hiEx - lo <= leafSize) continue;
                var ext = nd.Max - nd.Min;
                bool byX = ext.X >= ext.Y;
                Array.Sort(order, lo, hiEx - lo, Comparer<int>.Create((a, b) =>
                    (byX ? bmin[a].X + bmax[a].X : bmin[a].Y + bmax[a].Y).CompareTo(byX ? bmin[b].X + bmax[b].X : bmin[b].Y + bmax[b].Y)));
                int mid = (lo + hiEx) / 2;
                nd.FirstChild = nodes.Count; nd.LastChild = nodes.Count + 1;
                nodes.Add(new Node()); nodes.Add(new Node());
                queue.Enqueue((nd.FirstChild, lo, mid)); queue.Enqueue((nd.LastChild, mid, hiEx));
            }
        }

        // fixed-size fields first (offsets below countAt do not move until the arrays are spliced)
        var lo3 = n > 0 ? nodes[0].Min : Vector3.Zero; var hi3 = n > 0 ? nodes[0].Max : Vector3.Zero;
        var corners = (from x in new[] { lo3.X, hi3.X } from y in new[] { lo3.Y, hi3.Y } from z in new[] { lo3.Z, hi3.Z } select ToWorld(new V3(x, y, z))).ToList();
        var wlo = new V3(corners.Min(q => q.X), corners.Min(q => q.Y), corners.Min(q => q.Z));
        var whi = new V3(corners.Max(q => q.X), corners.Max(q => q.Y), corners.Max(q => q.Z));
        var built = props["BuiltInstanceBounds"]!;
        var oldMin = Vec(built["Min"]!); var oldMax = Vec(built["Max"]!);
        int builtAt = FindDoubles(d, [.. oldMin, .. oldMax], countAt).Single();
        Doubles(lo3.X, lo3.Y, lo3.Z, hi3.X, hi3.Y, hi3.Z).CopyTo(d, builtAt);
        if (mesh is not null && props["CacheMeshExtendedBounds"] is { } cmb)
            foreach (int at in FindDoubles(d, [.. Vec(cmb["Origin"]!), .. Vec(cmb["BoxExtent"]!)], countAt))
                Doubles(mesh.Origin.X, mesh.Origin.Y, mesh.Origin.Z, mesh.Extent.X, mesh.Extent.Y, mesh.Extent.Z, mesh.Radius).CopyTo(d, at);
        var oldLocalCentre = new V3((oldMin[0] + oldMax[0]) / 2, (oldMin[1] + oldMax[1]) / 2, (oldMin[2] + oldMax[2]) / 2);
        foreach (var key in new[] { "CachedBounds", "CachedBounds[1]", "CachedBounds[2]" })
        {
            if (props[key]?["Value"] is not { } v) continue;
            var o0 = Vec(v["Origin"]!);
            bool local = (new V3(o0[0], o0[1], o0[2]) - oldLocalCentre).Length < 100;
            var (a, b) = local ? (new V3(lo3.X, lo3.Y, lo3.Z), new V3(hi3.X, hi3.Y, hi3.Z)) : (wlo, whi);
            var c = (a + b) * 0.5; var x = (b - a) * 0.5;
            foreach (int at in FindDoubles(d, [.. o0, .. Vec(v["BoxExtent"]!)], instHdr))
                Doubles(c.X, c.Y, c.Z, x.X, x.Y, x.Z, x.Length).CopyTo(d, at);
        }

        // mesh: a new import, the StaticMesh property and its preload dependency retargeted
        if (mesh is not null)
        {
            string oldName = props["StaticMesh"]!["ObjectName"]!.GetValue<string>().Split('\'')[1];
            int oldMesh = Enumerable.Range(0, pkg.ImportCount).Where(i => pkg.ImportClassName(i) == "StaticMesh" && pkg.ImportName(i) == oldName).Select(i => -i - 1).First();
            int pkgImp = pkg.AddImport("/Script/CoreUObject", "Package", 0, mesh.Path);
            int newMesh = pkg.AddImport("/Script/Engine", "StaticMesh", pkgImp, mesh.Name);
            // StaticMesh comes after InstanceReorderTable in property order
            var meshRef = Find(d, Ints(oldMesh), reorderEnd, instHdr);
            if (meshRef.Count != 1) throw new InvalidDataException($"StaticMesh reference {oldMesh} found {meshRef.Count} times between InstanceReorderTable and PerInstanceSMData");
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(meshRef[0]), newMesh);
            foreach (var dep in pkg.ExportDeps(e).Where(x => x.index == oldMesh)) pkg.SetDep(dep.pos, newMesh);
        }

        // assemble: [.. sorted) sorted numBuilt [.. count) count seed reorder [.. instHdr) instances custom nodes
        var ident = Enumerable.Range(0, n).ToArray();
        using var ms = new MemoryStream();
        ms.Write(d, 0, sortedAt);
        ms.Write(Ints([n, .. ident])); ms.Write(Ints(n));
        ms.Write(d, numBuiltAt + 4, countAt - (numBuiltAt + 4));
        ms.Write(Ints(n, seed)); ms.Write(Ints([n, .. ident]));
        ms.Write(d, reorderEnd, instHdr - reorderEnd);
        ms.Write(Ints(128, n));
        foreach (int k in order) ms.Write(Doubles(mats[k]));
        ms.Write(Ints(4, 0, extra));
        ms.Write(Ints(64, nodes.Count));
        var nb = new byte[64];
        foreach (var nd in nodes)
        {
            void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(nb.AsSpan(at), v);
            void I(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(nb.AsSpan(at), v);
            F(0, nd.Min.X); F(4, nd.Min.Y); F(8, nd.Min.Z); I(12, nd.FirstChild);
            F(16, nd.Max.X); F(20, nd.Max.Y); F(24, nd.Max.Z); I(28, nd.LastChild);
            I(32, nd.FirstInstance); I(36, nd.LastInstance);
            F(40, nd.SMin.X); F(44, nd.SMin.Y); F(48, nd.SMin.Z); F(52, nd.SMax.X); F(56, nd.SMax.Y); F(60, nd.SMax.Z);
            ms.Write(nb);
        }
        pkg.SetExportData(e, ms.ToArray());
    }
}
