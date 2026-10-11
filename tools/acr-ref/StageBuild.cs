using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using static AcrRef.Splines;

namespace AcrRef;

/// `stage-build`: ports a DR2 stage IR into the Hafren South forward variant (editor-less), writing modified legacy
/// packages for BakedData, the forward gameplay cell, DT_TracksVariants and the forward pacenote table.
/// `stage-check`: offline verification of such packages (also used on a retoc round trip of the built container).
///
/// Frames: DR2 (x, y, z) metres, Y up, right-handed. ACR physics (x, y, z) metres, Y up = UE (X, Z, Y) / 100, which is
/// also right-handed, so DR2 -> physics is a rotation about Y plus a translation (no mirroring):
///   physics = R_y(theta) * (dr2 - c) + t,   with c the DR2 route bbox centre (XZ) and t = (tx, dy, tz).
internal static class StageBuild
{
    // Host limits in physics metres (x = UE X, z = UE Y, y = UE Z). PhysicsWorldBounds of WalesS4HafrenSouth, and the
    // safe XZ region = forward gameplay cell content bounds grown by its 640 m loading range, intersected with them.
    internal const double PhysMinX = -2048, PhysMaxX = 802, PhysMinZ = -964, PhysMaxZ = 2048;
    const double SafeMinX = -1950, SafeMaxX = 802, SafeMinZ = -964, SafeMaxZ = 2048;
    const double GroundBase = 380;                 // physics y of the DR2 collision minimum (bounds y 305..534)
    const double HostFlatMin = 306, HostFlatMax = 318;
    const double ChunkSize = 100;                  // m, XZ grid for DR2 collision chunks
    const int ChunkMaxVerts = 60000;
    const double ContourPush = 20;                 // m outside the gate edges
    const int HostTemplateCollider = 10;

