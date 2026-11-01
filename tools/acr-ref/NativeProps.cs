using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Plant = AcrRef.NativeFoliage.Plant;
using MeshInfo = AcrRef.NativeFoliage.MeshInfo;
using V3 = AcrRef.Splines.V3;

namespace AcrRef;

/// v1.14 native props: DR2's man-made objects (fences, posts, signs, start gantry, timers, cones, rocks) drawn as ACR's
/// own static meshes from any ACR map ([native_props] in config/scenery.toml), plus ground grass along the road.
///
/// Hosts in Hafren's persistent level, all hidden by ACR during a stage through their ServicePark tag (renamed on use):
/// - instanced: PCG stamp HISM components (as NativeFoliage), one ACR mesh each, any number of instances with a full
///   transform; rules marked "instanced" (fences tiled along their length, rocks scaled to the DR2 size);
/// - single: the service park's StaticMeshActors, one prop each, upright at scale 1. Their StaticMeshComponent's
///   RelativeLocation and RelativeRotation (doubles, present in the export data) and StaticMesh import are rewritten
///   in place. Hosts with OverrideMaterials or a RelativeScale3D are not used. Props nearest the road get hosts first;
///   the rest stay drawn as before (DR2 meshes / collider shapes in the visuals mesh).
/// Replaced DR2 instances are left out of the visuals mesh; their colliders stay in BakedData. The plan is made before
/// the visuals (so they know what is replaced) and applied to the persistent level after the scenery step.
internal static class NativeProps
{
    sealed record Rule(Regex Template, string[] Meshes, string Fit, bool Instanced, string? Material, string Text);

    static List<Rule> Rules(Toml cfg) => cfg.Arr("native_props", "rules").Cast<string>().Select(s =>
    {
        // "<template regex> => <mesh>, <mesh> [; fit none|height|size|length] [; instanced] [; material <MI path>]"
        var parts = s.Split(';').Select(x => x.Trim()).ToArray();
        var m = Regex.Match(parts[0], @"^(.+?)\s*=>\s*(.+)$");
        if (!m.Success) throw new ArgumentException($"[native_props] rule not understood: {s}");
        string fit = "none"; bool inst = false; string? mat = null;
        foreach (var opt in parts.Skip(1))
        {
            if (opt == "instanced") inst = true;
            else if (Regex.Match(opt, @"^material\s+(/Game/\S+)$") is { Success: true } mm) mat = mm.Groups[1].Value;
            else if (Regex.Match(opt, @"^fit\s+(none|height|size|length)$") is { Success: true } f) fit = f.Groups[1].Value;
            else throw new ArgumentException($"[native_props] rule option not understood: {opt} in {s}");
        }
        if (!inst && fit != "none") throw new ArgumentException($"[native_props] single (not instanced) props are placed at scale 1: {s}");
        if (inst && mat is not null) throw new ArgumentException($"[native_props] material is for single props (a host's OverrideMaterials slot): {s}");
        return new Rule(new Regex(m.Groups[1].Value), m.Groups[2].Value.Split(',').Select(x => x.Trim()).ToArray(), fit, inst, mat, s);
    }).ToList();

    public sealed record Single(string Id, MeshInfo Mesh, V3 At, double Yaw, double Dist, string? Material);
    /// A service-park host; Override: it has an OverrideMaterials array (rewritten: slot 0 = the rule's material or
    /// none, the other slots none, so the mesh's own materials show).
    internal sealed record Host(int Comp, int Actor, string Name, bool Override);

    public sealed class Plan
    {
        public readonly HashSet<string> Replaced = new();
        public bool HideMarkers, HideFlatColours;
        public readonly Dictionary<string, MeshInfo> Meshes = new();
        public readonly Dictionary<string, List<Plant>> Instanced = new();
        public readonly List<Single> Singles = new();
        internal List<Host> Hosts = new();
        public List<Plant> Grass = new();
        public JsonObject Report = new();
    }

    /// Stamp actors whose components host instanced props or grass (kept visible by the scenery step).
    public static HashSet<string> KeepActors(Toml cfg) =>
        cfg.Arr("native_props", "instanced_hosts").Cast<string>().Append(cfg.Str("native_props", "grass_host")).Select(h => h.Split('/')[0]).ToHashSet();

