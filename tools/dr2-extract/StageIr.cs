using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dr2Extract;

/// <summary>
/// <c>stage-ir &lt;stage_id&gt;</c>: DR2 install -> <c>work/ir/&lt;stage_id&gt;/</c> (stage.json, collision.glb, render/*.glb).
/// See docs/stage-ir.md for the schema.
/// </summary>
internal static partial class StageIr
{
    public const int SchemaVersion = 1;

    [GeneratedRegex(@"^(?<loc>[a-z0-9_]+)_route_(?<route>\d+)$")]
    private static partial Regex StageId();

    public static async Task<int> Run(string[] args)
    {
        var id = Program.Positional(args).FirstOrDefault() ?? throw new ArgumentException("stage-ir <stage_id>");
        var m = StageId().Match(id);
        if (!m.Success)
        {
            throw new ArgumentException($"stage id '{id}' is not <location>_route_<n> (e.g. new_zealand_rally_01_route_5)");
        }

        var location = m.Groups["loc"].Value;
        var route = int.Parse(m.Groups["route"].Value);
        var root = Program.Option(args, "--dr2") ?? Dr2Install.DefaultRoot;
        var outDir = Path.Combine(Program.Option(args, "--out") ?? Path.Combine("work", "ir"), id);
        var cache = Program.Option(args, "--cache") ?? Path.Combine("work", "dr2", "cache");
        var corridor = float.Parse(Program.Option(args, "--corridor") ?? "250", System.Globalization.CultureInfo.InvariantCulture);
        var surfaceMap = SurfaceMap.Load(Program.Option(args, "--surface-map") ?? Path.Combine("config", "surface-map.toml"));
        var render = args.Contains("--render");

        var install = await Dr2Install.OpenAsync(root, cache);
        var archive = install.LocationArchive(location);
        await install.OpenLocationAsync(archive);
        var country = Path.GetFileNameWithoutExtension(archive).Split("__")[0];
        var locDir = $"tracks/locations/{country}/{location}";
        var routeDir = $"{locDir}/route_{route}";
        if (!install.Exists($"{routeDir}/progress_track.xml"))
        {
            throw new FileNotFoundException($"{routeDir}/progress_track.xml not found; {location} routes: " +
                string.Join(", ", install.List($"{locDir}/route_").Select(p => p.Split('/')[4]).Distinct()));
        }

        Console.WriteLine($"stage {id}: archive {Path.GetFileName(archive)}");
        var progress = ProgressTrack.Load(await install.FetchAsync($"{routeDir}/progress_track.xml"));
        var codriverPath = $"{routeDir}/{location}_codriver_1_pro_{route}_data.xml";
        var codriver = install.Exists(codriverPath) ? Codriver.Load(await install.FetchAsync(codriverPath)) : null;
        var triggers = install.Exists($"{routeDir}/triggers_game_object.xml")
            ? TriggerBox.Load(await install.FetchAsync($"{routeDir}/triggers_game_object.xml"))
            : [];
        var lng = LngCommands.Load(await install.FetchAsync("language/language_eng.lng"));
        var surfaces = SurfaceMaterials.Load(await install.FetchAsync("surface_materials.xml"));

        var start = progress.GateOf("start");
        var finish = progress.GateOf("finish");
        var name = StageNames.Stage(lng, location, route);
        var locationName = StageNames.Location(lng, location, country);

        // Physics ground: crop to a corridor around the route, map surfaces (fails on unmapped codes).
        Console.WriteLine("reading physics ground...");
        var ground = Dr2Ground.ReadJpk(await install.FetchAsync($"{routeDir}/track.jpk"));
        var inCorridor = new RouteCorridor(progress.Gates.Select(g => g.Crossing).ToList(), corridor);
        var kept = ground.Cells.SelectMany(c => c.Triangles).Where(t => inCorridor.Contains(t.Centroid)).ToList();
        var mapping = surfaceMap.Resolve(kept.Select(t => t.Material));
        Directory.CreateDirectory(outDir);
        var stats = GroundGltf.Write(kept, Path.Combine(outDir, "collision.glb"));
        Console.WriteLine($"collision.glb: {stats.Triangles} triangles within {corridor} m of the route");

        var heights = progress.Gates.Select(g => (Gate: g, Y: Dr2Ground.HeightAt(ground.Cells, g.Crossing.X, g.Crossing.Z))).ToList();
        var dy = heights.Where(h => h.Y is not null).Select(h => MathF.Abs(h.Y!.Value - h.Gate.Crossing.Y)).ToList();

        var renderEntries = new List<object>();
        if (render)
        {
            Console.WriteLine("exporting terrain render mesh (tracksplit.pssg)...");
            var pssg = PssgCommands.Open(await install.FetchAsync($"{locDir}/tracksplit.pssg"));
            // tracksplit.pssg tiers: SHADOWCASTING_i_j = full-detail terrain, DECAL_i_j = road decals over it,
            // LOW_i_j = low LOD of the same area, NONLOD_i_j = distant vista ring.
            foreach (var (file, kind, prefix, crop) in new[]
                     {
                         ("terrain", "terrain_detail", "SHADOWCASTING_", true),
                         ("terrain_decals", "terrain_decals", "DECAL_", true),
                         ("terrain_low", "terrain_low_lod", "LOW_", true),
                         ("vista", "vista", "NONLOD_", false),
                     })
            {
                var glb = Path.Combine(outDir, "render", file + ".glb");
                var result = PssgMeshExport.Export(pssg, glb, crop ? inCorridor.Contains : null, prefix);
                File.WriteAllText(Path.ChangeExtension(glb, ".materials.json"),
                    JsonSerializer.Serialize(result.Materials.Where(x => x.Triangles > 0), Json.Options));
                Console.WriteLine($"render/{file}.glb: {result.Triangles} triangles, {result.RenderNodes} nodes");
                renderEntries.Add(new
                {
                    file = $"render/{file}.glb",
                    kind,
                    materials = $"render/{file}.materials.json",
                    source = $"{locDir}/tracksplit.pssg",
                    nodes = prefix + "i_j",
                    corridor_m = crop ? corridor : (float?)null,
                    triangles = result.Triangles,
                    winding_matches_normals = result.Triangles == 0 ? 0 : Round((float)result.WindingMatchesNormals / result.Triangles, 4),
                    bounds = Bounds(result.Min, result.Max),
                });
            }
        }

        var stage = new Dictionary<string, object?>
        {
            ["schema"] = "acr-dirt/stage-ir",
            ["version"] = SchemaVersion,
            ["id"] = id,
            ["name"] = name,
            ["location"] = new
            {
                id = location,
                country,
                name = locationName,
                archive = $"locations/{Path.GetFileName(archive)}",
                route_index = route,
            },
            ["length_m"] = Round(finish.Distance - start.Distance, 2),
            ["total_distance_m"] = progress.TotalDistance,
            ["track_type"] = progress.TrackType,
            ["direction"] = progress.Direction,
            ["coordinates"] = new
            {
                frame = "dr2_native",
                units = "metres",
                up = "+Y",
                handedness = "right-handed (assumed)",
                notes = "Positions are copied unchanged from DR2 files; for travel direction f, right = normalize(cross(f, up)) " +
                        "matches the gates' 'right' points. glTF files use the same frame (glTF is +Y up, right-handed).",
            },
            ["centreline"] = progress.Gates.Select(g => V(g.Crossing)).ToList(),
            ["gates"] = progress.Gates.Select(g => new
            {
                id = g.Id,
                distance = g.Distance,
                crossing = V(g.Crossing),
                left = V(g.Left),
                right = V(g.Right),
                width = Round(Vector3.Distance(g.Left, g.Right), 2),
            }).ToList(),
            ["timing"] = progress.Splits.Select(s =>
            {
                var g = progress.Gates[s.Gate];
                return new
                {
                    type = s.Type,
                    split_id = s.Id,
                    gate = s.Gate,
                    distance = g.Distance,
                    distance_from_start = Round(g.Distance - start.Distance, 2),
                    position = V(g.Crossing),
                    forward = V(progress.Forward(s.Gate), 5),
                    left = V(g.Left),
                    right = V(g.Right),
                };
            }).ToList(),
            ["pacenotes"] = codriver is null
                ? null
                : new
                {
                    source = codriverPath,
                    distance_reference = "metres along the route as stored (distanceAlongTrack); whether 0 is the progress-track " +
                                         "origin (gates[].distance) or the start line (timing[].distance_from_start) is unverified",
                    shakedown_cutoff_m = codriver.ShakedownCutoff,
                    finish_call_distance_m = codriver.FinishCallDistance,
                    calls = codriver.Calls.Select(c => new
                    {
                        distance = c.Distance,
                        name = c.Name.Length == 0 ? null : c.Name,
                        audio = c.AudioName,
                        subcalls = c.Subcalls.Select(s => new
                        {
                            type = s.Type,
                            delay_ms = s.Delay,
                            angle = s.Angle,
                            modifier_a = s.ModifierA,
                            modifier_b = s.ModifierB,
                            can_concat_mod_a = s.CanConcatModA,
                            can_concat_mod_b = s.CanConcatModB,
                            distance_link = s.DistanceLink,
                        }).ToList(),
                    }).ToList(),
                },
            ["triggers"] = triggers.Select(t => new
            {
                type = t.Type,
                name = t.Name,
                position = V(t.Position),
                forward = V(t.Forward, 5),
                size_whl = V(t.Size),
            }).ToList(),
            ["collision"] = new
            {
                file = "collision.glb",
                source = $"{routeDir}/track.jpk",
                corridor_m = corridor,
                triangles = stats.Triangles,
                bounds = Bounds(stats.Min, stats.Max),
                meshes = stats.TrianglesPerMaterial.Select(kv => new
                {
                    mesh = "collision",
                    gltf_material = kv.Key,
                    dr2_surface = kv.Key,
                    acr_physical_material = mapping[kv.Key],
                    acr_asset_path = SurfaceMap.AssetPrefix + mapping[kv.Key],
                    triangles = kv.Value,
                }).ToList(),
                flags = kept.GroupBy(t => t.Flags).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            },
            ["surfaces"] = mapping.Select(kv =>
            {
                surfaces.TryGetValue(kv.Key, out var s);
                return new
                {
                    code = kv.Key,
                    acr_physical_material = kv.Value,
                    triangles = stats.TrianglesPerMaterial.GetValueOrDefault(kv.Key),
                    dr2 = s,
                };
            }).ToList(),
            ["render"] = renderEntries,
            ["validation"] = new
            {
                ground_height_at_gates = new
                {
                    @checked = heights.Count,
                    missing = heights.Count(h => h.Y is null),
                    max_abs_dy_m = dy.Count == 0 ? (float?)null : Round(dy.Max(), 3),
                    mean_abs_dy_m = dy.Count == 0 ? (float?)null : Round(dy.Average(), 3),
                },
                // Fraction of collision triangles whose (B-A)x(C-A) normal has +Y, i.e. counter-clockwise seen from
                // above in a right-handed Y-up frame (glTF front face).
                collision_ccw_from_above = stats.Triangles == 0 ? 0 : Round((float)stats.UpFacing / stats.Triangles, 4),
            },
            ["sources"] = install.Sources,
        };

        File.WriteAllText(Path.Combine(outDir, "stage.json"), JsonSerializer.Serialize(stage, Json.Options));
        Console.WriteLine($"wrote {Path.Combine(outDir, "stage.json")}: \"{name}\" ({locationName}), " +
                          $"{finish.Distance - start.Distance:F1} m start->finish, {progress.Gates.Count} gates, " +
                          $"{codriver?.Calls.Count ?? 0} pacenotes, surfaces {string.Join(" ", mapping.Select(kv => $"{kv.Key}->{kv.Value}"))}");
        if (heights.Any(h => h.Y is null) || dy.Any(d => d > 2))
        {
            Console.WriteLine($"warning: ground vs gate height check: {heights.Count(h => h.Y is null)} gates without ground, max |dy| {dy.DefaultIfEmpty().Max():F2} m");
        }

        return 0;
    }

