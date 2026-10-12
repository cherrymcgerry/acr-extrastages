using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace AcrRef;

/// Interim visuals without the editor: the forward gameplay cell's start-trigger ProceduralMeshComponent (loaded with
/// variant 1/2, see ProcMesh) is rebuilt in world space with the DR2 ground (terrain + road surface), edge posts along
/// both road edges and start/finish gates. Render only: sections have no collision (the car uses BakedData).
/// Sections: 0 hidden dummy (keeps slot 0 for any runtime SetMaterial(0)), 1 terrain, 2 road, 3 edge posts, 4 gates.
internal static class Visuals
{
    const uint RfDefaultSubObject = 0x40000;

    /// Where the geometry goes. v1.1 used the start trigger's own mesh, which renders during loading but is gone in
    /// gameplay: the native StartEndSequenceTrigger drives its MeshComponent (Default/TriggeredMaterial) at runtime.
    ///   host:     trigger whose ProceduralMeshComponent carries the geometry (start | end)
    ///   retarget: that trigger's MeshComponent property is pointed at the other trigger's ring, so the native code no
    ///             longer touches the visuals component
    ///   owner:    re-parent the visuals component to this actor (export name prefix, e.g. SplinesActor) and attach it
    ///             to its root, so actor-level hiding of the trigger does not apply either
    ///   vegetation: none (no non-solid geometry) | crowns (small tree crowns, see VisualObjects)
    ///   canopies: overrides [objects] canopies (clip | apex | full | none)
    ///   vista: adds DR2's distant vista ring (render/vista.glb, see [vista]) as a last section
    ///   textured: terrain, road and vista get the [textured] materials; the road gets road-following UVs (RoadUv)
    ///   dr2: (v1.8, on top of textured) terrain, road, vista and the render-mesh objects get DR2's own textures
    ///        ([dr2_textures], Dr2Materials); dr2Revert: comma-separated sections (terrain, road, vista, objects) that
    ///        keep their textured (v1.7) material and UVs instead
    ///   terrainUv: planar (top-down, v1.7/v1.8) | box (v1.9, DR2 terrain only: each triangle projected on the axis plane
    ///        its normal points along most, at terrain_tile_m; no stretching on steep faces)
    ///   ground: collision (v1.1-v1.9: the terrain section is DR2's collision ground, a ribbon along the road with ragged
    ///        edges) | render (v1.10: DR2's render terrain, render/terrain.glb, minus its triangles under the collision
    ///        road; collision ground only where the render terrain has none; the road section raised by [ground]
    ///        road_lift_cm against z-fighting with the coplanar render terrain at its edges)
    ///   groundLod: (v1.11, render ground) error-bounded simplification, tolerance by distance from the route
    ///        ([ground] simplify_*; GroundLod.Simplify); also keeps DR2's own triangle winding (RenderGround keepWinding)
    ///   groundBake: (v1.11, render ground + dr2) DR2 ground-bake manifest (render/ground_bake/manifest.json): one terrain
    ///        section, texture and MIC per tile, tile UVs (GroundLod.Split); null = one terrain section, box UVs
    internal sealed record Options(string Host, bool Retarget, string? Owner, string Vegetation = "none", string? Canopies = null,
                                   bool Vista = false, bool Textured = false, bool Dr2 = false, string Dr2Revert = "",
                                   string TerrainUv = "planar", string Ground = "collision", bool GroundLod = false, string? GroundBake = null)
    {
        public static Options From(IReadOnlyDictionary<string, string> named) => new(
            named.GetValueOrDefault("visuals-host", "start"),
            named.GetValueOrDefault("visuals-retarget", "1") != "0",
            named.GetValueOrDefault("visuals-owner") is { Length: > 0 } ow ? ow : null,
            named.GetValueOrDefault("visuals-vegetation", "none"),
            named.GetValueOrDefault("visuals-canopies") is { Length: > 0 } cn ? cn : null,
            named.GetValueOrDefault("visuals-vista", "0") != "0",
            named.GetValueOrDefault("visuals-textured", "0") != "0",
            named.GetValueOrDefault("visuals-dr2", "0") != "0",
            named.GetValueOrDefault("visuals-dr2-revert", ""),
            named.GetValueOrDefault("visuals-terrain-uv", "planar"),
            named.GetValueOrDefault("visuals-ground", "collision"),
            named.GetValueOrDefault("visuals-ground-lod", "0") != "0",
            named.GetValueOrDefault("visuals-ground-bake") is { Length: > 0 } gb ? gb : null);

        public static readonly string[] Dr2Sections = ["terrain", "road", "vista", "objects"];

        /// Whether a section gets DR2 textures (dr2 on and the section not reverted).
        public bool Dr2On(string section)
        {
            var revert = Dr2Revert.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (revert.FirstOrDefault(r => !Dr2Sections.Contains(r)) is { } bad)
                throw new ArgumentException($"--visuals-dr2-revert: unknown section {bad} (use {string.Join(", ", Dr2Sections)})");
            return Dr2 && !revert.Contains(section);
        }
    }

    // by name: a round trip without the Blueprint containers cannot resolve the class imports
    static int TriggerActor(LegacyPackage cell, string host) => Enumerable.Range(0, cell.ExportCount).Single(i =>
        cell.ExportName(i).StartsWith(host == "end" ? "BC_EndSequenceTrigger_C_" : "BC_StartSequenceTrigger_C_") && cell.ExportOuter(i) > 0
        && cell.ExportName(cell.ExportOuter(i) - 1) == "PersistentLevel");

    /// The trigger's own MeshComponent export (by name, since the visuals one may have been re-parented).
    static int TriggerMesh(LegacyPackage cell, int actor) => Enumerable.Range(0, cell.ExportCount).Single(i =>
        cell.ExportClass(i) == "ProceduralMeshComponent" && cell.ExportName(i) == "MeshComponent" &&
        cell.ExportDeps(i).Any(d => d.kind == "CBC" && d.index == actor + 1));

    static int Unique(byte[] d, int value)
    {
        var hits = new List<int>();
        for (int p = 0; p + 4 <= d.Length; p++) if (BitConverter.ToInt32(d, p) == value) hits.Add(p);
        if (hits.Count != 1) throw new InvalidDataException($"package index {value} found {hits.Count} times in the actor data");
        return hits[0];
    }