    static double Opt(Toml cfg, string key, double dflt) => cfg["native_props"].TryGetValue(key, out var v) && v is double d ? d : dflt;

    public static Plan Make(Options o, string legacyPersistent, Toml cfg, StageBuild.Ir ir, StageBuild.Placement pl, ObjectColliders.Result objs)
    {
        var plan = new Plan { HideMarkers = Opt(cfg, "hide_markers", 1) != 0, HideFlatColours = Opt(cfg, "hide_flat_colours", 1) != 0 };
        var rules = Rules(cfg);
        using var provider = Program.Open(o);
        foreach (var path in rules.SelectMany(r => r.Meshes).Distinct()) plan.Meshes[path] = NativeFoliage.LoadMesh(provider, path);

        // DR2 template bounds (template space, metres; Y up)
        var tb = new Dictionary<string, (Vector3 mn, Vector3 mx)>();
        foreach (var g in new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().GroupBy(p => p.Mesh))
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var p in g)
                for (int i = 0; i < p.Positions.Length; i += 3)
                {
                    var v = new Vector3(p.Positions[i], p.Positions[i + 1], p.Positions[i + 2]);
                    mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v);
                }
            tb[g.Key] = (mn, mx);
        }

        var centre = ir.Centre.Select(pl.ToUe).ToList();
        double DistToCentre(V3 p) => Math.Sqrt(centre.Min(c => (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y)));
        var perRule = new Dictionary<string, int>();
        var singles = new List<(Single s, string rule)>();
        var instIds = new Dictionary<string, List<(string id, List<Plant> plants)>>();
        foreach (var inst in JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!["instances"]!.AsArray())
        {
            var tname = inst!["template"]!.GetValue<string>();
            var rule = rules.FirstOrDefault(r => r.Template.IsMatch(tname));
            if (rule is null || !tb.TryGetValue(tname, out var bounds)) continue;
            string id = inst["id"]!.GetValue<string>();
            var m = ObjectColliders.Trs(inst["transform"]);
            V3 Ue(Vector3 t) { var w = Vector3.Transform(t, m); return pl.ToUe([w.X, w.Y, w.Z]); }
            var (mn, mx) = bounds;
            var c = (mn + mx) / 2;
            bool longX = mx.X - mn.X >= mx.Z - mn.Z;
            var half = longX ? new Vector3((mx.X - mn.X) / 2, 0, 0) : new Vector3(0, 0, (mx.Z - mn.Z) / 2);
            var p0 = Ue(new Vector3(c.X, 0, c.Z));
            var lv = Ue(new Vector3(c.X, 0, c.Z) + half) - Ue(new Vector3(c.X, 0, c.Z) - half);
            double ldr2 = lv.Length, hdr2 = Math.Max(1, (Ue(new Vector3(c.X, mx.Y, c.Z)) - p0).Length);
            var up = Unit(Ue(new Vector3(c.X, 1, c.Z)) - p0);
            var u = ldr2 > 1 ? Unit(lv - up * Dot(lv, up)) : new V3(1, 0, 0);
            if (Math.Abs(u.Z) > 0.99) u = new V3(1, 0, 0);
            var rng = new Random((int)(Hash(id) & 0x7FFFFFFF));
            var mesh = plan.Meshes[rule.Meshes[(int)(Hash(id) % (ulong)rule.Meshes.Length)]];
            bool meshLongX = mesh.Extent.X >= mesh.Extent.Y;
            double lacr = 2 * Math.Max(mesh.Extent.X, mesh.Extent.Y), hacr = Math.Max(1, mesh.Origin.Z + mesh.Extent.Z);
            perRule[rule.Text] = perRule.GetValueOrDefault(rule.Text) + 1;
            if (!rule.Instanced)
            {
                // upright, yawed so the mesh's long plan axis follows the DR2 object's; plan centres on each other
                double yaw = Math.Atan2(u.Y, u.X) * 180 / Math.PI - (meshLongX ? 0 : 90);
                double cy = Math.Cos(yaw * Math.PI / 180), sy = Math.Sin(yaw * Math.PI / 180);
                var at = new V3(p0.X - (cy * mesh.Origin.X - sy * mesh.Origin.Y), p0.Y - (sy * mesh.Origin.X + cy * mesh.Origin.Y), p0.Z);
                singles.Add((new Single(id, mesh, at, yaw, DistToCentre(p0), rule.Material), rule.Text));
                continue;
            }
            int k = 1; double sL = 1, sS = 1, sH = 1;
            switch (rule.Fit)
            {
                case "height": sL = sS = sH = Math.Clamp(hdr2 / hacr, 0.3, 3); break;
                case "size": sL = sS = sH = Math.Clamp((ldr2 / lacr + hdr2 / hacr) / 2, 0.3, 3); break;
                case "length":
                    k = Math.Max(1, (int)Math.Round(ldr2 / lacr));
                    sL = ldr2 / (k * lacr); sH = Math.Clamp(hdr2 / hacr, 0.5, 2); break;
            }
            if (rule.Fit is "size" or "height")
            {
                // natural objects: random yaw about the DR2 up axis
                double a = rng.NextDouble() * 2 * Math.PI;
                var side = Unit(Cross(up, u));
                u = u * Math.Cos(a) + side * Math.Sin(a);
            }
            var list = new List<Plant>();
            for (int j = 0; j < k; j++)
            {
                var ctr = p0 + u * (((j + 0.5) / k - 0.5) * ldr2);
                V3 ax, ay, az = up * sH;
                if (meshLongX) { ax = u * sL; ay = Unit(Cross(up, u)) * sS; }
                else { ay = u * sL; ax = Unit(Cross(u, up)) * sS; }
                var at = ctr - ax * mesh.Origin.X - ay * mesh.Origin.Y;
                list.Add(new Plant(at, ax, ay, az));
            }
            if (!instIds.TryGetValue(mesh.Path, out var l)) instIds[mesh.Path] = l = new();
            l.Add((id, list));
        }

        // instanced: one host component per mesh, most instances first
        var hosts = cfg.Arr("native_props", "instanced_hosts").Cast<string>().ToList();
        int hi = 0;
        var notHosted = new JsonArray();
        foreach (var (path, l) in instIds.OrderByDescending(kv => kv.Value.Sum(x => x.plants.Count)))
        {
            if (hi >= hosts.Count) { notHosted.Add($"{plan.Meshes[path].Name}: {l.Count} DR2 objects (no instanced host left)"); continue; }
            hi++;
            plan.Instanced[path] = l.SelectMany(x => x.plants).ToList();
            foreach (var (id, _) in l) plan.Replaced.Add(id);
        }

        // single hosts: ServicePark StaticMeshActors whose component can be rewritten in place
        var pkg = new LegacyPackage(legacyPersistent);
        var exports = provider.LoadPackage(Scenery.PersistentLevel).GetExports().ToList();
        if (exports.Count != pkg.ExportCount) throw new InvalidDataException("CUE4Parse export count differs from the legacy persistent level");
        var rejected = new Dictionary<string, int>();
        for (int a = 0; a < exports.Count; a++)
        {
            if (exports[a].ExportType != "StaticMeshActor") continue;
            var aj = JsonNode.Parse(JsonConvert.SerializeObject(exports[a]))!["Properties"]!;
            var tags = (aj["Tags"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
            if (tags is not ["ServicePark"]) continue;
            int ci = Enumerable.Range(0, pkg.ExportCount).FirstOrDefault(i => pkg.ExportOuter(i) == a + 1 && pkg.ExportClass(i) == "StaticMeshComponent", -1);
            if (ci < 0) { rejected["no StaticMeshComponent"] = rejected.GetValueOrDefault("no StaticMeshComponent") + 1; continue; }
            var cprops = JsonNode.Parse(JsonConvert.SerializeObject(exports[ci]))!["Properties"]!;
            var why = HostProblem(pkg, ci, cprops, out _);
            if (why is not null) { rejected[why] = rejected.GetValueOrDefault(why) + 1; continue; }
            plan.Hosts.Add(new Host(ci, a, pkg.ExportName(a), cprops["OverrideMaterials"] is not null));
        }
        // nearest the road first; props that need a material take hosts with an OverrideMaterials slot
        var placed = singles.OrderBy(x => x.s.Dist).Take(plan.Hosts.Count).ToList();
        var withOverride = new Queue<Host>(plan.Hosts.Where(h => h.Override));
        var plain = new Queue<Host>(plan.Hosts.Where(h => !h.Override));
        var hostsInOrder = new List<Host>();
        var noOverrideLeft = new List<string>();
        foreach (var x in placed.ToList())
        {
            if (x.s.Material is not null)
            {
                if (withOverride.Count > 0) { hostsInOrder.Add(withOverride.Dequeue()); continue; }
                noOverrideLeft.Add(x.s.Id); placed.Remove(x); continue;
            }
            hostsInOrder.Add(plain.Count > 0 ? plain.Dequeue() : withOverride.Dequeue());
        }
        plan.Hosts = hostsInOrder;
        plan.Singles.AddRange(placed.Select(x => x.s));
        foreach (var x in placed) plan.Replaced.Add(x.s.Id);
        var singleRules = placed.GroupBy(x => x.rule).ToDictionary(g => g.Key, g => g.Count());

        // grass: clumps scattered at random beside the road (density grass_per_100m2, ramping up from grass_min_offset_m
        // over grass_ramp_m), on DR2 ground no steeper than grass_max_slope_deg, the whole clump off road surfaces
        var ground = new SurfaceGrid(Path.Combine(ir.Dir, "collision.glb"), pl);
        var grassMesh = cfg.Str("native_props", "grass_mesh");
        double density = Opt(cfg, "grass_per_100m2", 20) / 1e6, gMin = Opt(cfg, "grass_min_offset_m", 3) * 100, gMax = Opt(cfg, "grass_max_offset_m", 18) * 100;
        double ramp = Opt(cfg, "grass_ramp_m", 4) * 100, gSlope = Math.Tan(Opt(cfg, "grass_max_slope_deg", 35) * Math.PI / 180);
        int gDropped = 0, eCount = 0;
        if (density > 0)
        {
            var gm = plan.Meshes[grassMesh] = NativeFoliage.LoadMesh(provider, grassMesh);
            double rad = Math.Max(gm.Extent.X, gm.Extent.Y) * 1.2;
            var grng = new Random(12345);
            for (int i = 0; i + 1 < centre.Count; i++)
            {
                var a = centre[i]; var b = centre[i + 1];
                var d = new V3(b.X - a.X, b.Y - a.Y, 0); double len = d.Length;
                if (len < 1) continue;
                var dir = d * (1 / len); var nrm = new V3(-dir.Y, dir.X, 0);
                int n = (int)Math.Round(len * 2 * (gMax - gMin) * density);
                for (int k = 0; k < n; k++)
                {
                    double ja = grng.NextDouble() * len, jo = gMin + grng.NextDouble() * (gMax - gMin), side = grng.Next(2) * 2 - 1;
                    double yaw = grng.NextDouble() * 360, sc = 0.7 + 0.6 * grng.NextDouble(), keep = grng.NextDouble();
                    var q = new V3(a.X + dir.X * ja + nrm.X * side * jo, a.Y + dir.Y * ja + nrm.Y * side * jo, 0);
                    double dc = DistToCentre(q);
                    if (dc < gMin || keep > Math.Clamp((dc - gMin) / ramp, 0.15, 1)) continue;
                    double r = rad * sc;
                    if (new[] { (0.0, 0.0), (r, 0), (-r, 0), (0, r), (0, -r) }.Any(o => objs.Probe.OnRoadSurface(q.X + o.Item1, q.Y + o.Item2))) { gDropped++; continue; }
                    if (ground.HeightAt(q.X / 100, q.Y / 100) is not { } h) continue;
                    double hx = ground.HeightAt((q.X + 100) / 100, q.Y / 100) ?? h, hy = ground.HeightAt(q.X / 100, (q.Y + 100) / 100) ?? h;
                    if (Math.Max(Math.Abs(hx - h), Math.Abs(hy - h)) > gSlope) continue;
                    plan.Grass.Add(Plant.Yawed(q with { Z = h * 100 - 3 }, yaw, sc, sc));
                }
            }
            // v1.16 edge grass: vanilla gravel roads have no edge line, tufts grow over the outer gravel. Small clumps
            // every edge_grass_spacing_m along both sides (a random edge_grass_keep share of them), centred
            // edge_grass_out_m beyond the road surface's edge (found by stepping out from the centreline), so their
            // blades reach over it; the ground there is no steeper than grass_max_slope_deg.
            double eSpace = Opt(cfg, "edge_grass_spacing_m", 0) * 100, eKeep = Opt(cfg, "edge_grass_keep", 0.7);
            double eOut0 = Opt(cfg, "edge_grass_out_min_m", 0) * 100, eOut1 = Opt(cfg, "edge_grass_out_max_m", 0.5) * 100;
            double eSc0 = Opt(cfg, "edge_grass_scale_min", 0.5), eSc1 = Opt(cfg, "edge_grass_scale_max", 1.0);
            if (eSpace > 0)
            {
                var erng = new Random(23456);
                for (int i = 0; i + 1 < centre.Count; i++)
                {
                    var a = centre[i]; var b = centre[i + 1];
                    var d = new V3(b.X - a.X, b.Y - a.Y, 0); double len = d.Length;
                    if (len < 1) continue;
                    var dir = d * (1 / len); var nrm = new V3(-dir.Y, dir.X, 0);
                    for (double ja = erng.NextDouble() * eSpace; ja < len; ja += eSpace * (0.6 + 0.8 * erng.NextDouble()))
                        foreach (int side in new[] { -1, 1 })
                        {
                            double keep = erng.NextDouble(), yaw = erng.NextDouble() * 360, sc = eSc0 + (eSc1 - eSc0) * erng.NextDouble();
                            double out0 = eOut0 + (eOut1 - eOut0) * erng.NextDouble();
                            if (keep > eKeep) continue;
                            var p0 = new V3(a.X + dir.X * ja, a.Y + dir.Y * ja, 0);
                            // the road surface's edge: first point off it stepping out 10 cm at a time, the outermost of
                            // three lines 0.75 m apart (the road's edge is saw-toothed; the drawn road follows the tips)
                            double edge = -1;
                            foreach (double dl in new[] { -75.0, 0, 75 })
                                for (double step = 100; step <= gMin + 300; step += 10)
                                    if (!objs.Probe.OnRoadSurface(p0.X + dir.X * dl + nrm.X * side * step, p0.Y + dir.Y * dl + nrm.Y * side * step))
                                    { edge = Math.Max(edge, step); break; }
                            if (edge < 0) continue;
                            var q = new V3(p0.X + nrm.X * side * (edge + out0), p0.Y + nrm.Y * side * (edge + out0), 0);
                            if (DistToCentre(q) < edge - 50) continue;       // inside a bend of another road leg
                            if (ground.HeightAt(q.X / 100, q.Y / 100) is not { } h) continue;
                            double hx = ground.HeightAt((q.X + 100) / 100, q.Y / 100) ?? h, hy = ground.HeightAt(q.X / 100, (q.Y + 100) / 100) ?? h;
                            if (Math.Max(Math.Abs(hx - h), Math.Abs(hy - h)) > gSlope) continue;
                            plan.Grass.Add(Plant.Yawed(q with { Z = h * 100 - 3 }, yaw, sc, sc));
                            eCount++;
                        }
                }
            }
        }

        plan.Report = new JsonObject
        {
            ["dr2_objects_matched"] = new JsonObject(perRule.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["instanced"] = new JsonArray(plan.Instanced.Select(kv => (JsonNode?)$"{plan.Meshes[kv.Key].Name} x {kv.Value.Count}").ToArray()),
            ["instanced_not_hosted"] = notHosted,
            ["single_hosts"] = plain.Count + withOverride.Count + hostsInOrder.Count, ["singles_needing_a_material_slot_unplaced"] = noOverrideLeft.Count,
            ["single_hosts_rejected"] = new JsonObject(rejected.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["singles_placed"] = new JsonObject(singleRules.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["singles_without_host"] = singles.Count - placed.Count,
            ["singles_farthest_placed_m"] = placed.Count > 0 ? Math.Round(placed[^1].s.Dist / 100, 1) : null,
            ["replaced_dr2_objects"] = plan.Replaced.Count,
            ["grass_clumps"] = plan.Grass.Count, ["grass_dropped_touching_road"] = gDropped, ["edge_grass_clumps"] = eCount,
            ["hide_markers"] = plan.HideMarkers,
        };
        return plan;
    }

    /// Why a service-park StaticMeshComponent cannot host a prop (null: it can); the offsets of its location, rotation
    /// and StaticMesh reference.
    static string? HostProblem(LegacyPackage pkg, int ci, JsonNode props, out (int loc, int rot, int mesh, int oldMesh, int om, int[] omIds) at)
    {
        at = default;
        if (props["RelativeScale3D"] is not null) return "RelativeScale3D";
        if (props["RelativeLocation"] is not { } loc || props["RelativeRotation"] is not { } rot || props["StaticMesh"] is not { } sm) return "no location, rotation or mesh";
        var d = pkg.GetExportData(ci);
        var r = new[] { rot["Pitch"]!.GetValue<double>(), rot["Yaw"]!.GetValue<double>(), rot["Roll"]!.GetValue<double>() };
        if (r.All(x => x == 0)) return "zero rotation (not serialized)";
        var lh = NativeFoliage.FindDoubles(d, NativeFoliage.Vec(loc), d.Length);
        var rh = NativeFoliage.FindDoubles(d, r, d.Length);
        if (lh.Count != 1 || rh.Count != 1) return "location or rotation not found once";
        string oldName = sm["ObjectName"]!.GetValue<string>().Split('\'')[1];
        int oldMesh = Enumerable.Range(0, pkg.ImportCount).Where(i => pkg.ImportClassName(i) == "StaticMesh" && pkg.ImportName(i) == oldName).Select(i => -i - 1).FirstOrDefault();
        if (oldMesh == 0) return "mesh import not found";
        var mh = NativeFoliage.Find(d, NativeFoliage.Ints(oldMesh));
        if (mh.Count != 1) return "mesh reference not found once";
        int om = -1; int[] omIds = [];
        if (props["OverrideMaterials"] is JsonArray oms)
        {
            // TArray<UMaterialInterface*>: count, then package indices (0 = none)
            omIds = oms.Select(x => x is null ? 0 : Enumerable.Range(0, pkg.ImportCount)
                .Where(i => pkg.ImportName(i) == x["ObjectName"]!.GetValue<string>().Split('\'')[1] && pkg.ImportClassName(i).Contains("Material"))
                .Select(i => -i - 1).FirstOrDefault()).ToArray();
            if (omIds.Length == 0) return "empty OverrideMaterials";
            var oh = NativeFoliage.Find(d, NativeFoliage.Ints([omIds.Length, .. omIds]));
            if (oh.Count != 1) return "OverrideMaterials not found once";
            om = oh[0];
        }
        at = (lh[0], rh[0], mh[0], oldMesh, om, omIds);
        return null;
    }

    public static JsonObject Apply(Options o, string persistentFile, Toml cfg, Plan plan)
    {
        var pkg = new LegacyPackage(persistentFile);
        using var provider = Program.Open(o);
        var exports = provider.LoadPackage(Scenery.PersistentLevel).GetExports().ToList();
        if (exports.Count != pkg.ExportCount) throw new InvalidDataException("CUE4Parse export count differs from the legacy persistent level");
        int none = pkg.AddExportName("None");
        int leaf = (int)Opt(cfg, "leaf_instances", 16);
        string[] drop = ["ServicePark"];
        var used = new JsonArray();

        // instanced hosts (unused ones emptied: their actor is kept visible)
        var hosts = cfg.Arr("native_props", "instanced_hosts").Cast<string>().Select(h => (actor: h.Split('/')[0], comp: h.Split('/')[1])).ToList();
        int hi = 0;
        foreach (var (path, list) in plan.Instanced.OrderByDescending(kv => kv.Value.Count))
        {
            var (an, cn) = hosts[hi++];
            NativeFoliage.RewriteHost(pkg, exports, an, cn, plan.Meshes[path], list, leaf, drop, none);
            used.Add($"{an}/{cn}: {plan.Meshes[path].Name} x {list.Count}");
        }
        for (; hi < hosts.Count; hi++)
        {
            var (an, cn) = hosts[hi];
            NativeFoliage.RewriteHost(pkg, exports, an, cn, null, [], leaf, [], none);
            used.Add($"{an}/{cn}: unused (0 instances)");
        }
        var grassHost = cfg.Str("native_props", "grass_host").Split('/');
        if (plan.Grass.Count > 0)
        {
            NativeFoliage.RewriteHost(pkg, exports, grassHost[0], grassHost[1], plan.Meshes[cfg.Str("native_props", "grass_mesh")], plan.Grass, leaf, drop, none);
            used.Add($"{grassHost[0]}/{grassHost[1]}: grass x {plan.Grass.Count}");
        }

        // single hosts
        var imports = new Dictionary<string, int>();
        for (int i = 0; i < plan.Singles.Count; i++)
        {
            var s = plan.Singles[i]; var h = plan.Hosts[i];
            var props = JsonNode.Parse(JsonConvert.SerializeObject(exports[h.Comp]))!["Properties"]!;
            if (HostProblem(pkg, h.Comp, props, out var at) is { } why) throw new InvalidDataException($"{h.Name}: {why} (after the scenery step)");
            var d = pkg.GetExportData(h.Comp);
            NativeFoliage.Doubles(s.At.X, s.At.Y, s.At.Z).CopyTo(d, at.loc);
            NativeFoliage.Doubles(0, s.Yaw, 0).CopyTo(d, at.rot);
            if (!imports.TryGetValue(s.Mesh.Path, out int newMesh))
            {
                int pkgImp = pkg.AddImport("/Script/CoreUObject", "Package", 0, s.Mesh.Path);
                imports[s.Mesh.Path] = newMesh = pkg.AddImport("/Script/Engine", "StaticMesh", pkgImp, s.Mesh.Name);
            }
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at.mesh), newMesh);
            int newMat = 0;
            if (at.om >= 0)
            {
                if (s.Material is { } mp && !imports.TryGetValue(mp, out newMat))
                {
                    int pkgImp = pkg.AddImport("/Script/CoreUObject", "Package", 0, mp);
                    imports[mp] = newMat = pkg.AddImport("/Script/Engine", "MaterialInstanceConstant", pkgImp, mp[(mp.LastIndexOf('/') + 1)..]);
                }
                for (int k = 0; k < at.omIds.Length; k++) BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at.om + 4 + 4 * k), k == 0 ? newMat : 0);
            }
            else if (s.Material is not null) throw new InvalidDataException($"{h.Name}: {s.Id} needs a material slot");
            pkg.SetExportData(h.Comp, d);
            foreach (var dep in pkg.ExportDeps(h.Comp).Where(x => x.index == at.oldMesh)) pkg.SetDep(dep.pos, newMesh);
            // the first old override material's preload dependency now loads the new material
            if (newMat != 0 && at.omIds[0] != 0)
                foreach (var dep in pkg.ExportDeps(h.Comp).Where(x => x.index == at.omIds[0]).Take(1)) pkg.SetDep(dep.pos, newMat);
            NativeFoliage.DropTags(pkg, h.Actor, drop, none, h.Name);
        }
        pkg.SaveTo(persistentFile);
        var byMesh = plan.Singles.GroupBy(s => s.Mesh.Name).OrderByDescending(g => g.Count()).Select(g => (JsonNode?)$"{g.Key} x {g.Count()}").ToArray();
        Console.WriteLine($"native props: {plan.Instanced.Values.Sum(l => l.Count)} instanced ({plan.Instanced.Count} meshes), {plan.Singles.Count} single props " +
                          $"({plan.Replaced.Count} DR2 objects replaced), {plan.Grass.Count} grass clumps");
        var rep = plan.Report.DeepClone().AsObject();
        rep["hosts"] = used;
        rep["singles"] = new JsonArray(byMesh);
        return rep;
    }

    static ulong Hash(string s)
    {
        ulong h = 1469598103934665603;
        foreach (char c in s) { h ^= c; h *= 1099511628211; }
        return h;
    }

    static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static V3 Unit(V3 a) => a.Length > 1e-9 ? a * (1 / a.Length) : new V3(0, 0, 1);
}