    internal sealed record Placement(double ThetaDeg, double Cx, double Cz, double Tx, double Tz, double Dy, double Slack)
    {
        public V3 ToPhysics(double x, double y, double z)
        {
            double r = ThetaDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r), dx = x - Cx, dz = z - Cz;
            return new V3(c * dx + s * dz + Tx, y + Dy, -s * dx + c * dz + Tz);
        }
        public V3 ToPhysics(double[] d) => ToPhysics(d[0], d[1], d[2]);
        public V3 ToUe(double[] d) { var p = ToPhysics(d); return new V3(p.X * 100, p.Z * 100, p.Y * 100); }
        public double YawOffsetDeg => ThetaDeg;
    }

    internal sealed class Ir
    {
        public string Id = "", Name = "";
        public double LengthM, TotalM;
        public List<double[]> Centre = new();
        public List<(double distance, double[] crossing, double[] left, double[] right)> Gates = new();
        public List<(string type, int gate, double distance)> Timing = new();
        public List<(string type, string name, double[] position)> Triggers = new();
        public Dictionary<string, string> Materials = new();   // gltf material -> ACR asset path
        public double[] CollisionMin = [], CollisionMax = [];
        public long CollisionTriangles;
        public string Dir = "";

        public static Ir Load(string dir)
        {
            var j = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "stage.json")))!;
            double[] V(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            var ir = new Ir
            {
                Dir = dir, Id = j["id"]!.GetValue<string>(), Name = j["name"]!.GetValue<string>(),
                LengthM = j["length_m"]!.GetValue<double>(), TotalM = j["total_distance_m"]!.GetValue<double>(),
                CollisionMin = V(j["collision"]!["bounds"]!["min"]), CollisionMax = V(j["collision"]!["bounds"]!["max"]),
                CollisionTriangles = j["collision"]!["triangles"]!.GetValue<long>(),
            };
            foreach (var c in j["centreline"]!.AsArray()) ir.Centre.Add(V(c));
            foreach (var g in j["gates"]!.AsArray())
                ir.Gates.Add((g!["distance"]!.GetValue<double>(), V(g["crossing"]), V(g["left"]), V(g["right"])));
            foreach (var t in j["timing"]!.AsArray())
                ir.Timing.Add((t!["type"]!.GetValue<string>(), t["gate"]!.GetValue<int>(), t["distance"]!.GetValue<double>()));
            foreach (var t in j["triggers"]?.AsArray() ?? [])
                ir.Triggers.Add((t!["type"]!.GetValue<string>(), t["name"]!.GetValue<string>(), V(t["position"])));
            foreach (var m in j["collision"]!["meshes"]!.AsArray())
                ir.Materials[m!["gltf_material"]!.GetValue<string>()] = m["acr_asset_path"]!.GetValue<string>();
            if (ir.Gates.Count != ir.Centre.Count) throw new InvalidDataException("expected one gate per centreline point");
            return ir;
        }
    }

    internal const string PhysMatPrefix = "/Game/Data/Gameplay/PhysicalMaterials/";

    /// Registered surfaces from a CUE4Parse export (acr-ref export) of DA_PhysMaterialsMappingCommon, as /Game asset paths.
    internal static HashSet<string> LoadSurfaceRegistry(string exportJson)
    {
        var set = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(exportJson), "\"ObjectPath\"\\s*:\\s*\"(/Game/[^\"]+?)\\.\\d+\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        if (set.Count < 10) throw new InvalidDataException($"{exportJson}: only {set.Count} registered physical materials; not a DA_PhysMaterialsMappingCommon export?");
        return set;
    }

    static string Need(Options o, string k) => o.Named.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");
    static string F(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public static int Run(Options o)
    {
        var ir = Ir.Load(Need(o, "ir"));
        var legacy = Need(o, "legacy");
        var outRoot = o.Out ?? throw new ArgumentException("--out <dir> is required");
        var reportPath = o.Named.GetValueOrDefault("report") ?? Path.Combine(outRoot, "stage-report.json");
        bool keepPacenotes = o.Named.GetValueOrDefault("keep-pacenotes") is "1" or "true";
        var report = new JsonObject { ["stage"] = ir.Id, ["name"] = ir.Name, ["host"] = "WalesS4HafrenSouth forward (variant 1/2 \"Afon Bidno - Severn\")" };

        double groundOffset = double.Parse(o.Named.GetValueOrDefault("ground-offset", "0"), CultureInfo.InvariantCulture);
        var pl = Solve(ir, groundOffset);
        Console.WriteLine($"placement: theta {F(pl.ThetaDeg)} deg about DR2 ({F(pl.Cx)}, {F(pl.Cz)}), t = ({F(pl.Tx)}, {F(pl.Dy)}, {F(pl.Tz)}) m, slack {F(pl.Slack, "0.#")} m");
        report["placement"] = new JsonObject
        {
            ["formula"] = "physics = R_y(theta) * (dr2 - (cx, 0, cz)) + (tx, dy, tz); UE cm = (px, pz, py) * 100; R_y: x' = cos*x + sin*z, z' = -sin*x + cos*z",
            ["ground_offset_m"] = groundOffset, ["theta_deg"] = pl.ThetaDeg, ["cx"] = pl.Cx, ["cz"] = pl.Cz, ["tx"] = pl.Tx, ["dy"] = pl.Dy, ["tz"] = pl.Tz, ["slack_m"] = pl.Slack,
        };

        var objectsCfg = o.Named.GetValueOrDefault("objects");
        var pacenoteMap = o.Named.GetValueOrDefault("pacenote-map");
        var respawnMode = o.Named.GetValueOrDefault("respawn", "root");
        if (respawnMode is not ("root" or "sink" or "relay")) throw new ArgumentException("--respawn must be root (v1), sink or relay");
        report["options"] = new JsonObject
        {
            ["objects"] = objectsCfg, ["pacenote_map"] = pacenoteMap, ["respawn"] = respawnMode, ["keep_pacenotes"] = keepPacenotes,
            ["visuals"] = o.Named.GetValueOrDefault("visuals"), ["ground_offset_m"] = groundOffset,
        };
        if (o.Named.ContainsKey("visuals"))
        {
            var vo = Visuals.Options.From(o.Named);
            report["options"]!["visuals_host"] = vo.Host;
            report["options"]!["visuals_retarget"] = vo.Retarget;
            report["options"]!["visuals_owner"] = vo.Owner;
            report["options"]!["visuals_vegetation"] = vo.Vegetation;
            report["options"]!["visuals_canopies"] = vo.Canopies;
        }
        var road = Road(ir, pl);

        // Kunos' track physics only knows the surfaces listed in DA_PhysMaterialsMappingCommon; a collider whose physical
        // material is not listed gets no surface and crashes the stage load (Track.cpp, trackData.surfaceDef).
        var registry = LoadSurfaceRegistry(Need(o, "surface-registry"));
        var groundFallback = o.Named.GetValueOrDefault("unregistered-ground", PhysMatPrefix + "PM_SurfaceGrass");
        if (!registry.Contains(groundFallback)) throw new ArgumentException($"--unregistered-ground {groundFallback} is not a registered surface");
        var remap = new JsonObject();
        foreach (var (gltf, asset) in ir.Materials.ToList())
            if (!registry.Contains(asset)) { ir.Materials[gltf] = groundFallback; remap[gltf] = $"{asset} (not a registered surface) -> {groundFallback}"; }
        report["surface_registry"] = new JsonObject { ["registered"] = registry.Count, ["remapped"] = remap };
        if (remap.Count > 0) Console.WriteLine($"surfaces: remapped unregistered {string.Join(", ", remap.Select(kv => kv.Key))} -> {groundFallback}");

        int hostColliders = BuildBaked(ir, pl, legacy, outRoot, report, objectsCfg is null ? null : Toml.Load(objectsCfg), road, registry, out var objectShapes);
        report["host_collider_count"] = hostColliders;
        var cellInfo = BuildCell(o, ir, pl, legacy, outRoot, report, respawnMode, road, objectShapes);
        BuildTables(o, ir, legacy, outRoot, keepPacenotes, report, pacenoteMap is null ? null : Toml.Load(pacenoteMap), cellInfo);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report -> {reportPath}");

        int failures = Check(outRoot, ir, pl, hostColliders, Console.Out, report);
        return failures == 0 ? 0 : 5;
    }

    internal static RoadCorridor Road(Ir ir, Placement pl) =>
        new(ir.Centre.Select(pl.ToUe).ToList(), ir.Gates.Select(g => pl.ToUe(g.left)).ToList(), ir.Gates.Select(g => pl.ToUe(g.right)).ToList());

    static bool InPhysicsBounds(V3 p) => p.X >= PhysMinX && p.X <= PhysMaxX && p.Z >= PhysMinZ && p.Z <= PhysMaxZ;

    /// What the pacenote table needs from the rebuilt cell: CenterSpline arc length per DR2 gate (m) and the
    /// start line / finish line / stop zone positions along it (m).
    internal sealed record CellInfo(double[] GateS, List<V3> CentreUe, double StartS, double FinishS, double StopS);

    // ---------------- placement ----------------

    internal static Placement Solve(Ir ir, double groundOffset = 0)
    {
        var pts = ir.Gates.SelectMany(g => new[] { g.crossing, g.left, g.right }).Select(p => (x: p[0], z: p[2])).ToList();
        double cx = (pts.Min(p => p.x) + pts.Max(p => p.x)) / 2, cz = (pts.Min(p => p.z) + pts.Max(p => p.z)) / 2;
        double safeW = SafeMaxX - SafeMinX, safeH = SafeMaxZ - SafeMinZ;
        (double slack, double deg, double rcx, double rcz) best = (double.MinValue, 0, 0, 0);
        for (int deg = 0; deg < 360; deg++)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var (x, z) in pts)
            {
                double dx = x - cx, dz = z - cz, px = c * dx + s * dz, pz = -s * dx + c * dz;
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minZ = Math.Min(minZ, pz); maxZ = Math.Max(maxZ, pz);
            }
            double slack = Math.Min((safeW - (maxX - minX)) / 2, (safeH - (maxZ - minZ)) / 2);
            if (slack > best.slack + 1e-6) best = (slack, deg, (minX + maxX) / 2, (minZ + maxZ) / 2);
        }
        if (best.slack < 30) throw new InvalidOperationException($"route does not fit the host's safe region (slack {best.slack:0.#} m)");
        double tx = (SafeMinX + SafeMaxX) / 2 - best.rcx, tz = (SafeMinZ + SafeMaxZ) / 2 - best.rcz;
        return new Placement(best.deg, cx, cz, tx, tz, GroundBase + groundOffset - ir.CollisionMin[1], best.slack);
    }

    // ---------------- BakedData ----------------

    sealed class Chunk
    {
        public string Material = "";
        public List<Vector3> V = new();
        public Dictionary<uint, uint> Map = new();
        public List<uint> I = new();
    }

    static int BuildBaked(Ir ir, Placement pl, string legacy, string outRoot, JsonObject report, Toml? objectsCfg, RoadCorridor road,
                          HashSet<string> registry, out ObjectColliders.Result? objectShapes)
    {
        var path = HostPaths.Resolve(legacy, HostPaths.Baked);
        var pkg = new LegacyPackage(path);
        var data = File.ReadAllBytes(Path.ChangeExtension(path, ".uexp"));
        var (colliders, countAt, simpleStart) = BakedEdit.ParseColliders(data);
        var (mapStart, meshMap, simpleMapStart) = BakedEdit.LocateMaps(data, simpleStart);
        var simple = SimpleColliders.ParseExact(data, simpleStart, mapStart);
        var r = new JsonObject();
        report["baked"] = r;

        // host winding convention, from triangles that face mostly up or down
        long up = 0, down = 0;
        foreach (var c in colliders)
            for (int t = 0; t + 2 < c.Indices.Length; t += 3)
            {
                var n = Normal(c.Positions, c.Indices[t], c.Indices[t + 1], c.Indices[t + 2]);
                if (Math.Abs(n.Y) < 0.5f * n.Length()) continue;
                if (n.Y > 0) up++; else down++;
            }
        bool flip = down > up;
        Console.WriteLine($"host ground winding: {up} up-facing vs {down} down-facing (right-handed normals) -> DR2 triangles {(flip ? "flipped" : "kept CCW-from-above")}");
        r["host_up_facing"] = up; r["host_down_facing"] = down; r["dr2_winding_flipped"] = flip;

        // flatten host mesh colliders into a thin layer below the DR2 ground (owners and stride maps are kept)
        float hostMin = colliders.Min(c => c.Min.Y), hostMax = colliders.Max(c => c.Max.Y);
        foreach (var c in colliders)
            for (int k = 0; k < c.Positions.Length / 3; k++)
            {
                int at = c.PositionsAt + 4 + (k * 3 + 1) * 4;
                float y = BitConverter.ToSingle(data, at);
                BitConverter.GetBytes((float)(HostFlatMin + (y - hostMin) * (HostFlatMax - HostFlatMin) / (hostMax - hostMin))).CopyTo(data, at);
            }
        r["host_mesh_y_before"] = $"{F(hostMin)}..{F(hostMax)}"; r["host_mesh_y_after"] = $"{HostFlatMin}..{HostFlatMax}";

        // simple colliders (tree trunks etc.): centres are world UE cm; squash their heights the same way
        // (a few are parked far below the world already; those are left alone)
        static bool InWorld(double z) => z is > 25000 and < 60000;
        var zs = simple.CenterAt.Select(a => BitConverter.ToDouble(data, a + 16)).Where(InWorld).ToList();
        double zMin = zs.Min(), zMax = zs.Max();
        foreach (var a in simple.CenterAt)
        {
            double z = BitConverter.ToDouble(data, a + 16);
            if (InWorld(z)) BitConverter.GetBytes(HostFlatMin * 100 + (z - zMin) * 0.05).CopyTo(data, a + 16);
        }
        r["host_simple"] = $"{simple.Boxes} boxes, {simple.Capsules} capsules; {zs.Count} centres at Z {F(zMin / 100)}..{F(zMax / 100)} m -> {HostFlatMin}..{F(HostFlatMin + (zMax - zMin) * 0.0005)} m, " +
                           $"{simple.CenterAt.Count - zs.Count} already below the world left alone";
        Console.WriteLine($"simple colliders: {r["host_simple"]}");

        // DR2 collision, chunked by material and XZ grid
        var glb = new Glb(Path.Combine(ir.Dir, "collision.glb"));
        var chunks = new Dictionary<(string, int, int), List<Chunk>>();
        long kept = 0, cropped = 0;
        foreach (var prim in glb.Primitives())
        {
            if (!ir.Materials.ContainsKey(prim.Material)) throw new InvalidDataException($"collision material {prim.Material} not in stage.json");
            int nv = prim.Positions.Length / 3;
            var pv = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                var p = pl.ToPhysics(prim.Positions[i * 3], prim.Positions[i * 3 + 1], prim.Positions[i * 3 + 2]);
                pv[i] = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            }
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3)
            {
                uint a = prim.Indices[t], b = prim.Indices[t + 1], c = prim.Indices[t + 2];
                float cx = (pv[a].X + pv[b].X + pv[c].X) / 3, cz = (pv[a].Z + pv[b].Z + pv[c].Z) / 3;
                if (cx < PhysMinX || cx > PhysMaxX || cz < PhysMinZ || cz > PhysMaxZ) { cropped++; continue; }
                var key = (prim.Material, (int)Math.Floor(cx / ChunkSize), (int)Math.Floor(cz / ChunkSize));
                if (!chunks.TryGetValue(key, out var list)) chunks[key] = list = new List<Chunk> { new() { Material = prim.Material } };
                var ch = list[^1];
                if (ch.V.Count > ChunkMaxVerts - 3) { ch = new Chunk { Material = prim.Material }; list.Add(ch); }
                uint Add(uint v)
                {
                    if (!ch.Map.TryGetValue(v, out var ni)) { ni = (uint)ch.V.Count; ch.Map[v] = ni; ch.V.Add(pv[v]); }
                    return ni;
                }
                uint na = Add(a), nb = Add(b), nc = Add(c);
                if (flip) { ch.I.Add(na); ch.I.Add(nc); ch.I.Add(nb); } else { ch.I.Add(na); ch.I.Add(nb); ch.I.Add(nc); }
                kept++;
            }
        }
        var all = chunks.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).Where(c => c.I.Count > 0).ToList();
        Console.WriteLine($"DR2 collision: {kept} triangles kept, {cropped} cropped outside the physics bounds, {all.Count} chunks");
        int groundChunks = all.Count;

        // DR2 objects: simple shapes -> new simple entries; cones -> small mesh colliders appended after the ground
        ObjectColliders.Result? objs = null;
        const string ObjPrefix = "/Game/Data/Gameplay/PhysicalMaterials/";
        if (objectsCfg is not null)
        {
            var surfaces = new SurfaceGrid(Path.Combine(ir.Dir, "collision.glb"), pl);
            objs = ObjectColliders.Build(Path.Combine(ir.Dir, "stage.json"), pl, objectsCfg, road, InPhysicsBounds, surfaces, ir.Centre.Select(pl.ToUe).ToList());
            var objRemap = new JsonObject();
            foreach (var s in objs.Kept.Where(s => !registry.Contains(ObjPrefix + s.Material)))
            {
                objRemap[s.Material] = $"not a registered surface -> PM_WallBasic";
                s.Material = "PM_WallBasic";
            }
            objs.Report["unregistered_remapped"] = objRemap;
            r["objects"] = objs.Report;
            Console.WriteLine($"objects: {objs.Report["kept_total"]} shapes kept, {objs.Report["dropped_on_road_total"]} dropped on the road, skipped {objs.Report["skipped"]!.ToJsonString()}");
            foreach (var g in objs.Kept.Where(s => s.MeshPhysics is not null).GroupBy(s => s.Material))
            {
                var ch = new Chunk { Material = ObjPrefix + g.Key };
                foreach (var s in g)
                {
                    if (ch.V.Count + s.MeshPhysics!.Count > ChunkMaxVerts) { all.Add(ch); ch = new Chunk { Material = ch.Material }; }
                    uint b = (uint)ch.V.Count;
                    ch.V.AddRange(s.MeshPhysics);
                    ch.I.AddRange(s.MeshTris!.Select(i => i + b));
                }
                if (ch.I.Count > 0) all.Add(ch);
            }
        }

        // physical materials (new imports for any the host does not reference)
        var pmIndex = new Dictionary<string, int>();
        var added = new JsonArray();
        foreach (var asset in ir.Materials.Values.Concat(objs?.Kept.Select(s => ObjPrefix + s.Material) ?? []).Distinct())
        {
            if (!registry.Contains(asset)) throw new InvalidDataException($"{asset} is not a registered surface (DA_PhysMaterialsMappingCommon)");
            pmIndex[asset] = ResolveImport(pkg, asset, out bool isNew);
            if (isNew) added.Add(asset);
        }
        r["added_material_imports"] = added;
        string AssetOf(Chunk c) => c.Material.StartsWith('/') ? c.Material : ir.Materials[c.Material];

        var reference = colliders[HostTemplateCollider];
        using var ms = new MemoryStream();
        ms.Write(data, 0, countAt);
        ms.Write(BitConverter.GetBytes(colliders.Count + all.Count));
        ms.Write(data, countAt + 4, simpleStart - countAt - 4);
        var perMaterial = new Dictionary<string, long>();
        foreach (var ch in all)
        {
            ms.Write(BakedEdit.BuildCollider(data, reference, ch.V, ch.I, pmIndex[AssetOf(ch)]));
            perMaterial[ch.Material] = perMaterial.GetValueOrDefault(ch.Material) + ch.I.Count / 3;
        }
        BakedEdit.MapEntry NoneKey(List<BakedEdit.MapEntry> map, string which) =>
            map.SingleOrDefault(e => pkg.NameAt(e.NameIndex, e.NameNumber) == "None")
            ?? throw new InvalidOperationException($"no always-active (None) key in the {which} stride map");
        NoneKey(meshMap, "mesh").Strides.Add(new BakedEdit.Stride { Index = (uint)colliders.Count, Num = (uint)all.Count });

        int simpleAdded = 0;
        if (objs is not null && objs.Kept.Any(s => s.MeshPhysics is null))
        {
            // owner of the new entries: the host's foliage actor (the owner of its tree-trunk capsules)
            int ownerEntry = Enumerable.Range(0, simple.Owners.Count).First(i =>
                System.Text.Encoding.ASCII.GetString(data, simple.Owners[i].At, simple.Owners[i].End - simple.Owners[i].At).Contains("InstancedFoliageActor"));
            var owner = data[simple.Owners[ownerEntry].At..simple.Owners[ownerEntry].End];
            int hostSimple = BitConverter.ToInt32(data, simpleStart);
            ms.Write(BitConverter.GetBytes(hostSimple + objs.Kept.Count(s => s.MeshPhysics is null)));
            ms.Write(data, simpleStart + 4, mapStart - simpleStart - 4);
            foreach (var s in objs.Kept.Where(s => s.MeshPhysics is null))
            {
                ms.Write(ObjectColliders.Entry(s, pmIndex[ObjPrefix + s.Material], owner));
                simpleAdded++;
            }
            var simpleMapData = data.AsSpan(simpleMapStart).ToArray();
            int sp = 0;
            var simpleMap = BakedEdit.ReadMap(simpleMapData, ref sp);
            NoneKey(simpleMap, "simple").Strides.Add(new BakedEdit.Stride { Index = (uint)hostSimple, Num = (uint)simpleAdded });
            ms.Write(BakedEdit.WriteMap(meshMap));
            ms.Write(BakedEdit.WriteMap(simpleMap));
            ms.Write(simpleMapData, sp, simpleMapData.Length - sp);
            r["simple_added"] = simpleAdded;
            r["simple_stride"] = $"{{{hostSimple}, {simpleAdded}}} in the None key";
            r["object_owner"] = System.Text.Encoding.ASCII.GetString(owner[20..^1]);
        }
        else
        {
            ms.Write(data, simpleStart, mapStart - simpleStart);
            ms.Write(BakedEdit.WriteMap(meshMap));
            ms.Write(data, simpleMapStart, data.Length - simpleMapStart);
        }
        r["object_mesh_colliders"] = all.Count - groundChunks;
        var outData = ms.ToArray();
        pkg.SetExportData(0, outData[..^4]);
        pkg.SaveTo(Path.Combine(outRoot, HostPaths.Baked + Path.GetExtension(path)));

        r["dr2_triangles"] = kept; r["dr2_cropped"] = cropped; r["dr2_colliders"] = all.Count;
        r["dr2_triangles_per_material"] = new JsonObject(perMaterial.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        r["uexp_bytes"] = $"{data.Length} -> {outData.Length}";
        Console.WriteLine($"BakedData: {colliders.Count} host + {all.Count} DR2 colliders, uexp {data.Length} -> {outData.Length} bytes");
        objectShapes = objs;
        return colliders.Count;
    }

    static Vector3 Normal(float[] p, uint a, uint b, uint c)
    {
        Vector3 V(uint i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
        var va = V(a);
        return Vector3.Cross(V(b) - va, V(c) - va);
    }

    static int ResolveImport(LegacyPackage pkg, string assetPath, out bool isNew)
    {
        var name = assetPath[(assetPath.LastIndexOf('/') + 1)..];
        int template = -1;
        for (int i = 0; i < pkg.ImportCount; i++)
        {
            if (pkg.ImportClassName(i) == "Package") continue;
            int outer = pkg.ImportOuter(i);
            if (outer >= 0) continue;
            var outerName = pkg.ImportName(-outer - 1);
            if (!outerName.StartsWith("/Game/Data/Gameplay/PhysicalMaterials/")) continue;
            if (pkg.ImportName(i) == name)
            {
                if (outerName != assetPath) throw new InvalidDataException($"import {name} lives in {outerName}, expected {assetPath}");
                isNew = false;
                return -(i + 1);
            }
            template = i;
        }
        if (template < 0) throw new InvalidDataException("no physical material import to copy");
        int tOuter = -pkg.ImportOuter(template) - 1;
        int pkgImport = pkg.AddImport(pkg.ImportClassPackage(tOuter), pkg.ImportClassName(tOuter), 0, assetPath);
        isNew = true;
        return pkg.AddImport(pkg.ImportClassPackage(template), pkg.ImportClassName(template), pkgImport, name);
    }

    // ---------------- gameplay cell ----------------

    sealed class Polyline
    {
        public readonly List<V3> P;
        public readonly double[] S;
        public Polyline(List<V3> p)
        {
            P = p; S = new double[p.Count];
            for (int i = 1; i < p.Count; i++) S[i] = S[i - 1] + (p[i] - p[i - 1]).Length;
        }
        public double Length => S[^1];
        static double Heading(V3 a, V3 b) => Math.Atan2(b.Y - a.Y, b.X - a.X);

        /// (distance along, signed lateral offset to the right, height above, heading rad), horizontal projection.
        public (double s, double lateral, double up, double heading) Project(V3 q)
        {
            (double d2, double s, double lat, double up, double h) best = (double.MaxValue, 0, 0, 0, 0);
            for (int i = 0; i + 1 < P.Count; i++)
            {
                var a = P[i]; var b = P[i + 1];
                double ex = b.X - a.X, ey = b.Y - a.Y, len2 = ex * ex + ey * ey;
                if (len2 < 1e-9) continue;
                double u = Math.Clamp(((q.X - a.X) * ex + (q.Y - a.Y) * ey) / len2, 0, 1);
                double px = a.X + u * ex, py = a.Y + u * ey, dx = q.X - px, dy = q.Y - py, d2 = dx * dx + dy * dy;
                if (d2 >= best.d2) continue;
                double len = Math.Sqrt(len2), rx = -ey / len, ry = ex / len;
                best = (d2, S[i] + u * (S[i + 1] - S[i]), dx * rx + dy * ry, q.Z - (a.Z + u * (b.Z - a.Z)), Heading(a, b));
            }
            return (best.s, best.lat, best.up, best.h);
        }

        public V3 At(double s, double lateral, double up, out double heading)
        {
            s = Math.Clamp(s, 0, Length);
            int i = Array.BinarySearch(S, s);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, P.Count - 2);
            var a = P[i]; var b = P[i + 1];
            double u = (S[i + 1] - S[i]) > 0 ? (s - S[i]) / (S[i + 1] - S[i]) : 0;
            heading = Heading(a, b);
            double rx = -Math.Sin(heading), ry = Math.Cos(heading);
            var p = a + (b - a) * u;
            return new V3(p.X + rx * lateral, p.Y + ry * lateral, p.Z + up);
        }
    }

    static CellInfo BuildCell(Options o, Ir ir, Placement pl, string legacy, string outRoot, JsonObject report, string respawnMode, RoadCorridor road,
                              ObjectColliders.Result? objectShapes)
    {
        var path = HostPaths.Resolve(legacy, HostPaths.Cell);
        var cell = new LegacyPackage(path);
        var r = new JsonObject();
        report["cell"] = r;

        // host component transforms via CUE4Parse (floats), located in the legacy bytes as doubles
        using var provider = Program.Open(o);
        var gamePath = "acr/Content/Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP.umap";
        var objs = provider.LoadPackage(gamePath).GetExports().ToList();
        if (objs.Count != cell.ExportCount || Enumerable.Range(0, objs.Count).Any(i => objs[i].Name != cell.ExportName(i)))
            throw new InvalidDataException("CUE4Parse export order differs from the legacy package");

        // splines
        var centre = ir.Centre.Select(pl.ToUe).ToList();
        var left = ir.Gates.Select(g => pl.ToUe(g.left)).ToList();
        var right = ir.Gates.Select(g => pl.ToUe(g.right)).ToList();
        var contour = new List<V3>();
        V3 Push(V3 edge, V3 c)
        {
            var d = new V3(edge.X - c.X, edge.Y - c.Y, 0);
            double len = d.Length;
            return len < 1 ? edge : edge + d * (ContourPush * 100 / len);
        }
        for (int i = 0; i < centre.Count; i++) contour.Add(Push(left[i], centre[i]));
        for (int i = centre.Count - 1; i >= 0; i--) contour.Add(Push(right[i], centre[i]));

        var hostCentre = Splines.SplitComponent(cell.GetExportData(cell.FindExport("CenterSpline", "DMSplineComponent"))).Curves;
        var hostLine = new Polyline(hostCentre.Position.Points.Select(p => V3.From(p.Out)).ToList());
        var newLine = new Polyline(centre);

        var splines = new JsonObject();
        double[] gateS = [];
        foreach (var (name, pts) in new[] { ("CenterSpline", centre), ("IdealSpline", centre), ("LeftBorderSpline", left), ("RightBorderSpline", right), ("ContourSpline", contour) })
        {
            int e = cell.FindExport(name, "DMSplineComponent");
            var split = Splines.SplitComponent(cell.GetExportData(e));
            var host = split.Curves;
            var sc = Splines.Build(pts, host.Position.Looped, host);
            foreach (var (nc, hc) in new[] { (sc.Position, host.Position), (sc.Rotation, host.Rotation), (sc.Scale, host.Scale) })
            {
                nc.Looped = hc.Looped; nc.LoopKeyOffset = hc.LoopKeyOffset;
            }
            if (host.Position.Looped) sc.Reparam = Splines.BuildReparam(sc.Position, true, 10);
            if (name == "CenterSpline")
                gateS = Enumerable.Range(0, pts.Count).Select(i => sc.Reparam.Points.First(p => Math.Abs(p.Out[0] - i) < 1e-6).InVal / 100.0).ToArray();
            split.Curves = sc;
            cell.SetExportData(e, Splines.JoinComponent(split));
            splines[name] = $"{host.Position.Points.Count} pts / {F(host.Reparam.Points[^1].InVal / 100, "0.#")} m -> {pts.Count} pts / {F(sc.Reparam.Points[^1].InVal / 100, "0.#")} m";
        }
        r["splines"] = splines;
        int me = cell.FindExport("Metadata", "DMSplineMetadata");
        var md = Splines.ReadMetadata(cell.GetExportData(me));
        for (int i = 0; i < md.Curves.Length; i++)
            if (md.Curves[i] is { } hc) md.Curves[i] = Splines.ConstantFloatCurve(centre.Count, hc);
        cell.SetExportData(me, Splines.WriteMetadata(md));

        // actors
        int Actor(string cls) => Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportClass(e) == cls);
        int Comp(int actor, string name) => Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportOuter(e) == actor + 1 && cell.ExportName(e) == name);
        var sectors = Enumerable.Range(0, cell.ExportCount).Where(e => cell.ExportClass(e) == "RaceSector").Select(a => Comp(a, "Trigger"))
            .OrderBy(c => hostLine.Project(Loc(objs[c])).s).ToList();
        if (sectors.Count != 4) throw new InvalidDataException($"expected 4 race sectors, found {sectors.Count}");
        int completStart = Comp(Actor("BC_CompleStartTargetPoint_C"), "SceneComp");
        int startTrigger = Comp(Actor("BC_StartSequenceTrigger_C"), "BoxComponent");
        int endTrigger = Comp(Actor("BC_EndSequenceTrigger_C"), "BoxComponent");
        int postCam = Comp(Actor("BC_PostRaceCamera_C"), "SceneComponent");
        int respawn = Comp(Actor("RespawnPlanes"), "Root Component");

        double SGate(int gate) => newLine.S[gate];
        var start = ir.Timing.Single(t => t.type == "start");
        var finish = ir.Timing.Single(t => t.type == "finish");
        var splits = ir.Timing.Where(t => t.type == "time").ToList();
        double compLen = SGate(finish.gate) - SGate(start.gate);
        int SplitNear(double frac) => splits.Count == 0 ? (int)Math.Round(start.gate + frac * (finish.gate - start.gate))
            : splits.OrderBy(t => Math.Abs(SGate(t.gate) - SGate(start.gate) - frac * compLen)).First().gate;
        int splitA = SplitNear(1 / 3.0), splitB = SplitNear(2 / 3.0);
        if (splitB == splitA) splitB = splits.Count > 1 ? splits[^1].gate : (int)Math.Round(start.gate + 2 / 3.0 * (finish.gate - start.gate));

        double hostStart = hostLine.Project(Loc(objs[sectors[0]])).s, hostFinish = hostLine.Project(Loc(objs[sectors[3]])).s;
        double hostEnd = hostLine.Project(Loc(objs[endTrigger])).s;
        double newEnd = SGate(finish.gate) + (hostEnd - hostFinish);
        var dr2Stop = ir.Triggers.FirstOrDefault(t => t.type == "VehicleBox" && t.name.StartsWith("EndSequenceTrigger", StringComparison.OrdinalIgnoreCase));
        if (dr2Stop.position is not null) newEnd = newLine.Project(pl.ToUe(dr2Stop.position)).s;
        double minS = 500, maxS = newLine.Length - 1500;

        var plan = new List<(string role, int comp, double s)>
        {
            ("CompleStart (car start point)", completStart, SGate(start.gate) + hostLine.Project(Loc(objs[completStart])).s - hostStart),
            ("StartSequenceTrigger", startTrigger, SGate(start.gate) + hostLine.Project(Loc(objs[startTrigger])).s - hostStart),
            ("RaceSector0 (start line)", sectors[0], SGate(start.gate)),
            ($"RaceSector1 (split, DR2 gate {splitA})", sectors[1], SGate(splitA)),
            ($"RaceSector2 (split, DR2 gate {splitB})", sectors[2], SGate(splitB)),
            ($"RaceSector3 (finish, DR2 gate {finish.gate})", sectors[3], SGate(finish.gate)),
            ("EndSequenceTrigger (stop zone)", endTrigger, newEnd),
            ("PostRaceCamera", postCam, newEnd + hostLine.Project(Loc(objs[postCam])).s - hostEnd),
        };
        var actors = new JsonArray();
        foreach (var (role, comp, s0) in plan)
        {
            double s = Math.Clamp(s0, minS, maxS);
            var obj = objs[comp];
            var hostLoc = Loc(obj);
            var (_, lat, upOff, hHead) = hostLine.Project(hostLoc);
            var data = cell.GetExportData(comp);
            var newLoc = newLine.At(s, lat, upOff, out double nHead);
            int locAt = FindDoubles(data, Floats(obj, "RelativeLocation"));
            WriteDoubles(data, locAt, newLoc.X, newLoc.Y, newLoc.Z);
            string rot = "none";
            if (obj.TryGetValue(out FRotator hr, "RelativeRotation"))
            {
                int rotAt = FindDoubles(data, [hr.Pitch, hr.Yaw, hr.Roll]);
                double yaw = NormDeg(hr.Yaw + (nHead - hHead) * 180 / Math.PI);
                WriteDoubles(data, rotAt + 8, yaw);
                rot = $"yaw {F(hr.Yaw, "0.#")} -> {F(yaw, "0.#")} (road heading {F(hHead * 180 / Math.PI, "0.#")} -> {F(nHead * 180 / Math.PI, "0.#")})";
            }
            cell.SetExportData(comp, data);
            actors.Add($"{role}: s {F(s / 100, "0.#")} m{(Math.Abs(s - s0) > 1 ? $" (clamped from {F(s0 / 100, "0.#")})" : "")}, lateral {F(lat / 100, "0.##")} m, up {F(upOff / 100, "0.##")} m, " +
                       $"UE ({F(newLoc.X, "0")}, {F(newLoc.Y, "0")}, {F(newLoc.Z, "0")}), {rot}");
        }
        // Respawn walls. Their spline params are world positions (the SplineMeshComponents are not attached to the
        // RespawnPlanes root), so moving the root, as v1 did, leaves them in place; see docs/decisions.md D7.
        r["respawn_walls_left_in_place_audit"] = Respawn.AuditInPlace(cell, objs, road);
        if (respawnMode == "root")
        {
            var obj = objs[respawn];
            var data = cell.GetExportData(respawn);
            var loc = Loc(obj);
            WriteDoubles(data, FindDoubles(data, Floats(obj, "RelativeLocation")) + 16, loc.Z - 100000);
            cell.SetExportData(respawn, data);
            actors.Add($"RespawnPlanes root: Z {F(loc.Z, "0")} -> {F(loc.Z - 100000, "0")} (v1 behaviour; the walls are not attached to it and stay in place)");
        }
        else
        {
            r["respawn"] = Respawn.Relay(cell, objs, contour, road, sinkAll: respawnMode == "sink");
            actors.Add($"respawn walls ({respawnMode}): {r["respawn"]!.ToJsonString()}");
        }
        if (o.Named.GetValueOrDefault("visuals") is { } visualsCfg) r["visuals"] = Visuals.Build(cell, ir, pl, Toml.Load(visualsCfg), Visuals.Options.From(o.Named), objectShapes, InPhysicsBounds);
        r["actors"] = actors;
        r["dr2_centre_length_m"] = newLine.Length / 100;
        foreach (var a in actors) Console.WriteLine($"  {a}");
        cell.SaveTo(Path.Combine(outRoot, HostPaths.Cell + Path.GetExtension(path)));

        double sm(double cm) => Math.Clamp(cm, minS, maxS) / 100;
        double PolyToSpline(double polyCm)
        {
            int i = Array.BinarySearch(newLine.S, polyCm);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, newLine.S.Length - 2);
            double u = (polyCm - newLine.S[i]) / Math.Max(1e-9, newLine.S[i + 1] - newLine.S[i]);
            return gateS[i] + u * (gateS[i + 1] - gateS[i]);
        }
        return new CellInfo(gateS, centre, PolyToSpline(SGate(start.gate)), PolyToSpline(SGate(finish.gate)), PolyToSpline(sm(newEnd) * 100));
    }

    static double NormDeg(double d) { d %= 360; return d > 180 ? d - 360 : d <= -180 ? d + 360 : d; }

    static V3 Loc(UObject o) => o.TryGetValue(out FVector v, "RelativeLocation") ? new V3(v.X, v.Y, v.Z)
        : throw new InvalidDataException($"{o.Name} has no RelativeLocation");
    static double[] Floats(UObject o, string prop) => o.TryGetValue(out FVector v, prop) ? [v.X, v.Y, v.Z] : throw new InvalidDataException($"{o.Name} has no {prop}");

    /// Offset of the unique run of doubles whose float casts equal the given (float) values.
    static int FindDoubles(byte[] d, double[] vals)
    {
        var hits = new List<int>();
        for (int i = 0; i + vals.Length * 8 <= d.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < vals.Length && ok; k++) ok = (float)BitConverter.ToDouble(d, i + k * 8) == (float)vals[k];
            if (ok) hits.Add(i);
        }
        if (hits.Count != 1) throw new InvalidDataException($"double pattern ({string.Join(", ", vals)}) found {hits.Count} times");
        return hits[0];
    }

    static void WriteDoubles(byte[] d, int at, params double[] v)
    {
        for (int k = 0; k < v.Length; k++) BitConverter.GetBytes(v[k]).CopyTo(d, at + k * 8);
    }

    // ---------------- DataTables ----------------

    static void BuildTables(Options o, Ir ir, string legacy, string outRoot, bool keepPacenotes, JsonObject report, Toml? pacenoteMap, CellInfo cell)
    {
        var r = new JsonObject();
        report["tables"] = r;

        var vpath = HostPaths.Resolve(legacy, HostPaths.Variants);
        var variants = new LegacyPackage(vpath);
        var vd = variants.GetExportData(0);
        var pattern = new List<byte>(BitConverter.GetBytes(4.8f));
        pattern.AddRange(BitConverter.GetBytes(345.0)); pattern.AddRange(BitConverter.GetBytes(410.0));
        pattern.AddRange(Convert.FromHexString("01000000B724EF43"));
        int at = UniqueIndexOf(vd, pattern.ToArray());
        float km = (float)Math.Round(ir.LengthM / 1000, 3);
        BitConverter.GetBytes(km).CopyTo(vd, at);
        variants.SetExportData(0, vd);
        variants.SaveTo(Path.Combine(outRoot, HostPaths.Variants + Path.GetExtension(vpath)));
        r["tracks_variants"] = $"Hafren South FullForward Length 4.8 -> {F(km)} km";

        var ppath = HostPaths.Resolve(legacy, HostPaths.Pacenote);
        var pace = new LegacyPackage(ppath);
        if (keepPacenotes) { r["pacenotes"] = "kept host (Hafren South) table"; return; }
        if (pacenoteMap is not null)
        {
            var table = Pacenotes.Read(pace);
            int hostRows = table.Rows.Count;
            var gd = ir.Gates.Select(g => g.distance).ToArray();
            double ToSpline(double gateDistance)
            {
                int i = Array.BinarySearch(gd, gateDistance);
                if (i < 0) i = ~i - 1;
                i = Math.Clamp(i, 0, gd.Length - 2);
                double u = Math.Clamp((gateDistance - gd[i]) / Math.Max(1e-9, gd[i + 1] - gd[i]), 0, 1);
                return cell.GateS[i] + u * (cell.GateS[i + 1] - cell.GateS[i]);
            }
            var curve = Curvature.FromPoints(cell.CentreUe.Select(p => (p.X / 100, p.Y / 100)).ToList(), cell.GateS);
            var pr = new JsonObject();
            var tokens = Pacenotes.LoadTokens(Path.Combine(ir.Dir, "stage.json"));
            var placements = new List<Pacenotes.Placement>();
            table.Rows = Pacenotes.Generate(tokens, ToSpline, curve, cell.StartS, cell.FinishS, cell.StopS, pacenoteMap, pr, placements,
                                            o.Named.GetValueOrDefault("pacenote-assignment"));
            pace.SetExportData(0, Pacenotes.Write(table, pace));
            pace.SaveTo(Path.Combine(outRoot, HostPaths.Pacenote + Path.GetExtension(ppath)));
            pr["host_rows_replaced"] = hostRows;
            if (o.Named.GetValueOrDefault("pacenote-report") is { } reportPath)
            {
                var greedy = new List<Pacenotes.Placement>();
                var greedyRows = Pacenotes.Generate(tokens, ToSpline, curve, cell.StartS, cell.FinishS, cell.StopS, pacenoteMap, new JsonObject(), greedy, "greedy");
                var (mis, hints, corners) = Pacenotes.Report(table.Rows, curve, cell.StartS, pacenoteMap, ir.Name, reportPath, placements, greedyRows, greedy);
                pr["report"] = $"{reportPath}: {corners} corner calls, {mis} mismatches, {hints} severity hints";
                Console.WriteLine($"pacenote report: {pr["report"]}");
            }
            pr["spline_m"] = new JsonObject { ["start_line"] = Math.Round(cell.StartS, 1), ["finish_line"] = Math.Round(cell.FinishS, 1), ["stop"] = Math.Round(cell.StopS, 1) };
            r["pacenotes"] = pr;
            Console.WriteLine($"pacenotes: {hostRows} host rows -> {pr["rows"]} rows from {pr["dr2_tokens"]} DR2 tokens ({pr["dr2_tokens_mapped"]} mapped; " +
                              $"{pr["corners_localised_on_geometry"]}/{pr["corner_tokens"]} corners on geometry); unmapped {pr["unmapped"]!.ToJsonString()}");
            return;
        }
        var pd = pace.GetExportData(0);
        using var provider = Program.Open(o);
        var dt = provider.LoadPackage("acr/Content/Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward.uasset").GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Engine.UDataTable>().Single();
        int rows = dt.RowMap.Count;
        // rows are the tail of the export: ..., int32 NumRows, rows (FName key + struct)
        int countAt = -1;
        for (int i = 2; i + 12 <= pd.Length; i++)
        {
            if (BitConverter.ToInt32(pd, i) != rows) continue;
            int nameIdx = BitConverter.ToInt32(pd, i + 4), nameNum = BitConverter.ToInt32(pd, i + 8);
            if (nameIdx < 0 || nameIdx >= pace.Names.Count || nameNum is < 0 or > 10000) continue;
            if (pace.NameAt(nameIdx, nameNum) != dt.RowMap.Keys.First().Text) continue;
            countAt = i; break;
        }
        if (countAt < 0) throw new InvalidDataException("could not locate the pacenote row count");
        var trimmed = new byte[countAt + 4];
        Array.Copy(pd, trimmed, countAt);
        pace.SetExportData(0, trimmed);
        pace.SaveTo(Path.Combine(outRoot, HostPaths.Pacenote + Path.GetExtension(ppath)));
        r["pacenotes"] = $"DT_PacenoteHafrenSouthForward: {rows} rows -> 0";
    }

    static int UniqueIndexOf(byte[] d, byte[] pattern)
    {
        int first = d.AsSpan().IndexOf(pattern);
        if (first < 0 || d.AsSpan(first + 1).IndexOf(pattern) >= 0) throw new InvalidDataException("pattern not unique");
        return first;
    }

    // ---------------- verification ----------------

    public static int RunCheck(Options o)
    {
        var ir = Ir.Load(Need(o, "ir"));
        var report = JsonNode.Parse(File.ReadAllText(Need(o, "report")))!;
        var p = report["placement"]!;
        var pl = new Placement(p["theta_deg"]!.GetValue<double>(), p["cx"]!.GetValue<double>(), p["cz"]!.GetValue<double>(),
            p["tx"]!.GetValue<double>(), p["tz"]!.GetValue<double>(), p["dy"]!.GetValue<double>(), p["slack_m"]!.GetValue<double>());
        using var w = o.Out is null ? Console.Out : new StreamWriter(o.Out);
        int failures = Check(Need(o, "legacy"), ir, pl, report["host_collider_count"]!.GetValue<int>(), w, report);
        return failures == 0 ? 0 : 5;
    }

    static int Check(string root, Ir ir, Placement pl, int hostCount, TextWriter w, JsonNode report)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }

        var vpath = HostPaths.Resolve(root, HostPaths.Variants);
        var vd = new LegacyPackage(vpath).GetExportData(0);
        float km = (float)Math.Round(ir.LengthM / 1000, 3);
        var lenPattern = BitConverter.GetBytes(km).Concat(BitConverter.GetBytes(345.0)).Concat(BitConverter.GetBytes(410.0))
            .Concat(Convert.FromHexString("01000000B724EF43")).ToArray();
        int lenHits = 0;
        for (int i = vd.AsSpan().IndexOf(lenPattern); i >= 0; i = vd.AsSpan(i + 1).IndexOf(lenPattern) is var j and >= 0 ? i + 1 + j : -1) lenHits++;
        C(lenHits == 1, $"DT_TracksVariants: Hafren South forward row Length = {F(km)} km (DR2 length_m {F(ir.LengthM, "0.#")})");

        if (report["options"]?["visuals"]?.GetValue<string>() is { } visualsCfg)
        {
            var vo = report["options"]!;
            var opt = new Visuals.Options(vo["visuals_host"]?.GetValue<string>() ?? "start",
                vo["visuals_retarget"]?.GetValue<bool>() ?? false, vo["visuals_owner"]?.GetValue<string>(),
                vo["visuals_vegetation"]?.GetValue<string>() ?? "none", vo["visuals_canopies"]?.GetValue<string>());
            RoadProbe? probe = vo["objects"]?.GetValue<string>() is { } objCfg
                ? new RoadProbe(Road(ir, pl), new SurfaceGrid(Path.Combine(ir.Dir, "collision.glb"), pl), Toml.Load(objCfg)) : null;
            failures += Visuals.Check(new LegacyPackage(HostPaths.Resolve(root, HostPaths.Cell)), ir, pl, Toml.Load(visualsCfg), opt, w,
                                      report["cell"]?["visuals"], probe);
        }

        var bpath = HostPaths.Resolve(root, HostPaths.Baked);
        _ = new LegacyPackage(bpath);
        var data = File.ReadAllBytes(Path.ChangeExtension(bpath, ".uexp"));
        var (cols, _, simpleStart) = BakedEdit.ParseColliders(data);
        var (mapStart, meshMap, _) = BakedEdit.LocateMaps(data, simpleStart);
        var simple = SimpleColliders.ParseExact(data, simpleStart, mapStart);
        int objMesh = report["baked"]?["object_mesh_colliders"]?.GetValue<int>() ?? 0;
        var dr2 = cols.Skip(hostCount).ToList();
        var groundCols = dr2.Take(dr2.Count - objMesh).ToList();
        long tris = groundCols.Sum(c => (long)c.Indices.Length / 3);
        C(dr2.Count > 0 && cols.Count == hostCount + dr2.Count, $"BakedData re-read: {cols.Count} mesh colliders = {hostCount} host + {groundCols.Count} DR2 ground + {objMesh} DR2 object, {simple.Entries} simple entries");
        C(tris >= ir.CollisionTriangles * 0.99, $"DR2 triangles {tris} of {ir.CollisionTriangles} in the IR ({100.0 * tris / ir.CollisionTriangles:0.##}%)");
        var road = Road(ir, pl);
        if (report["baked"]?["simple_added"] is { } addedNode)
        {
            var cfg = Toml.Load(report["options"]!["objects"]!.GetValue<string>());
            var surf = new SurfaceGrid(Path.Combine(ir.Dir, "collision.glb"), pl);
            var roadSurf = cfg.Arr("road", "surfaces").Cast<string>().ToHashSet();
            failures += CheckObjects(data, simpleStart, mapStart, simple, addedNode.GetValue<int>(), road,
                                     (x, y) => surf.At(x / 100, y / 100) is { } s && roadSurf.Contains(s), w);
        }
        C(meshMap.Any(e => e.Strides.Any(s => s.Index == hostCount && s.Num == dr2.Count)), $"always-active stride {{{hostCount}, {dr2.Count}}} present");
        float hostTop = cols.Take(hostCount).Max(c => c.Max.Y);
        float dr2Min = dr2.Min(c => c.Min.Y);
        C(hostTop < dr2Min, $"host colliders flattened below DR2 ground: host top y {hostTop:0.##} m < DR2 min y {dr2Min:0.##} m");

        long up = 0, down = 0, hostUp = 0, hostDown = 0;
        foreach (var (c, i) in cols.Select((c, i) => (c, i)))
            for (int t = 0; t + 2 < c.Indices.Length; t += 3)
            {
                var n = Normal(c.Positions, c.Indices[t], c.Indices[t + 1], c.Indices[t + 2]);
                if (Math.Abs(n.Y) < 0.5f * n.Length()) continue;
                if (i >= hostCount) { if (n.Y > 0) up++; else down++; }
                else { if (n.Y > 0) hostUp++; else hostDown++; }
            }
        bool hostIsUp = hostUp >= hostDown;
        double agree = (hostIsUp ? up : down) / (double)Math.Max(1, up + down);
        C(agree > 0.9, $"DR2 winding matches the host convention ({(hostIsUp ? "+Y" : "-Y")} normals): {agree:P1} of flat-ish DR2 triangles");

        var ground = groundCols;
        double maxGate = 0, sumGate = 0; int missGate = 0;
        foreach (var g in ir.Gates)
        {
            var q = pl.ToPhysics(g.crossing);
            var h = BakedEdit.GroundHeight(ground, new Vector2((float)q.X, (float)q.Z));
            if (h is null) { missGate++; continue; }
            double dy = Math.Abs(h.Value - q.Y);
            maxGate = Math.Max(maxGate, dy); sumGate += dy;
        }
        C(missGate == 0 && maxGate <= 0.10, $"ground raycast at {ir.Gates.Count} gate crossings: {missGate} missing, max |dy| {maxGate * 100:0.#} cm, mean {sumGate / Math.Max(1, ir.Gates.Count - missGate) * 100:0.##} cm");

        int samples = 0, missLine = 0; double maxLine = 0;
        for (int i = 0; i + 1 < ir.Centre.Count; i++)
        {
            var a = pl.ToPhysics(ir.Centre[i]); var b = pl.ToPhysics(ir.Centre[i + 1]);
            int n = Math.Max(1, (int)Math.Ceiling((b - a).Length));
            for (int k = 0; k < n; k++)
            {
                var q = a + (b - a) * ((double)k / n);
                samples++;
                var h = BakedEdit.GroundHeight(ground, new Vector2((float)q.X, (float)q.Z));
                if (h is null) { missLine++; continue; }
                maxLine = Math.Max(maxLine, Math.Abs(h.Value - q.Y));
            }
        }
        C(missLine == 0, $"ground raycast every <=1 m along the centreline: {samples} samples, {missLine} without ground (max |dy| to the chord {maxLine * 100:0.#} cm)");

        var cell = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Cell));
        var cs = Splines.SplitComponent(cell.GetExportData(cell.FindExport("CenterSpline", "DMSplineComponent"))).Curves;
        double len = cs.Reparam.Points[^1].InVal / 100;
        double polyLen = 0;
        for (int i = 1; i < ir.Centre.Count; i++) polyLen += (pl.ToPhysics(ir.Centre[i]) - pl.ToPhysics(ir.Centre[i - 1])).Length;
        C(cs.Position.Points.Count == ir.Centre.Count && Math.Abs(len - polyLen) < polyLen * 0.01,
            $"CenterSpline: {cs.Position.Points.Count} points, length {len:0.#} m (DR2 centreline polyline {polyLen:0.#} m)");
        var first = pl.ToUe(ir.Centre[0]);
        C((V3.From(cs.Position.Points[0].Out) - first).Length < 1, "CenterSpline starts at the DR2 run-up (first gate) and runs in driving order");

        // object mesh colliders (cones) must not sit on the road at car height: raycast them along the centreline
        if (objMesh > 0)
        {
            var objCols = dr2.Skip(dr2.Count - objMesh).ToList();
            int hits = 0, samplesO = 0;
            foreach (var g in ir.Gates)
            {
                var q = pl.ToPhysics(g.crossing);
                samplesO++;
                var h = BakedEdit.GroundHeight(objCols, new Vector2((float)q.X, (float)q.Z));
                if (h is { } hv && hv > q.Y - 1 && hv < q.Y + 2.5) hits++;
            }
            C(hits == 0, $"object mesh colliders: {objMesh} colliders, {objCols.Sum(c => c.Indices.Length / 3)} triangles; {hits} of {samplesO} gate crossings have one at car height");
        }

        var respawnRep = report["cell"]?["respawn"];
        if (respawnRep is not null)
        {
            var (placed, sunk, minClear, bad) = Respawn.Check(cell, road);
            C(bad == 0 && placed == respawnRep["placed_along_dr2_contour"]!.GetValue<int>() && placed + sunk == respawnRep["walls"]!.GetValue<int>(),
              $"respawn walls re-read: {placed} placed along the DR2 contour, {sunk} sunk 1 km down, {bad} with unexpected layout");
            if (placed > 0) C(minClear >= 3, $"respawn walls: min clearance to the DR2 road edges {minClear:0.##} m (>= 3 m)");
        }

        var prep = report["tables"]?["pacenotes"];
        if (prep is JsonObject)
        {
            var pace = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Pacenote));
            var t = Pacenotes.Read(pace);
            bool sorted = true, tiled = true;
            for (int i = 0; i + 1 < t.Rows.Count; i++)
            {
                if (t.Rows[i + 1].SplineDistanceM < t.Rows[i].SplineDistanceM) sorted = false;
                if (t.Rows[i].SplineDistanceM + t.Rows[i].LengthM > t.Rows[i + 1].SplineDistanceM + 0.01f) tiled = false;
            }
            bool keys = t.Rows.Select((r, i) => r.Key == $"R{i:0000}").All(x => x);
            C(t.Rows.Count == prep["rows"]!.GetValue<int>() && sorted && tiled && keys,
              $"pacenote table re-read: {t.Rows.Count} rows, sorted {sorted}, non-overlapping {tiled}, keys R0000.. {keys}");
            C(t.Rows.All(r => r.Tokens.Count > 0) && t.Rows[^1].SplineDistanceM + t.Rows[^1].LengthM <= len + 0.5,
              $"pacenote rows: all have tokens, last ends at {t.Rows[^1].SplineDistanceM + t.Rows[^1].LengthM:0.#} m of the {len:0.#} m CenterSpline; finish call at {t.Rows.First(r => r.Tokens.Contains("Finish")).SplineDistanceM:0.#} m");
        }
        return failures;
    }

    /// Re-parses the appended simple entries and checks them against what the build reported.
    static int CheckObjects(byte[] d, int simpleStart, int mapStart, SimpleColliders.Info simple, int added, RoadCorridor road,
                            Func<double, double, bool> onRoadSurface, TextWriter w)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }
        int total = BitConverter.ToInt32(d, simpleStart);
        int host = total - added;
        C(simple.Entries == total, $"simple entries: {total} = {host} host + {added} DR2 objects");
        int p = simple.Owners[host - 1].End;
        int boxes = 0, capsules = 0, outOfWorld = 0, onRoad = 0, badRot = 0;
        double minR = double.MaxValue, maxR = 0;
        for (int i = 0; i < added; i++)
        {
            foreach (var s in Unv.ReadHeader(d, ref p))
            {
                if (s.Zero) continue;
                switch (s.Index)
                {
                    case 0 or 2:
                    {
                        int n = BitConverter.ToInt32(d, p); p += 4;
                        for (int k = 0; k < n; k++)
                        {
                            var vals = new Dictionary<int, double[]>();
                            foreach (var e in Unv.ReadHeader(d, ref p))
                            {
                                if (e.Zero) { vals[e.Index] = [0]; continue; }
                                int size = e.Index switch { 0 or 1 => 24, 6 when s.Index == 0 => 8, 5 when s.Index == 2 => 8, 7 when s.Index == 0 => 1, 8 when s.Index == 0 => 1, 6 when s.Index == 2 => 1, 7 when s.Index == 2 => 1, _ => 4 };
                                vals[e.Index] = size == 24 ? [BitConverter.ToDouble(d, p), BitConverter.ToDouble(d, p + 8), BitConverter.ToDouble(d, p + 16)]
                                              : size == 4 ? [BitConverter.ToSingle(d, p)] : [d[p]];
                                p += size;
                            }
                            var c = vals[0]; var rot = vals[1];
                            var (ax, ay, az) = ObjectColliders.RotationAxes(rot[0], rot[1], rot[2]);
                            if (Math.Abs(ax.X * (ay.Y * az.Z - ay.Z * az.Y) - ax.Y * (ay.X * az.Z - ay.Z * az.X) + ax.Z * (ay.X * az.Y - ay.Y * az.X) - 1) > 1e-6) badRot++;
                            var centre = new V3(c[0], c[1], c[2]);
                            if (!InPhysicsBounds(new V3(c[0] / 100, c[2] / 100, c[1] / 100))) outOfWorld++;
                            double reach;
                            if (s.Index == 0) { boxes++; reach = 0; }
                            else
                            {
                                capsules++;
                                double r = vals[2][0];
                                minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                                reach = r;
                            }
                            var (cl, z, _) = road.Query(centre.X, centre.Y);
                            if (cl < 0 && centre.Z - reach < z + 250 && centre.Z + reach > z - 100 && onRoadSurface(centre.X, centre.Y)) onRoad++;
                        }
                        break;
                    }
                    case 3: p += 4; break;
                    case 4:
                        p += 16;
                        int len = BitConverter.ToInt32(d, p); p += 4;
                        p += len >= 0 ? len : -len * 2;
                        break;
                    default: throw new InvalidDataException($"simple collider property {s.Index}");
                }
            }
        }
        C(p == mapStart, $"added simple entries parse exactly to the stride maps ({added} entries: {boxes} boxes, {capsules} capsules, radius {minR:0.#}..{maxR:0.#} cm)");
        C(badRot == 0 && outOfWorld == 0, $"added simple entries: {badRot} with a non-rotation, {outOfWorld} outside the physics bounds");
        C(onRoad == 0, $"added simple entries: {onRoad} with their centre over the road surface at car height");
        var mapsAt = mapStart;
        var meshMap = BakedEdit.ReadMap(d, ref mapsAt);
        var simpleMap = BakedEdit.ReadMap(d, ref mapsAt);
        C(simpleMap.Any(e => e.Strides.Any(s => s.Index == host && s.Num == added)), $"simple stride {{{host}, {added}}} present");
        return failures;
    }
}