    public static JsonObject Build(LegacyPackage cell, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, Options opt,
                                   ObjectColliders.Result? objects, Func<Splines.V3, bool> inPhysicsBounds, Dr2Materials? dr2 = null)
    {
        var r = new JsonObject();
        if (opt.Dr2 && (dr2 is null || !opt.Textured)) throw new ArgumentException("--visuals-dr2 needs --visuals-textured and the DR2 material writer");
        int hostActor = TriggerActor(cell, opt.Host), otherActor = TriggerActor(cell, opt.Host == "end" ? "start" : "end");
        int e = TriggerMesh(cell, hostActor), other = TriggerMesh(cell, otherActor);
        var comp = ProcMesh.Parse(cell.GetExportData(e), e);
        if (!ProcMesh.RoundTrips(comp, out var why)) throw new InvalidDataException($"trigger mesh sections do not round trip: {why}");

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
        if (opt.Ground == "render") (terrain, r["ground"]) = RenderGround(ir, pl, cfg, terrain, road, keepWinding: opt.GroundLod);
        if (opt.GroundLod)
        {
            if (opt.Ground != "render") throw new ArgumentException("--visuals-ground-lod needs --visuals-ground render");
            var route = new GroundLod.RouteDistance(ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)));
            var (sp, si, srep) = GroundLod.Simplify(terrain, route, GroundLod.Tolerance(cfg));
            terrain = new ProcMesh.Section();
            foreach (var p in sp) { terrain.Pos.Add(p); terrain.Nrm.Add(Vector3.Zero); terrain.Uv.Add(new Vector2(p.X / 400, p.Y / 400)); }
            for (int t = 0; t + 2 < si.Count; t += 3) AddTriKeep(terrain, (int)si[t], (int)si[t + 1], (int)si[t + 2]);
            r["ground"]!["simplified"] = srep;
        }
        else if (opt.Ground != "collision") throw new ArgumentException($"--visuals-ground must be collision or render, not {opt.Ground}");
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

        var imported = new Dictionary<string, int>();
        int MatPath(string path)
        {
            if (path.Length == 0) return 0;
            if (imported.TryGetValue(path, out var known)) return known;
            var name = path[(path.LastIndexOf('/') + 1)..];
            int pkgImp = cell.AddImport("/Script/CoreUObject", "Package", 0, path);
            return imported[path] = cell.AddImport("/Script/Engine", "MaterialInstanceConstant", pkgImp, name);
        }
        int Mat(string section, string key) => MatPath(cfg.Str(section, key));
        string ground = "materials";
        var dr2Rep = new JsonObject();
        if (opt.Textured)
        {
            ground = "textured";
            r["textured"] = RoadUv.Apply(ir, pl, cfg, road);
            float k = (float)(4 / cfg.Num("textured", "terrain_uv_m"));
            for (int i = 0; i < terrain.Uv.Count; i++) terrain.Uv[i] *= k;
        }
        int terrainMat = Mat(ground, "terrain"), roadMat = Mat(ground, "road");
        var bakeSections = new List<(string name, ProcMesh.Section s, int mat)>();
        if (opt.GroundBake is { } manifest)
        {
            // section 1 = the first tile, further tiles after the gates; triangles over no tile keep the grass slice below
            if (!opt.Dr2On("terrain") || opt.Ground != "render") throw new ArgumentException("--visuals-ground-bake needs --visuals-dr2 (terrain not reverted) and --visuals-ground render");
            var tiles = GroundLod.LoadTiles(manifest);
            var (used, rest, brep) = GroundLod.Split(terrain, pl, tiles);
            var dir = Path.GetDirectoryName(manifest)!;
            foreach (var (tile, s) in used)
                bakeSections.Add(($"terrain:{tile.Name}", s, MatPath(dr2!.Mic("bake_" + tile.Name, dr2.TextureFromFile(Path.Combine(dir, tile.Dds), "ground_bake_" + tile.Name)))));
            terrain = bakeSections[0].s; terrainMat = bakeSections[0].mat;
            bakeSections.RemoveAt(0);
            if (rest.Idx.Count > 0)
            {
                dr2Rep["terrain_uv"] = BoxUv(rest, (float)(cfg.Num("dr2_textures", "terrain_tile_m") * 100));
                bakeSections.Add(("terrain:rest", rest, MatPath(dr2!.Mic("terrain", dr2.Texture(cfg.Str("dr2_textures", "terrain"))))));
            }
            dr2Rep["terrain"] = $"ground bake {manifest}: {used.Count} tile sections" + (rest.Idx.Count > 0 ? $", {rest.Idx.Count / 3} triangles over no tile on {cfg.Str("dr2_textures", "terrain")}" : "");
            dr2Rep["ground_bake"] = brep;
        }
        else if (opt.Dr2On("terrain"))
        {
            // DR2 detail slice tiled every terrain_tile_m (the v1.7 UVs tile every terrain_uv_m)
            float k = (float)(cfg.Num("textured", "terrain_uv_m") / cfg.Num("dr2_textures", "terrain_tile_m"));
            if (opt.TerrainUv == "box") dr2Rep["terrain_uv"] = BoxUv(terrain, (float)(cfg.Num("dr2_textures", "terrain_tile_m") * 100));
            else if (opt.TerrainUv == "planar") for (int i = 0; i < terrain.Uv.Count; i++) terrain.Uv[i] *= k;
            else throw new ArgumentException($"--visuals-terrain-uv must be planar or box, not {opt.TerrainUv}");
            terrainMat = MatPath(dr2!.Mic("terrain", dr2.Texture(cfg.Str("dr2_textures", "terrain"))));
            dr2Rep["terrain"] = $"{cfg.Str("dr2_textures", "terrain")}, tiled every {cfg.Num("dr2_textures", "terrain_tile_m")} m";
        }
        if (opt.Dr2On("road"))
        {
            // DR2's road strip runs edge to edge across U: RoadUv's UV1 layout (0..1 across, road_uv1_along_m along)
            road.Uv = road.Uv1.ToList();
            road.Color = 0xFFFFFFFF;
            roadMat = MatPath(dr2!.Mic("road", dr2.Texture(cfg.Str("dr2_textures", "road"))));
            dr2Rep["road"] = $"{cfg.Str("dr2_textures", "road")} on UV0 = UV1 (0..1 edge to edge, {cfg.Num("textured", "road_uv1_along_m")} m along)";
        }
        var mats = new List<int> { 0, terrainMat, roadMat, Mat("materials", "posts"), Mat("materials", "gates") };
        var sections = new List<ProcMesh.Section> { dummy, terrain, road, posts, gates };
        var sectionNames = new JsonArray("dummy", "terrain", "road", "posts", "gates");
        foreach (var (name, s, mat) in bakeSections) { sections.Add(s); mats.Add(mat); sectionNames.Add(name); }
        if (objects is not null && cfg.Num("objects", "enabled") != 0)
        {
            var objJson = opt.Dr2On("objects")
                ? JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "render", "objects.materials.json")))!.AsArray()
                    .GroupBy(m => m!["name"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.First()!)
                : null;
            var objDiffuse = new Dictionary<string, string?>();
            var noTex = new Dictionary<string, string>();
            string? Diffuse(string mat)
            {
                if (objDiffuse.TryGetValue(mat, out var known)) return known;
                string? w = "not in objects.materials.json";
                var d = objJson!.TryGetValue(mat, out var j) ? dr2!.ObjectDiffuse(j, out w) : null;
                if (d is null) noTex[mat] = w!;
                return objDiffuse[mat] = d;
            }
            var (objSecs, objRep) = VisualObjects.Build(ir, pl, cfg, objects, inPhysicsBounds, opt.Vegetation, opt.Canopies,
                                                        objJson is null ? null : m => Diffuse(m) is not null);
            foreach (var cls in VisualObjects.Classes.Where(c => objSecs[c].Idx.Count > 0))
            {
                sections.Add(objSecs[cls]); mats.Add(Mat("object_materials", cls)); sectionNames.Add($"objects:{cls}");
            }
            var drawn = new HashSet<string>();
            foreach (var (key, s) in objSecs.Where(kv => kv.Key.StartsWith(VisualObjects.Dr2Prefix) && kv.Value.Idx.Count > 0).OrderBy(kv => kv.Key))
            {
                var mat = key[VisualObjects.Dr2Prefix.Length..];
                drawn.Add(mat);
                sections.Add(s); mats.Add(MatPath(dr2!.ObjectMic(Diffuse(mat)!))); sectionNames.Add($"objects:{key}");
            }
            if (objJson is not null)
                dr2Rep["objects"] = new JsonObject
                {
                    ["dr2_material_sections"] = drawn.Count,
                    ["drawn"] = objRep["dr2_material_sections"]?.DeepClone(),
                    ["materials_without_dr2_texture"] = new JsonObject(noTex.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                        .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)$"{g.Count()} ({string.Join(", ", g.Select(kv => kv.Key).Take(6))}{(g.Count() > 6 ? ", ..." : "")})"))),
                    ["note"] = "materials without a DR2 texture keep their class colour (as do trunks and collider-shape proxies)",
                };
            r["objects"] = objRep;
        }
        if (opt.Vista)
        {
            var (vista, vr) = BuildVista(ir, pl, cfg, [terrain, road, .. bakeSections.Select(b => b.s)], colourMapUv: opt.Dr2On("vista"));
            int vistaMat;
            if (opt.Dr2On("vista"))
            {
                vista.Uv = vista.Uv1; vista.Uv1 = new();
                vistaMat = MatPath(dr2!.VistaMic());
                dr2Rep["vista"] = $"baked {cfg.Str("dr2_textures", "vista_colour_map")} x layers, plan-projected over the vista bounds";
            }
            else
            {
                if (opt.Textured)
                {
                    float k = (float)(4 / cfg.Num("textured", "vista_uv_m"));
                    for (int i = 0; i < vista.Uv.Count; i++) vista.Uv[i] *= k;
                }
                vistaMat = opt.Textured ? Mat("textured", "vista") : Mat("vista", "material");
            }
            sections.Add(vista); mats.Add(vistaMat); sectionNames.Add("vista");
            r["vista"] = vr;
        }
        if (opt.Dr2)
        {
            dr2Rep["reverted_to_v1_7"] = opt.Dr2Revert;
            dr2Rep["textures"] = dr2!.TextureCount; dr2Rep["mics"] = dr2.MicCount; dr2Rep["texture_bytes"] = dr2.TextureBytes;
            dr2Rep["packages"] = dr2.Log.DeepClone();
            r["dr2"] = dr2Rep;
        }
        r["sections"] = sectionNames;
        r["triangles_total"] = sections.Sum(s => s.Idx.Count / 3);
        var data = ProcMesh.BuildComponent(comp, sections, mats);
        r["component"] = $"{cell.ExportName(e)} of {cell.ExportName(cell.ExportOuter(e) - 1)} (export {e})";

        if (opt.Retarget)
        {
            // host trigger: MeshComponent -> the other trigger's ring (data and its CreateBeforeSerialization dependency)
            var ad = (byte[])cell.GetExportData(hostActor).Clone();
            BitConverter.GetBytes(other + 1).CopyTo(ad, Unique(ad, e + 1));
            cell.SetExportData(hostActor, ad);
            var dep = cell.ExportDeps(hostActor).Single(x => x.kind == "CBS" && x.index == e + 1);
            cell.SetDep(dep.pos, other + 1);
            r["retarget"] = $"{cell.ExportName(hostActor)}.MeshComponent -> export {other} ({cell.ExportName(otherActor)}'s ring)";
        }
        if (opt.Owner is { } ownerPrefix)
        {
            if (!opt.Retarget) throw new ArgumentException("--visuals-owner needs --visuals-retarget (the trigger would still reference the component)");
            int owner = Enumerable.Range(0, cell.ExportCount).Single(i => cell.ExportName(i).StartsWith(ownerPrefix) && cell.ExportOuter(i) > 0
                && cell.ExportName(cell.ExportOuter(i) - 1) == "PersistentLevel");
            var root = cell.ExportDeps(owner).Where(x => x.kind == "CBS" && x.index > 0 && cell.ExportName(x.index - 1) == "Root").ToList();
            if (root.Count != 1) throw new InvalidDataException($"{cell.ExportName(owner)}: no unique Root component");
            int oldParent = BitConverter.ToInt32(data, ProcMesh.Parse(data, e).Slots[124].at);
            BitConverter.GetBytes(root[0].index).CopyTo(data, ProcMesh.Parse(data, e).Slots[124].at);
            foreach (var d in cell.ExportDeps(e))
            {
                if (d.kind == "CBC" && d.index == hostActor + 1) cell.SetDep(d.pos, owner + 1);
                else if (d.kind == "CBS" && d.index == oldParent) cell.SetDep(d.pos, root[0].index);
            }
            cell.SetExportOuter(e, owner + 1);
            cell.SetExportFlags(e, cell.ExportFlags(e) & ~RfDefaultSubObject);
            r["owner"] = $"re-parented to {cell.ExportName(owner)}, attached to its Root (export {root[0].index - 1})";
        }
        cell.SetExportData(e, data);

        var allTerrain = bakeSections.Select(b => b.s).Prepend(terrain).ToList();
        r["terrain"] = $"{allTerrain.Sum(s => s.Pos.Count)} vertices, {allTerrain.Sum(s => s.Idx.Count / 3)} triangles" +
                       (bakeSections.Count > 0 ? $" in {allTerrain.Count} sections" : "");
        r["road"] = $"{road.Pos.Count} vertices, {road.Idx.Count / 3} triangles (codes {string.Join(", ", roadCodes)})";
        r["edge_posts"] = postCount;
        r["edge_posts_first"] = postCentres;
        r["gates"] = "start + finish: 2 posts + banner each";
        r["materials"] = new JsonArray(mats.Select(m => (JsonNode)(m == 0 ? "default" : cell.ImportName(-m - 1))).ToArray());
        r["export_bytes"] = $"{comp.Data.Length} -> {data.Length}";
        var (mn, mx) = ProcMesh.Bounds(sections.SelectMany(s => s.Pos));
        r["bounds_m"] = $"({mn.X / 100:0},{mn.Y / 100:0},{mn.Z / 100:0})..({mx.X / 100:0},{mx.Y / 100:0},{mx.Z / 100:0})";
        Console.WriteLine($"visuals: terrain {r["terrain"]}, road {r["road"]}, {postCount} edge posts, {sections.Count} sections, " +
                          $"{r["triangles_total"]} triangles total{(r["objects"] is { } ob ? $" (objects {ob["triangles"]})" : "")}, export {r["export_bytes"]} bytes");
        return r;
    }

    /// Offline check of the rebuilt component in a built (or round-tripped) cell.
    public static int Check(LegacyPackage cell, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, Options opt, TextWriter w,
                            JsonNode? visualsReport = null, RoadProbe? probe = null, string? legacyRoot = null)
    {
        int f0 = CheckBody(cell, ir, pl, cfg, opt, w, visualsReport, probe);
        if (opt.Dr2 && legacyRoot is not null) f0 += CheckDr2(cell, cfg, opt, w, legacyRoot, visualsReport);
        return f0;
    }

    /// v1.8: every section's DR2 MaterialInstanceConstant and its textures are in the package root (built container,
    /// round-tripped), parse, and are what the sections expect; reverted sections use their v1.7 material.
    static int CheckDr2(LegacyPackage cell, Toml cfg, Options opt, TextWriter w, string legacyRoot, JsonNode? visualsReport)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }
        var pmcs = Enumerable.Range(0, cell.ExportCount).Where(i => cell.ExportClass(i) == "ProceduralMeshComponent").ToList();
        int e = pmcs.MaxBy(i => cell.GetExportData(i).Length);
        var c = ProcMesh.Parse(cell.GetExportData(e), e);
        int at = c.Slots[7].at, n = BitConverter.ToInt32(c.Data, at);
        var names = (visualsReport?["sections"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
        string root = cfg.Str("dr2_textures", "package_root").TrimEnd('/');
        string File(string pkg) => Path.Combine(legacyRoot, "acr", "Content", pkg["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        var micPaths = new List<(string section, string path)>();
        for (int i = 0; i < n; i++)
        {
            int m = BitConverter.ToInt32(c.Data, at + 4 + 4 * i);
            string path = m < 0 ? cell.ImportName(-cell.ImportOuter(-m - 1) - 1) : "";
            micPaths.Add((i < names.Count ? names[i] : $"#{i}", path));
        }
        var expectDr2 = new Dictionary<string, bool>
        {
            ["terrain"] = opt.Dr2On("terrain"), ["road"] = opt.Dr2On("road"), ["vista"] = opt.Dr2On("vista"),
        };
        foreach (var (sec, want) in expectDr2)
        {
            var hit = micPaths.FirstOrDefault(x => x.section == sec);
            if (hit.section is null) continue;
            bool isDr2 = hit.path.StartsWith(root + "/");
            // reverted: the v1.7 material lives in another container, so a round trip of ours alone cannot name it
            C(isDr2 == want, $"dr2: section {sec} uses {(isDr2 ? $"its DR2 material {hit.path}" : "a material outside the DR2 package root (reverted to v1.7)")}");
        }
        var objDr2 = micPaths.Where(x => x.section.StartsWith("objects:" + VisualObjects.Dr2Prefix)).ToList();
        C(opt.Dr2On("objects") ? objDr2.Count > 0 && objDr2.All(x => x.path.StartsWith(root + "/")) : objDr2.Count == 0,
          $"dr2: {objDr2.Count} object sections with DR2 materials{(opt.Dr2On("objects") ? "" : " (objects reverted)")}");

        var mics = micPaths.Select(x => x.path).Where(p => p.StartsWith(root + "/")).Distinct().ToList();
        int texOk = 0, micOk = 0; var problems = new List<string>(); var textures = new HashSet<string>();
        foreach (var mic in mics)
        {
            if (!System.IO.File.Exists(File(mic))) { problems.Add($"{mic}: not in the container"); continue; }
            var mp = new LegacyPackage(File(mic));
            var texImports = Enumerable.Range(0, mp.ImportCount).Where(i => mp.ImportClassName(i) == "Texture2D").ToList();
            // a round trip of our container alone cannot name the parent (another container): retoc writes it as
            // /Engine/UnknownPackage.UnknownExport; stage-verify resolves it to M_Simple through the game's containers
            var parent = Enumerable.Range(0, mp.ImportCount).FirstOrDefault(i => mp.ImportClassName(i) == "Material" && mp.ImportName(i) == "M_Simple"
                                                                                 || mp.ImportName(i) == "UnknownExport", -1);
            bool ok = mp.ExportCount == 1 && mp.ExportClass(0) == "MaterialInstanceConstant" && mp.PackageName == mic && mp.ExportName(0) == mic[(mic.LastIndexOf('/') + 1)..]
                      && texImports.Count == 3 && parent >= 0;
            foreach (var t in texImports)
            {
                string tp = mp.ImportName(-mp.ImportOuter(t) - 1);
                if (!tp.StartsWith(root + "/") || !System.IO.File.Exists(File(tp))) { ok = false; problems.Add($"{mic}: texture {tp} missing"); continue; }
                if (!textures.Add(tp)) continue;
                var tpkg = new LegacyPackage(File(tp));
                var dr = tpkg.DataResources();
                int count = BinaryPrimitives.ReadInt32LittleEndian(dr.AsSpan(4));
                var data = tpkg.GetExportData(0);
                bool inline = Enumerable.Range(0, count).All(k =>
                {
                    var en = dr.AsSpan(8 + k * LegacyPackage.DataResourceSize);
                    long off = BinaryPrimitives.ReadInt64LittleEndian(en[4..]), size = BinaryPrimitives.ReadInt64LittleEndian(en[20..]);
                    return BinaryPrimitives.ReadUInt32LittleEndian(en[40..]) == 0x48 && off > 0 && off + size <= data.Length
                           && BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)off - 4)) == k;
                });
                if (tpkg.ExportClass(0) == "Texture2D" && tpkg.PackageName == tp && tpkg.Names[1].StartsWith("PF_") && count > 0 && inline) texOk++;
                else problems.Add($"{tp}: texture package does not parse as expected");
            }
            if (ok) micOk++; else problems.Add($"{mic}: MIC layout");
        }
        C(problems.Count == 0 && micOk == mics.Count && texOk == textures.Count && (mics.Count > 0) == Options.Dr2Sections.Any(opt.Dr2On),
          $"dr2: {mics.Count} MICs (parent M_Simple, 3 texture imports each) and {textures.Count} textures resolve in the container; every mip inline at its data resource" +
          (problems.Count > 0 ? $"; {string.Join("; ", problems.Take(5))}" : ""));
        return failures;
    }

    static int CheckBody(LegacyPackage cell, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, Options opt, TextWriter w,
                         JsonNode? visualsReport, RoadProbe? probe)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }
        var pmcs = Enumerable.Range(0, cell.ExportCount).Where(i => cell.ExportClass(i) == "ProceduralMeshComponent").ToList();
        int e = pmcs.MaxBy(i => cell.GetExportData(i).Length);
        var c = ProcMesh.Parse(cell.GetExportData(e), e);
        string outerName = cell.ExportName(cell.ExportOuter(e) - 1);
        C(c.PropsEnd + 8 == c.Data.Length, $"visuals: mesh export {e} of {outerName} parses to its end ({c.Data.Length} bytes)");

        // nothing that the native trigger code drives may reference the visuals component
        int start = TriggerActor(cell, "start"), end = TriggerActor(cell, "end");
        var refs = new[] { start, end }.Where(a =>
            Enumerable.Range(0, cell.GetExportData(a).Length - 3).Any(p => BitConverter.ToInt32(cell.GetExportData(a), p) == e + 1)).ToList();
        if (opt.Retarget)
        {
            var ring = pmcs.Single(i => i != e);
            bool both = new[] { start, end }.All(a => cell.ExportDeps(a).Any(d => d.kind == "CBS" && d.index == ring + 1));
            C(refs.Count == 0 && both, $"visuals: neither trigger's MeshComponent references the visuals component (both use export {ring})");
        }
        int ownerIdx = cell.ExportOuter(e) - 1;
        if (opt.Owner is { } ow)
            C(cell.ExportName(ownerIdx).StartsWith(ow) && (cell.ExportFlags(e) & RfDefaultSubObject) == 0
              && cell.ExportDeps(e).Any(d => d.kind == "CBC" && d.index == ownerIdx + 1)
              && cell.ExportName(BitConverter.ToInt32(c.Data, c.Slots[124].at) - 1) == "Root",
              $"visuals: component owned by {cell.ExportName(ownerIdx)} (not a default subobject; created after it; attached to its Root)");
        else
            C(ownerIdx == TriggerActor(cell, opt.Host),
              $"visuals: component owned by the {opt.Host} trigger ({outerName})");
        C(c.Slots.TryGetValue(142, out var hid) && hid.zero && c.Slots.ContainsKey(136) && c.Slots.ContainsKey(133),
          "visuals: bHiddenInGame = false, bVisible = true, absolute transform (world-space vertices)");
        int p = c.Slots[3].at; int n = BitConverter.ToInt32(c.Data, p); p += 4;
        var secs = new List<ProcMesh.Section>();
        for (int i = 0; i < n; i++) secs.Add(ProcMesh.ReadSection(c.Data, ref p, out _));
        var secNames = (visualsReport?["sections"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList();
        // section 1 and, with the ground bake, every further tile section (terrain:<tile>, terrain:rest)
        var terrainIdx = Enumerable.Range(0, n).Where(i => i == 1 || secNames is not null && i < secNames.Count && secNames[i].StartsWith("terrain:")).ToList();
        var terrainSecs = terrainIdx.Select(i => secs[i]).ToList();
        C(n >= 5 && secs.All(s => !s.Collision), $"visuals: {n} sections, none with collision ({string.Join(", ", secs.Select(s => $"{s.Pos.Count} v / {s.Idx.Count / 3} t"))})");
        C(secs.All(s => s.Idx.All(i => i < s.Pos.Count)) && secs.All(s => s.Idx.Count % 3 == 0 && s.Idx.Count > 0), "visuals: all indices in range, no empty section");
        var mats = c.Slots.TryGetValue(7, out var m) && !m.zero ? BitConverter.ToInt32(c.Data, m.at) : 0;
        C(mats == n, $"visuals: {mats} material slots, one per section (dummy, terrain = default, road, posts, gates{(n > 5 ? ", object classes" : "")})");
        long tris = secs.Sum(s => (long)s.Idx.Count / 3);
        double budget = cfg.Num("objects", "max_total_triangles");
        C(tris <= budget, $"visuals: {tris} triangles in total (budget {budget:0})");

        // road corridor: nothing drawn may stand in the road unless a collider is there too (over road surface).
        //   car height (car_height_min_m .. car_height_max_m): solid object sections at most to the collider limit
        //     (max_penetration_m + road_overlap_tolerance_m); the render-only edge posts, centred on the gate edges, at
        //     most 0.75 x their width
        //   above (.. overhang_height_m): only inside a kept collider that the build reported there (trunks leaning over
        //     the road, the start gantry)
        //   crowns: not in the corridor in plan at any height
        // The gates' banner spans the road at gate_height_m by design and is not checked.
        if (probe is not null && visualsReport?["sections"] is JsonArray names && names.Count == n)
        {
            double tol = cfg.Num("objects", "road_overlap_tolerance_m") * 100, overhang = cfg.Num("objects", "overhang_height_m") * 100;
            var colliders = (visualsReport["objects"]?["colliders_overhead_in_corridor"] as JsonArray ?? [])
                .Select(x => (at: new Splines.V3(x!["at_cm"]![0]!.GetValue<double>(), x["at_cm"]![1]!.GetValue<double>(), x["at_cm"]![2]!.GetValue<double>()),
                              reach: x["reach_cm"]!.GetValue<double>())).ToList();
            for (int i = 0; i < n; i++)
            {
                var name = names[i]!.GetValue<string>();
                if (name is "dummy" or "terrain" or "road" or "gates" || name.StartsWith("terrain:")) continue;
                bool soft = name == "objects:soft", isPosts = name == "posts";
                double limit = isPosts ? cfg.Num("markers", "post_width_m") * 75 : soft ? 0 : probe.MaxPen + tol;
                var s = secs[i];
                double deepest = 0, deepestHigh = 0; int over = 0, high = 0, unexplained = 0;
                for (int t = 0; t + 2 < s.Idx.Count; t += 3)
                {
                    Splines.V3 P(int k) { var v = s.Pos[(int)s.Idx[t + k]]; return new(v.X, v.Y, v.Z); }
                    if (soft)
                    {
                        double d = probe.TriangleDepth(P(0), P(1), P(2), 50, -1e9, 1e9, surfaceOnly: false);
                        deepest = Math.Max(deepest, d);
                        if (d > 0.5) over++;
                        continue;
                    }
                    double car = probe.TriangleDepth(P(0), P(1), P(2), 50, probe.HMin, probe.HMax);
                    deepest = Math.Max(deepest, car);
                    if (car > limit + 0.5) over++;
                    double up = probe.TriangleDepth(P(0), P(1), P(2), 50, probe.HMax, overhang);
                    if (up <= (isPosts ? limit : probe.MaxPen + tol) + 0.5) continue;
                    high++; deepestHigh = Math.Max(deepestHigh, up);
                    var g = (P(0) + P(1) + P(2)) * (1.0 / 3);
                    if (!colliders.Any(cl => (g - cl.at).Length <= cl.reach + 1)) unexplained++;
                }
                if (soft)
                    C(over == 0, $"visuals road corridor: crowns {s.Idx.Count / 3} triangles, deepest {deepest / 100:0.00} m inside the corridor in plan, {over} inside");
                else
                    C(over == 0 && unexplained == 0,
                      $"visuals road corridor: {name} {s.Idx.Count / 3} triangles; at car height {probe.HMin / 100:0.#}..{probe.HMax / 100:0.#} m deepest {deepest / 100:0.00} m " +
                      $"(limit {limit / 100:0.00} m), {over} over; above it to {overhang / 100:0.#} m {high} triangles in the corridor (deepest {deepestHigh / 100:0.0} m), " +
                      $"{unexplained} not inside a reported overhead collider");
            }
            C(true, $"visuals road corridor: {colliders.Count} kept colliders reach over the road above car height (listed in the report)");
            C(opt.Vegetation == "crowns" || !names.Any(x => x!.GetValue<string>() == "objects:soft"),
              $"visuals: vegetation {opt.Vegetation}: {(names.Any(x => x!.GetValue<string>() == "objects:soft") ? "crown section present" : "no non-solid section")}");
            if (visualsReport["objects"] is { } or)
            {
                double maxExt = cfg.Num("objects", "max_object_extent_m"), largest = or["largest_extent_m"]!.GetValue<double>();
                C(largest <= maxExt, $"visuals: largest drawn object {largest:0.0} m in plan (limit {maxExt:0} m): {or["largest"]![0]}");
                int ob = or["over_bounds"]!.GetValue<int>();
                C(ob == 0, $"visuals: {ob} objects drawn wider than {cfg.Num("objects", "max_extent_over_bounds")} x their DR2 render bounds + 1 m");
                if (or["shapes_vs_colliders"] is { } sv)
                {
                    int compared = sv["compared"]!.GetValue<int>(), bad = sv["mismatches"]!.GetValue<int>();
                    C(compared > 0 && bad == 0, $"visuals: {compared} drawn collider shapes vs their colliders: {bad} outside by > 2 cm or under {cfg.Num("objects", "shape_min_fill")} fill"
                                         + $" (max outside {sv["max_outside_collider_cm"]} cm, min fill {sv["min_fill"]})" + (bad > 0 ? $": {sv["first"]![0]}" : ""));
                }
                if (or["canopies"] is { } cn) C(true, $"visuals: canopies {cn}");
                if (or["canopy_drawn_span"] is JsonObject cs && or["canopies"]?.GetValue<string>().StartsWith("apex") == true)
                {
                    double lim = cfg.Num("objects", "canopy_max_span_m");
                    C(cs["max_m"]!.GetValue<double>() <= lim,
                      $"visuals: drawn canopy cones at most {cs["max_m"]} m across (limit {lim} m; widest {cs["widest"]![0]})");
                }
            }
        }
        else if (probe is not null) C(false, "visuals road corridor: section names missing from the report");
        var boxSecs = new List<ProcMesh.Section> { secs[1] };
        if (opt.GroundBake is not null)
        {
            var tileIdx = terrainIdx.Where(i => secNames?[i] != "terrain:rest").ToList();
            int uvOut = tileIdx.Sum(i => secs[i].Uv.Count(u => u.X < 0 || u.X > 1 || u.Y < 0 || u.Y > 1));
            int verts = tileIdx.Sum(i => secs[i].Pos.Count);
            int rest = terrainIdx.Where(i => secNames?[i] == "terrain:rest").Sum(i => secs[i].Idx.Count / 3);
            C(secNames is not null && tileIdx.Count > 0 && uvOut == 0,
              $"visuals: ground bake: {tileIdx.Count} tile sections, UV0 inside the tile texture (0..1) on all {verts} vertices ({uvOut} outside); {rest} triangles over no tile");
            boxSecs = terrainIdx.Where(i => secNames?[i] == "terrain:rest").Select(i => secs[i]).ToList();
        }
        if (opt.Dr2On("terrain"))
        foreach (var tr in boxSecs)
        {
            // UV area (in texture repeats x repeat size) over true area per triangle: 1 = no stretch; top-down planar UVs
            // tend to 0 on steep faces (long streaks), box projection keeps at least 1/sqrt(3)
            double rep = cfg.Num("dr2_textures", "terrain_tile_m") * 100, minRatio = double.MaxValue;
            int steep = 0, nTri = 0;
            for (int t = 0; t + 2 < tr.Idx.Count; t += 3)
            {
                var (a, b, c3) = ((int)tr.Idx[t], (int)tr.Idx[t + 1], (int)tr.Idx[t + 2]);
                double area = Vector3.Cross(tr.Pos[b] - tr.Pos[a], tr.Pos[c3] - tr.Pos[a]).Length();
                if (area < 1) continue;
                var du = tr.Uv[b] - tr.Uv[a]; var dv = tr.Uv[c3] - tr.Uv[a];
                double ratio = Math.Abs(du.X * dv.Y - du.Y * dv.X) * rep * rep / area;
                nTri++; minRatio = Math.Min(minRatio, ratio); if (ratio < 0.5) steep++;
            }
            if (opt.TerrainUv == "box")
                C(steep == 0 && minRatio >= 0.57, $"visuals: terrain box UVs: UV / true area at least {minRatio:0.000} on all {nTri} triangles (1/sqrt 3 = 0.577; none stretched)");
            else w.WriteLine($"     visuals: terrain planar UVs: {steep} of {nTri} triangles stretched more than 2x (UV / true area < 0.5), worst {minRatio:0.000}");
        }
        if (opt.Dr2On("road"))
        {
            var road = secs[2];
            bool same = road.Uv1.Count == road.Pos.Count && road.Uv.Count == road.Pos.Count && road.Uv.Zip(road.Uv1).All(x => x.First == x.Second);
            C(same && road.Uv.All(u => u.X >= 0 && u.X <= 1) && road.Color == 0xFFFFFFFF,
              $"visuals: DR2 road strip on UV0 = UV1 (0..1 edge to edge) on all {road.Pos.Count} road vertices, colour white");
        }
        else if (opt.Textured)
        {
            var road = secs[2];
            double nomU = cfg.Num("textured", "road_uv0_across_m"), nomV = cfg.Num("textured", "road_uv0_along_m");
            var (inside, mu, mv) = RoadUv.Stretch(road, nomU, nomV);
            double min = cfg.Num("textured", "road_stretch_min_fraction");
            bool layout = new[] { road.Uv1, road.Uv2, road.Uv3 }.All(l => l.Count == road.Pos.Count)
                          && road.Uv1.All(u => u.X >= 0 && u.X <= 1) && road.Color == RoadUv.Bgra(cfg, "road_color");
            C(layout && inside >= min && Math.Abs(mu / nomU - 1) < 0.15 && Math.Abs(mv / nomV - 1) < 0.15,
              $"visuals: road UVs follow the road: UV0 median {mu:0.##} m across / {mv:0.##} m along per unit (nominal {nomU} / {nomV}), " +
              $"{inside:P1} of {road.Idx.Count / 3} triangles within 1.5x (min {min:P0}); UV1..UV3 on every vertex, UV1.U in 0..1, colour {road.Color:X8}");
        }
        if (opt.Vista)
        {
            var vr = visualsReport?["vista"];
            double near = vr?["nearest_to_centreline_m"]?.GetValue<double>() ?? 0, min = cfg.Num("vista", "min_distance_m");
            int vt = vr?["triangles"]?.GetValue<int>() ?? 0;
            C(vr is not null && secs[^1].Idx.Count / 3 == vt && near >= min,
              $"visuals: vista section last, {vt} triangles, nearest vertex {near} m from the centreline (min {min} m)");
        }

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
        var ground = terrainSecs.SelectMany(s => s.Pos).Concat(secs[2].Pos).ToList();
        var (mn, mx) = ProcMesh.Bounds(ground);
        var cl = ir.Centre.Select(pl.ToUe).ToList();
        C(cl.All(q => q.X >= mn.X && q.X <= mx.X && q.Y >= mn.Y && q.Y <= mx.Y) && secs[2].Idx.Count > 0,
          $"visuals: ground mesh {ground.Count} vertices covers all {cl.Count} centreline points; road section {secs[2].Idx.Count / 3} triangles");
        if (opt.Ground == "render") failures += CheckRenderGround(ir, pl, cfg, terrainSecs, secs[2], C);
        return failures;
    }

    /// DR2's distant vista ring in world space. Triangles over a [vista] exclude_cell_m grid cell that the drawn ground
    /// touches are dropped: the ring also runs under the playable area, where it would show through the ground.
    /// Plan grid over the host physics bounds (UE cm): marks the cells whose centre lies inside a triangle's plan projection.
    sealed class PlanMask(double cellCm)
    {
        readonly double _x0 = StageBuild.PhysMinX * 100, _y0 = StageBuild.PhysMinZ * 100;
        readonly int _w = (int)Math.Ceiling((StageBuild.PhysMaxX - StageBuild.PhysMinX) * 100 / cellCm),
                     _h = (int)Math.Ceiling((StageBuild.PhysMaxZ - StageBuild.PhysMinZ) * 100 / cellCm);
        bool[]? _on;
        bool[] On => _on ??= new bool[_w * _h];

        public void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            double d = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (Math.Abs(d) < 1e-6) return;
            int i0 = Math.Max(0, (int)Math.Ceiling((Math.Min(a.X, Math.Min(b.X, c.X)) - _x0) / cellCm - 0.5));
            int i1 = Math.Min(_w - 1, (int)Math.Floor((Math.Max(a.X, Math.Max(b.X, c.X)) - _x0) / cellCm - 0.5));
            int j0 = Math.Max(0, (int)Math.Ceiling((Math.Min(a.Y, Math.Min(b.Y, c.Y)) - _y0) / cellCm - 0.5));
            int j1 = Math.Min(_h - 1, (int)Math.Floor((Math.Max(a.Y, Math.Max(b.Y, c.Y)) - _y0) / cellCm - 0.5));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    double px = _x0 + (i + 0.5) * cellCm, py = _y0 + (j + 0.5) * cellCm;
                    double l1 = ((px - a.X) * (c.Y - a.Y) - (c.X - a.X) * (py - a.Y)) / d, l2 = ((b.X - a.X) * (py - a.Y) - (px - a.X) * (b.Y - a.Y)) / d;
                    if (l1 >= -1e-9 && l2 >= -1e-9 && l1 + l2 <= 1 + 1e-9) On[j * _w + i] = true;
                }
        }

        public bool this[Vector3 p]
        {
            get
            {
                int i = (int)Math.Floor((p.X - _x0) / cellCm), j = (int)Math.Floor((p.Y - _y0) / cellCm);
                return i >= 0 && j >= 0 && i < _w && j < _h && On[j * _w + i];
            }
        }
    }

    /// Highest up-facing (|n_z| >= 0.3) triangle under a plan point, UE cm, bucketed on a 4 m grid.
    sealed class TopSurface
    {
        const float Cell = 400;
        readonly Dictionary<(int, int), List<(Vector3 a, Vector3 b, Vector3 c)>> _cells = new();

        public void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.Length() == 0 || Math.Abs(n.Z) / n.Length() < 0.3) return;
            for (int x = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell); x <= (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell); x++)
                for (int y = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / Cell); y <= (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / Cell); y++)
                {
                    if (!_cells.TryGetValue((x, y), out var l)) _cells[(x, y)] = l = new();
                    l.Add((a, b, c));
                }
        }

        public double? At(Vector2 p)
        {
            if (!_cells.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var l)) return null;
            double? best = null;
            foreach (var (a, b, c) in l)
            {
                double d = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
                if (Math.Abs(d) < 1e-6) continue;
                double l1 = ((p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y)) / d, l2 = ((b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y)) / d;
                if (l1 < -1e-9 || l2 < -1e-9 || l1 + l2 > 1 + 1e-9) continue;
                double z = a.Z + l1 * (b.Z - a.Z) + l2 * (c.Z - a.Z);
                if (best is null || z > best) best = z;
            }
            return best;
        }
    }

    /// v1.10: the drawn ground against the collision ground the car drives on. Road vertices sit road_lift_cm above it;
    /// drawn terrain vertices near the centreline match its height (the two DR2 meshes are triangulated differently, so
    /// small differences between their vertices are expected); the drawn ground has no holes near the route.
    /// Returns 0: failures are counted by C.
    static int CheckRenderGround(StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, List<ProcMesh.Section> terrain, ProcMesh.Section road, Action<bool, string> C)
    {
        var sg = new SurfaceGrid(Path.Combine(ir.Dir, "collision.glb"), pl);
        double lift = cfg.Num("ground", "road_lift_cm");
        var rd = road.Pos.Select(v => sg.HeightAt(v.X / 100, v.Y / 100) is { } h ? v.Z - h * 100 : double.NaN).Where(d => !double.IsNaN(d)).ToList();
        int off = rd.Count(d => Math.Abs(d - lift) > 0.5);
        C(rd.Count > road.Pos.Count * 0.99 && off <= rd.Count / 1000,
          $"visuals ground: road vertices {lift} cm above the collision ground ({rd.Count} of {road.Pos.Count} over it; {off} off by more than 0.5 cm, " +
          $"e.g. a higher layer such as a bridge)");

        var cl = ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)).ToList();
        const float Bucket = 2000;
        var buckets = cl.GroupBy(q => ((int)Math.Floor(q.X / Bucket), (int)Math.Floor(q.Y / Bucket))).ToDictionary(g => g.Key, g => g.ToArray());
        double Dist(Vector2 p)
        {
            int bx = (int)Math.Floor(p.X / Bucket), by = (int)Math.Floor(p.Y / Bucket);
            double best = double.MaxValue;
            for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++)
                    if (buckets.TryGetValue((bx + i, by + j), out var qs)) foreach (var q in qs) best = Math.Min(best, (p - q).LengthSquared());
            return Math.Sqrt(best) / 100;
        }

        // surface heights at the same plan points (every 1 m within 15 m of the centreline), up-facing triangles only:
        // on steep banks a vertex-to-surface comparison mostly measures the two triangulations' plan offsets
        var drawn = new TopSurface(); var collision = new TopSurface();
        foreach (var s in terrain.Append(road))
            for (int t = 0; t + 2 < s.Idx.Count; t += 3) drawn.Add(s.Pos[(int)s.Idx[t]], s.Pos[(int)s.Idx[t + 1]], s.Pos[(int)s.Idx[t + 2]]);
        foreach (var prim in new Glb(Path.Combine(ir.Dir, "collision.glb")).Primitives())
        {
            var ue = new Vector3[prim.Positions.Length / 3];
            for (int v = 0; v < ue.Length; v++)
            {
                var q = pl.ToPhysics(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]);
                ue[v] = new((float)(q.X * 100), (float)(q.Z * 100), (float)(q.Y * 100));
            }
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3) collision.Add(ue[prim.Indices[t]], ue[prim.Indices[t + 1]], ue[prim.Indices[t + 2]]);
        }
        // 5-15 m: verges and bank tops, where DR2's two meshes themselves differ (terrain-compare before placement:
        // p99 0.09 m at 3-10 m, 0.21 m at 10-30 m)
        (double lo, double hi, double p99Max, List<double> d)[] bands = [(0.0, 5.0, 0.05, new()), (5.0, 15.0, 0.25, new())];
        var worst = new List<(double diff, Vector2 p, double hd, double hc, double dist)>();
        var seen1 = new HashSet<(int, int)>();
        foreach (var q in cl)
            for (int dx = -1500; dx <= 1500; dx += 100)
                for (int dy = -1500; dy <= 1500; dy += 100)
                {
                    var p = q + new Vector2(dx, dy);
                    if (!seen1.Add(((int)Math.Floor(p.X / 100), (int)Math.Floor(p.Y / 100)))) continue;
                    double dist = Dist(p);
                    var band = bands.FirstOrDefault(b => dist >= b.lo && dist < b.hi);
                    if (band.d is null || drawn.At(p) is not { } hd || collision.At(p) is not { } hc) continue;
                    band.d.Add(Math.Abs(hd - hc) / 100);
                    if (Math.Abs(hd - hc) > 30) worst.Add((Math.Abs(hd - hc) / 100, p, hd / 100, hc / 100, dist));
                }
        var along = new double[cl.Count];
        for (int i = 1; i < cl.Count; i++) along[i] = along[i - 1] + (cl[i] - cl[i - 1]).Length() / 100;
        string Where(Vector2 p) { int k = Enumerable.Range(0, cl.Count).MinBy(i => (cl[i] - p).LengthSquared()); return $"{along[k]:0} m along"; }
        foreach (var x in worst.OrderByDescending(x => x.diff).Take(5))
            Console.WriteLine($"  ground height outlier: {Where(x.p)}, {x.dist:0.0} m off, drawn {x.hd:0.00} m vs collision {x.hc:0.00} m ({(x.hd > x.hc ? "drawn above" : "drawn below")})");
        Console.WriteLine($"  ground height outliers over 30 cm: {worst.Count(x => x.hd > x.hc)} drawn above, {worst.Count(x => x.hd < x.hc)} drawn below; by 100 m along: " +
                          string.Join(", ", worst.GroupBy(x => (int)(double.Parse(Where(x.p).Split(' ')[0]) / 100)).OrderBy(g => g.Key).Select(g => $"{g.Key * 100}: {g.Count()}")));
        foreach (var (lo, hi, p99Max, d) in bands)
        {
            d.Sort();
            double p99 = d.Count == 0 ? 0 : d[(int)(0.99 * (d.Count - 1))], mx = d.Count == 0 ? 0 : d[^1];
            int over = d.Count(x => x > 0.3);
            C(d.Count > 0 && p99 <= p99Max && over <= d.Count / 200,
              $"visuals ground: drawn ground vs collision ground top height {lo:0}-{hi:0} m from the centreline: {d.Count} plan points, " +
              $"|difference| median {(d.Count > 0 ? d[d.Count / 2] * 100 : 0):0.0} cm, p99 {p99 * 100:0.0} cm (limit {p99Max * 100:0} cm), " +
              $"over 30 cm {over} (limit 0.5%), max {mx:0.00} m");
        }

        var covered = new PlanMask(100);
        foreach (var s in terrain.Append(road))
            for (int t = 0; t + 2 < s.Idx.Count; t += 3) covered.Add(s.Pos[(int)s.Idx[t]], s.Pos[(int)s.Idx[t + 1]], s.Pos[(int)s.Idx[t + 2]]);
        double reach = cfg.Num("ground", "hole_check_m") * 100;
        var seen = new HashSet<(int, int)>(); int samples = 0, holes = 0; var first = new List<string>();
        for (int k = 0; k < cl.Count; k++)
            for (double dx = -reach; dx <= reach; dx += 200)
                for (double dy = -reach; dy <= reach; dy += 200)
                {
                    var p = cl[k] + new Vector2((float)dx, (float)dy);
                    if (dx * dx + dy * dy > reach * reach || !seen.Add(((int)Math.Floor(p.X / 200), (int)Math.Floor(p.Y / 200)))) continue;
                    if (p.X < StageBuild.PhysMinX * 100 || p.X > StageBuild.PhysMaxX * 100 || p.Y < StageBuild.PhysMinZ * 100 || p.Y > StageBuild.PhysMaxZ * 100) continue;
                    samples++;
                    if (covered[new Vector3(p, 0)]) continue;
                    holes++;
                    // drawn ground 10 cm around it: a seam between triangles rather than an opening
                    int around = new[] { new Vector2(10, 0), new Vector2(-10, 0), new Vector2(0, 10), new Vector2(0, -10) }.Count(o => drawn.At(p + o) is not null);
                    if (first.Count < 5) first.Add($"({p.X / 100:0},{p.Y / 100:0}) {Dist(p):0} m off, drawn ground at {around} of 4 points 10 cm around");
                }
        C(holes <= samples / 1000,
          $"visuals ground: {samples} plan samples every 2 m within {reach / 100:0} m of the centreline (inside the physics bounds): {holes} over no drawn ground" +
          (holes > 0 ? $" ({string.Join(", ", first)})" : ""));
        return 0;
    }

    /// v1.10 ground: DR2's render terrain (render/terrain.glb) inside the physics bounds instead of the collision ground,
    /// which only covers a ribbon along the road. Render triangles whose vertices, edge midpoints and centre all lie over the collision
    /// road (cells of [ground] road_mask_cm) are dropped, the road section stays (RoadUv) and is raised by road_lift_cm:
    /// both meshes share heights over the road. Collision ground triangles over no render terrain are kept as a fill.
    /// keepWinding (v1.11): render triangles keep DR2's winding (per mesh, turned so most of it faces up) instead of each
    /// being turned to face up, which turned folded or overhanging faces away from the camera (back-face culled).
    static (ProcMesh.Section, JsonObject) RenderGround(StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, ProcMesh.Section collisionTerrain, ProcMesh.Section road,
                                                       bool keepWinding = false)
    {
        var roadMask = new PlanMask(cfg.Num("ground", "road_mask_cm"));
        for (int t = 0; t + 2 < road.Idx.Count; t += 3) roadMask.Add(road.Pos[(int)road.Idx[t]], road.Pos[(int)road.Idx[t + 1]], road.Pos[(int)road.Idx[t + 2]]);
        var covered = new PlanMask(cfg.Num("ground", "fill_cell_cm"));

        var terrain = new ProcMesh.Section();
        int source = 0, cropped = 0, underRoad = 0, againstUp = 0;
        var perMat = new Dictionary<string, int>();
        foreach (var prim in new Glb(Path.Combine(ir.Dir, "render", "terrain.glb")).Primitives())
        {
            int nv = prim.Positions.Length / 3;
            var ue = new Vector3[nv];
            for (int v = 0; v < nv; v++)
            {
                var p = pl.ToPhysics(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]);
                ue[v] = new((float)(p.X * 100), (float)(p.Z * 100), (float)(p.Y * 100));
            }
            var map = new int[nv]; Array.Fill(map, -1);
            int Add(uint v)
            {
                if (map[v] >= 0) return map[v];
                map[v] = terrain.Pos.Count;
                terrain.Pos.Add(ue[v]); terrain.Nrm.Add(Vector3.Zero); terrain.Uv.Add(new Vector2(ue[v].X / 400, ue[v].Y / 400));
                return map[v];
            }
            // keepWinding: the mesh's own winding, turned so that most of it faces up (UE front face: cross points down)
            int upVotes = 0;
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3)
                upVotes += Math.Sign(Vector3.Cross(ue[prim.Indices[t + 1]] - ue[prim.Indices[t]], ue[prim.Indices[t + 2]] - ue[prim.Indices[t]]).Z);
            bool flip = upVotes > 0;
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3)
            {
                source++;
                uint a = prim.Indices[t], b = prim.Indices[t + 1], c = prim.Indices[t + 2];
                var g = (ue[a] + ue[b] + ue[c]) / 3;
                if (g.X < StageBuild.PhysMinX * 100 || g.X > StageBuild.PhysMaxX * 100 || g.Y < StageBuild.PhysMinZ * 100 || g.Y > StageBuild.PhysMaxZ * 100) { cropped++; continue; }
                covered.Add(ue[a], ue[b], ue[c]);
                if (new[] { ue[a], ue[b], ue[c], g, (ue[a] + ue[b]) / 2, (ue[b] + ue[c]) / 2, (ue[c] + ue[a]) / 2 }.All(q => roadMask[q])) { underRoad++; continue; }
                if (!keepWinding) AddTri(terrain, Add(a), Add(b), Add(c));
                else if (flip) { int ia = Add(a), ic = Add(c), ib = Add(b); AddTriKeep(terrain, ia, ic, ib); }
                else { int ia = Add(a), ib = Add(b), ic = Add(c); AddTriKeep(terrain, ia, ib, ic); }
                if (keepWinding && Vector3.Cross(ue[b] - ue[a], ue[c] - ue[a]).Z * (flip ? -1 : 1) > 0) againstUp++;
                var key = prim.Material.Split('!')[0];
                perMat[key] = perMat.GetValueOrDefault(key) + 1;
            }
        }

        int fill = 0, collisionTris = collisionTerrain.Idx.Count / 3;
        var fmap = new Dictionary<uint, int>();
        int FAdd(uint v)
        {
            if (fmap.TryGetValue(v, out var k)) return k;
            fmap[v] = k = terrain.Pos.Count;
            var p = collisionTerrain.Pos[(int)v];
            terrain.Pos.Add(p); terrain.Nrm.Add(Vector3.Zero); terrain.Uv.Add(new Vector2(p.X / 400, p.Y / 400));
            return k;
        }
        for (int t = 0; t + 2 < collisionTerrain.Idx.Count; t += 3)
        {
            uint a = collisionTerrain.Idx[t], b = collisionTerrain.Idx[t + 1], c = collisionTerrain.Idx[t + 2];
            var g = (collisionTerrain.Pos[(int)a] + collisionTerrain.Pos[(int)b] + collisionTerrain.Pos[(int)c]) / 3;
            if (covered[g]) continue;
            fill++;
            AddTri(terrain, FAdd(a), FAdd(b), FAdd(c));
        }

        float lift = (float)cfg.Num("ground", "road_lift_cm");
        for (int i = 0; i < road.Pos.Count; i++) road.Pos[i] += new Vector3(0, 0, lift);

        var r = new JsonObject
        {
            ["source"] = "render/terrain.glb",
            ["render_triangles"] = source,
            ["outside_physics_bounds"] = cropped,
            ["under_collision_road_dropped"] = underRoad,
            ["render_triangles_drawn"] = new JsonObject(perMat.OrderBy(kv => kv.Key).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["collision_fill_triangles"] = fill,
            ["collision_terrain_triangles_not_drawn"] = collisionTris - fill,
            ["road_lift_cm"] = lift,
        };
        if (keepWinding) r["dr2_winding_kept"] = $"{againstUp} drawn triangles face down as DR2 wound them (folds, overhangs)";
        Console.WriteLine($"visuals: render ground {source - cropped - underRoad} of {source} terrain.glb triangles ({cropped} outside the physics bounds, " +
                          $"{underRoad} under the collision road), {fill} collision fill triangles, road raised {lift} cm");
        return (terrain, r);
    }

    /// Box projection in place: each triangle takes UV0 from the axis plane its face normal points along most (UE
    /// X/Y/Z), one texture repeat every `repeatCm`; vertices are duplicated where triangles on different planes meet
    /// (normals kept smooth).
    static string BoxUv(ProcMesh.Section s, float repeatCm)
    {
        var pos = s.Pos.ToList(); var nrm = s.Nrm.ToList(); var idx = s.Idx.ToList();
        s.Pos.Clear(); s.Nrm.Clear(); s.Uv.Clear(); s.Idx.Clear(); s.Tan.Clear();
        var map = new Dictionary<(uint v, int axis), uint>();
        var perAxis = new int[3];
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            var n = Vector3.Cross(pos[(int)idx[t + 1]] - pos[(int)idx[t]], pos[(int)idx[t + 2]] - pos[(int)idx[t]]);
            var a = Vector3.Abs(n);
            int axis = a.Z >= a.X && a.Z >= a.Y ? 2 : a.X >= a.Y ? 0 : 1;
            perAxis[axis]++;
            for (int k = 0; k < 3; k++)
            {
                uint v = idx[t + k];
                if (!map.TryGetValue((v, axis), out var nv))
                {
                    var p = pos[(int)v];
                    map[(v, axis)] = nv = (uint)s.Pos.Count;
                    s.Pos.Add(p); s.Nrm.Add(nrm[(int)v]);
                    s.Uv.Add(axis switch { 2 => new Vector2(p.X, p.Y), 0 => new Vector2(p.Y, p.Z), _ => new Vector2(p.X, p.Z) } / repeatCm);
                }
                s.Idx.Add(nv);
            }
        }
        return $"box: {perAxis[2]} triangles on the plan plane, {perAxis[0]} on X, {perAxis[1]} on Y; {pos.Count} -> {s.Pos.Count} vertices, repeat {repeatCm / 100} m";
    }

    /// colourMapUv: UV1 = the DR2 glb plan position over the vista's plan bounds (0..1; how DR2's large vista colour map
    /// lies, see acr-ref vista-map).
    static (ProcMesh.Section, JsonObject) BuildVista(StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, IReadOnlyList<ProcMesh.Section> groundSecs,
                                                     bool colourMapUv = false)
    {
        double cellSize = cfg.Num("vista", "exclude_cell_m") * 100;
        (long, long) Key(float x, float y) => ((long)Math.Floor(x / cellSize), (long)Math.Floor(y / cellSize));
        var ground = new HashSet<(long, long)>();
        foreach (var s in groundSecs)
            for (int t = 0; t + 2 < s.Idx.Count; t += 3)
                foreach (var k in new[] { 0, 1, 2 }) { var v = s.Pos[(int)s.Idx[t + k]]; ground.Add(Key(v.X, v.Y)); }

        var vista = new ProcMesh.Section();
        int dropped = 0, source = 0;
        var prims = new Glb(Path.Combine(ir.Dir, "render", "vista.glb")).Primitives().ToList();
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        foreach (var prim in prims)
            for (int v = 0; v < prim.Positions.Length / 3; v++)
            {
                x0 = Math.Min(x0, prim.Positions[v * 3]); x1 = Math.Max(x1, prim.Positions[v * 3]);
                z0 = Math.Min(z0, prim.Positions[v * 3 + 2]); z1 = Math.Max(z1, prim.Positions[v * 3 + 2]);
            }
        foreach (var prim in prims)
        {
            int nv = prim.Positions.Length / 3;
            var ue = new Vector3[nv];
            for (int v = 0; v < nv; v++)
            {
                var p = pl.ToPhysics(prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2]);
                ue[v] = new((float)(p.X * 100), (float)(p.Z * 100), (float)(p.Y * 100));
            }
            var map = new int[nv]; Array.Fill(map, -1);
            int Add(uint v)
            {
                if (map[v] >= 0) return map[v];
                map[v] = vista.Pos.Count;
                vista.Pos.Add(ue[v]); vista.Nrm.Add(Vector3.Zero); vista.Uv.Add(new Vector2(ue[v].X / 400, ue[v].Y / 400));
                if (colourMapUv) vista.Uv1.Add(new Vector2((prim.Positions[v * 3] - x0) / (x1 - x0), (prim.Positions[v * 3 + 2] - z0) / (z1 - z0)));
                return map[v];
            }
            for (int t = 0; t + 2 < prim.Indices.Length; t += 3)
            {
                source++;
                uint a = prim.Indices[t], b = prim.Indices[t + 1], c = prim.Indices[t + 2];
                var g = (ue[a] + ue[b] + ue[c]) / 3;
                if (ground.Contains(Key(g.X, g.Y))) { dropped++; continue; }
                AddTri(vista, Add(a), Add(b), Add(c));
            }
        }
        for (int i = 0; i < vista.Nrm.Count; i++) vista.Nrm[i] = vista.Nrm[i] == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(vista.Nrm[i]);

        var cl = ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)).ToArray();
        double nearest = double.MaxValue;
        foreach (var v in vista.Pos) foreach (var q in cl) nearest = Math.Min(nearest, (new Vector2(v.X, v.Y) - q).LengthSquared());
        var (mn, mx) = ProcMesh.Bounds(vista.Pos);
        var r = new JsonObject
        {
            ["source_triangles"] = source,
            ["triangles"] = vista.Idx.Count / 3,
            ["dropped_under_ground"] = dropped,
            ["nearest_to_centreline_m"] = Math.Round(Math.Sqrt(nearest) / 100, 1),
            ["bounds_m"] = $"({mn.X / 100:0},{mn.Y / 100:0},{mn.Z / 100:0})..({mx.X / 100:0},{mx.Y / 100:0},{mx.Z / 100:0})",
        };
        Console.WriteLine($"visuals: vista {r["triangles"]} of {source} triangles ({dropped} under the drawn ground dropped), " +
                          $"nearest vertex {r["nearest_to_centreline_m"]} m from the centreline, bounds {r["bounds_m"]}");
        return (vista, r);
    }

    /// Adds a triangle as wound (UE front face: cross(b - a, c - a) points against the normal) and accumulates normals.
    static void AddTriKeep(ProcMesh.Section s, int a, int b, int c)
    {
        var up = -Vector3.Cross(s.Pos[b] - s.Pos[a], s.Pos[c] - s.Pos[a]);
        s.Nrm[a] += up; s.Nrm[b] += up; s.Nrm[c] += up;
        s.Idx.Add((uint)a); s.Idx.Add((uint)b); s.Idx.Add((uint)c);
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