    internal static float Round(float v, int digits) => MathF.Round(v, digits);

    internal static float[] V(Vector3 v, int digits = 3) => [Round(v.X, digits), Round(v.Y, digits), Round(v.Z, digits)];

    internal static object Bounds(Vector3 min, Vector3 max) => new { min = V(min), max = V(max) };
}

/// <summary>Point-in-corridor test (XZ distance to a polyline) accelerated with a uniform grid.</summary>
internal sealed class RouteCorridor
{
    private readonly float _radius;
    private readonly Dictionary<(int, int), List<(Vector2 A, Vector2 B)>> _grid = new();

    public RouteCorridor(IReadOnlyList<Vector3> polyline, float radius)
    {
        _radius = radius;
        for (var i = 0; i + 1 < polyline.Count; i++)
        {
            var a = new Vector2(polyline[i].X, polyline[i].Z);
            var b = new Vector2(polyline[i + 1].X, polyline[i + 1].Z);
            var min = Vector2.Min(a, b) - new Vector2(radius);
            var max = Vector2.Max(a, b) + new Vector2(radius);
            for (var x = Cell(min.X); x <= Cell(max.X); x++)
            {
                for (var z = Cell(min.Y); z <= Cell(max.Y); z++)
                {
                    if (!_grid.TryGetValue((x, z), out var list))
                    {
                        _grid[(x, z)] = list = [];
                    }

                    list.Add((a, b));
                }
            }
        }
    }

    private int Cell(float v) => (int)MathF.Floor(v / _radius);

    public bool Contains(Vector3 p)
    {
        if (!_grid.TryGetValue((Cell(p.X), Cell(p.Z)), out var segments))
        {
            return false;
        }

        var q = new Vector2(p.X, p.Z);
        var r2 = _radius * _radius;
        foreach (var (a, b) in segments)
        {
            var ab = b - a;
            var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            if (Vector2.DistanceSquared(q, a + t * ab) <= r2)
            {
                return true;
            }
        }

        return false;
    }
}
