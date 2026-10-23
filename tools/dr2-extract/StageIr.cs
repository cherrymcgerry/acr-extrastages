using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;

namespace Dr2Extract;

/// <summary>Per-stage result of <see cref="StageIr.Build"/>, used for the <c>--all</c> report.</summary>
internal sealed record StageSummary(
    string Id,
    string? Name,
    float LengthM,
    int Gates,
    int CollisionTriangles,
    int Surfaces,
    int ObjectInstances,
    int ObjectShapes,
    int ObjectCollisionTriangles,
    int RenderTriangles,
    int PacenoteCalls,
    int PacenoteTokens,
    int UnknownPacenoteTokens,
    RouteMapFit? RouteMap,
    float? MaxGateDy,
    double Seconds);

/// <summary>
/// <c>stage-ir &lt;stage_id&gt;</c> / <c>stage-ir --all</c>: DR2 install -> <c>work/ir/&lt;stage_id&gt;/</c> (stage.json,
/// collision.glb, objects_collision.glb, render/*.glb). See docs/stage-ir.md for the schema.
/// </summary>
internal static partial class StageIr
{
    public const int SchemaVersion = 2;

    [GeneratedRegex(@"^(?<loc>[a-z0-9_]+)_route_(?<route>\d+)$")]
    private static partial Regex StageId();

    private sealed record Options(string OutRoot, float Corridor, SurfaceMap SurfaceMap, bool Render, GroundBakeOptions? Bake);

    public static async Task<int> Run(string[] args)
    {
        var root = Program.Option(args, "--dr2") ?? Dr2Install.DefaultRoot;
        var outRoot = Program.Option(args, "--out") ?? Path.Combine("work", "ir");
        var cache = Program.Option(args, "--cache") ?? Path.Combine("work", "dr2", "cache");
        var corridorM = float.Parse(Program.Option(args, "--corridor") ?? "250", CultureInfo.InvariantCulture);
        var options = new Options(outRoot, corridorM,
            SurfaceMap.Load(Program.Option(args, "--surface-map") ?? Path.Combine("config", "surface-map.toml")),
            args.Contains("--render"),
            args.Contains("--bake-ground") ? GroundBakeOptions.Parse(args, corridorM) : null);
        var install = await Dr2Install.OpenAsync(root, cache);

        if (args.Contains("--all"))
        {
            return await RunAll(install, options, new Regex(Program.Option(args, "--filter") ?? "_rally_"));
        }

        var id = Program.Positional(args).FirstOrDefault() ?? throw new ArgumentException("stage-ir <stage_id> | --all");
        var m = StageId().Match(id);
        if (!m.Success)
        {
            throw new ArgumentException($"stage id '{id}' is not <location>_route_<n> (e.g. new_zealand_rally_01_route_5)");
        }

        var location = m.Groups["loc"].Value;
        var route = int.Parse(m.Groups["route"].Value, CultureInfo.InvariantCulture);
        var archive = install.LocationArchive(location);
        await install.OpenLocationAsync(archive);
        var stage = new StageRef(archive, Path.GetFileNameWithoutExtension(archive).Split("__")[0], location, route);
        if (!install.Exists($"{stage.RouteDir}/progress_track.xml"))
        {
            throw new FileNotFoundException($"{stage.RouteDir}/progress_track.xml not found; {location} routes: " +
                string.Join(", ", install.List($"{stage.LocDir}/route_").Select(p => p.Split('/')[4]).Distinct()));
        }

        if (options.Bake is not null && args.Contains("--bake-only"))
        {
            var progress = ProgressTrack.Load(await install.FetchAsync($"{stage.RouteDir}/progress_track.xml"));
            await TerrainBake.Run(install, stage, progress, Path.Combine(outRoot, stage.Id), options.Bake);
            return 0;
        }

        await Build(install, stage, options);
        return 0;
    }

    private static async Task<int> RunAll(Dr2Install install, Options options, Regex filter)
    {
        var results = new List<(StageRef Stage, StageSummary? Summary, string? Error)>();
        await foreach (var stage in StageRef.Enumerate(install, filter))
        {
            try
            {
                results.Add((stage, await Build(install, stage, options), null));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or KeyNotFoundException
                                           or InvalidOperationException or FormatException or IndexOutOfRangeException)
            {
                Console.WriteLine($"FAIL {stage.Id}: {ex.Message}");
                results.Add((stage, null, ex.Message));
            }
        }

        var report = Path.Combine(options.OutRoot, "report.md");
        File.WriteAllText(report, Report(results, options));
        Console.WriteLine($"{results.Count(r => r.Error is null)}/{results.Count} stages built; wrote {report}");
        return results.Any(r => r.Error is not null) ? 1 : 0;
    }

    private static string Report(List<(StageRef Stage, StageSummary? Summary, string? Error)> results, Options options)
    {
        var ok = results.Where(r => r.Summary is not null).Select(r => r.Summary!).ToList();
        var md = new StringBuilder();
        md.AppendLine("# stage-ir --all report");
        md.AppendLine();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Generated by `dr2-extract stage-ir --all` (corridor {options.Corridor} m, render {(options.Render ? "on" : "off")}). " +
            $"{ok.Count}/{results.Count} stages built, {results.Count(r => r.Error is not null)} failed."));
        md.AppendLine();
        var maps = ok.Where(s => s.RouteMap is not null).ToList();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Handedness (route map fit): {maps.Count(s => s.RouteMap!.RightHanded)}/{maps.Count} stages fit the stage-select map " +
            $"better unmirrored; median mirrored/unmirrored cost ratio {Median(maps.Select(s => s.RouteMap!.Ratio)):F1}."));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Totals: {ok.Sum(s => (long)s.CollisionTriangles)} ground triangles, {ok.Sum(s => s.ObjectInstances)} object instances " +
            $"({ok.Sum(s => s.ObjectShapes)} collision shapes), {ok.Sum(s => s.PacenoteTokens)} pacenote tokens " +
            $"({ok.Sum(s => s.UnknownPacenoteTokens)} of unknown kind)."));
        md.AppendLine();
        md.AppendLine("| stage | name | length (m) | gates | ground tris | surfaces | objects | object shapes | object tris | render tris | calls | tokens (unknown) | map fit (ratio) | max gate dy (m) | s | status |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---|");
        foreach (var (stage, s, error) in results)
        {
            md.AppendLine(s is null
                ? $"| `{stage.Id}` | | | | | | | | | | | | | | | **FAIL**: {error?.Replace("|", "\\|")} |"
                : string.Create(CultureInfo.InvariantCulture,
                    $"| `{s.Id}` | {s.Name} | {s.LengthM:F1} | {s.Gates} | {s.CollisionTriangles} | {s.Surfaces} | {s.ObjectInstances} | " +
                    $"{s.ObjectShapes} | {s.ObjectCollisionTriangles} | {s.RenderTriangles} | {s.PacenoteCalls} | " +
                    $"{s.PacenoteTokens} ({s.UnknownPacenoteTokens}) | " +
                    $"{(s.RouteMap is null ? "n/a" : $"{(s.RouteMap.RightHanded ? "RH" : "**mirrored**")} ({s.RouteMap.Ratio:F1})")} | " +
                    $"{s.MaxGateDy:F3} | {s.Seconds:F0} | ok |"));
        }

        return md.ToString();
    }

    private static float Median(IEnumerable<float> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? float.NaN : v[v.Count / 2];
    }

    private static async Task<StageSummary> Build(Dr2Install install, StageRef st, Options options)
    {
        var timer = Stopwatch.StartNew();
        var (id, location, country, route, locDir, routeDir) = (st.Id, st.Location, st.Country, st.Route, st.LocDir, st.RouteDir);
        var outDir = Path.Combine(options.OutRoot, id);
        var corridor = options.Corridor;
        var surfaceMap = options.SurfaceMap;

        Console.WriteLine($"stage {id}: archive {Path.GetFileName(st.Archive)}");
        var progress = ProgressTrack.Load(await install.FetchAsync($"{routeDir}/progress_track.xml"));
        var codriverPath = st.CodriverPath;
        var codriver = install.Exists(codriverPath) ? Codriver.Load(await install.FetchAsync(codriverPath)) : null;
        var triggers = install.Exists($"{routeDir}/triggers_game_object.xml")
            ? TriggerBox.Load(await install.FetchAsync($"{routeDir}/triggers_game_object.xml"))
            : [];
        var lng = LngCommands.Load(await install.FetchAsync("language/language_eng.lng"));
        var surfaces = SurfaceMaterials.Load(await install.FetchAsync("surface_materials.xml"));

        var start = progress.GateOf("start");
        var finish = progress.GateOf("finish");
        var line = new RouteLine(progress);
        var name = StageNames.Stage(lng, location, route);
        var locationName = StageNames.Location(lng, location, country);

        // Physics ground: crop to a corridor around the route, map surfaces (fails on unmapped codes).
        Console.WriteLine("reading physics ground...");
        var jpk = await install.FetchAsync($"{routeDir}/track.jpk", temporary: true);
        (Vector3 Min, Vector3 Max, List<GroundCell> Cells) ground;
        try
        {
            ground = Dr2Ground.ReadJpk(jpk);
        }
        finally
        {
            File.Delete(jpk);
        }

        var inCorridor = new RouteCorridor(progress.Gates.Select(g => g.Crossing).ToList(), corridor);
        var kept = ground.Cells.SelectMany(c => c.Triangles).Where(t => inCorridor.Contains(t.Centroid)).ToList();
        var mapping = surfaceMap.Resolve(kept.Select(t => t.Material));
        Directory.CreateDirectory(outDir);
        var stats = GroundGltf.Write(kept, Path.Combine(outDir, "collision.glb"));
        Console.WriteLine($"collision.glb: {stats.Triangles} triangles within {corridor} m of the route");

        var heights = progress.Gates.Select(g => (Gate: g, Y: Dr2Ground.HeightAt(ground.Cells, g.Crossing.X, g.Crossing.Z))).ToList();
        var dy = heights.Where(h => h.Y is not null).Select(h => MathF.Abs(h.Y!.Value - h.Gate.Crossing.Y)).ToList();

        var renderEntries = new List<object>();
        var renderTriangles = 0;
        if (options.Render)
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
                renderTriangles += result.Triangles;
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

        object? groundBake = null;
        if (options.Bake is not null)
        {
            try
            {
                groundBake = await TerrainBake.Run(install, st, progress, outDir, options.Bake);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // Australia/Sweden/Finland winter shaders etc. have no terrain_array.fx — still write stage.json.
                Console.Error.WriteLine($"warning: ground bake skipped: {ex.Message}");
                groundBake = new Dictionary<string, object?> { ["skipped"] = true, ["reason"] = ex.Message };
                var bakeDir = Path.Combine(outDir, "render", "ground_bake");
                Directory.CreateDirectory(bakeDir);
                File.WriteAllText(Path.Combine(bakeDir, "skipped.json"),
                    JsonSerializer.Serialize(groundBake, Json.Options));
            }
        }

        var objects = await BuildObjects(install, st, inCorridor, options, outDir, ground.Cells);
        renderTriangles += objects?.RenderTriangles ?? 0;

        // Pacenotes: distanceAlongTrack is metres from the start line (see docs/dr2-formats.md).
        var tokens = codriver?.Calls.SelectMany((c, i) => PacenoteNormalizer.Normalize(c, i)).ToList() ?? [];
        foreach (var t in tokens)
        {
            t["gate_distance"] = Round(start.Distance + (float)t["distance_m"]!, 2);
            t["position"] = V(line.At(start.Distance + (float)t["distance_m"]!));
        }

        RouteMapFit? routeMap = null;
        if (install.Exists(RouteMapCheck.MapPath(id)))
        {
            routeMap = RouteMapCheck.Fit(await install.FetchAsync(RouteMapCheck.MapPath(id)), RouteMapCheck.StagePolyline(progress),
                RouteMapCheck.MapPath(id));
        }

        object? resetLines = null;
        if (install.Exists($"{routeDir}/resetlines.cqtc"))
        {
            try
            {
                resetLines = ResetLines.ReadHeader(await install.FetchAsync($"{routeDir}/resetlines.cqtc"));
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"warning: resetlines.cqtc unreadable: {e.Message}");
                resetLines = new { error = e.Message };
            }
        }

        var stage = new Dictionary<string, object?>
        {
            ["schema"] = "ACR-XS/stage-ir",
            ["version"] = SchemaVersion,
            ["id"] = id,
            ["name"] = name,
            ["location"] = new
            {
                id = location,
                country,
                name = locationName,
                archive = $"locations/{Path.GetFileName(st.Archive)}",
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
                handedness = "right-handed (verified: stage-select route map fit, pacenote left/right vs road curvature)",
                notes = "Positions are copied unchanged from DR2 files; for travel direction f, right = normalize(cross(f, up)) " +
                        "matches the gates' 'right' points and the codriver's right calls. glTF files use the same frame " +
                        "(glTF is +Y up, right-handed).",
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
                    distance_origin = "start_line",
                    distance_reference = "distance_m = metres along the route from the start line (timing type 'start'); " +
                                         "gate_distance = the same point on the gates[].distance scale (start gate distance + distance_m)",
                    start_gate_distance = start.Distance,
                    shakedown_cutoff_m = codriver.ShakedownCutoff,
                    finish_call_distance_m = codriver.FinishCallDistance,
                    tokens,
                    raw = codriver.Calls.Select(c => new
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
            ["respawn"] = new
            {
                method = "gates",
                notes = "DR2 resets onto route_N/resetlines.cqtc, whose payload is not decoded; respawn candidates are the " +
                        "gates (crossing = road centre, left/right = road edges, heading = next crossing - previous).",
                median_gate_spacing_m = Round(Median(progress.Gates.Zip(progress.Gates.Skip(1)).Select(p => p.Second.Distance - p.First.Distance)), 2),
                resetlines = resetLines,
            },
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
            ["objects"] = objects?.Json,
            ["render"] = renderEntries,
            ["ground_bake"] = groundBake,
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
                route_map_fit = routeMap is null
                    ? null
                    : new
                    {
                        source = routeMap.Source,
                        right_handed = routeMap.RightHanded,
                        unmirrored_cost_px = routeMap.ProperCostPx,
                        unmirrored_rotation_deg = routeMap.ProperRotationDeg,
                        mirrored_cost_px = routeMap.ReflectedCostPx,
                        mirrored_rotation_deg = routeMap.ReflectedRotationDeg,
                    },
                object_shapes_vs_render = objects?.ShapeCheck,
            },
            ["sources"] = install.Sources,
        };

        File.WriteAllText(Path.Combine(outDir, "stage.json"), JsonSerializer.Serialize(stage, Json.Options));
        Console.WriteLine($"wrote {Path.Combine(outDir, "stage.json")}: \"{name}\" ({locationName}), " +
                          $"{finish.Distance - start.Distance:F1} m start->finish, {progress.Gates.Count} gates, " +
                          $"{codriver?.Calls.Count ?? 0} pacenote calls, {objects?.Instances ?? 0} objects, " +
                          $"surfaces {string.Join(" ", mapping.Select(kv => $"{kv.Key}->{kv.Value}"))}");
        if (heights.Any(h => h.Y is null) || dy.Any(d => d > 2))
        {
            Console.WriteLine($"warning: ground vs gate height check: {heights.Count(h => h.Y is null)} gates without ground, max |dy| {dy.DefaultIfEmpty().Max():F2} m");
        }

        return new StageSummary(id, name, Round(finish.Distance - start.Distance, 1), progress.Gates.Count, stats.Triangles,
            mapping.Count, objects?.Instances ?? 0, objects?.Shapes ?? 0, objects?.CollisionTriangles ?? 0, renderTriangles,
            codriver?.Calls.Count ?? 0, tokens.Count, tokens.Count(t => (string?)t["kind"] == "unknown"), routeMap,
            dy.Count == 0 ? null : Round(dy.Max(), 3), timer.Elapsed.TotalSeconds);
    }

    private sealed record ObjectsResult(object Json, object? ShapeCheck, int Instances, int Shapes, int CollisionTriangles,
        int RenderTriangles);

    private static Dictionary<string, ShapeProps>? _physParams;

    private static async Task<ObjectsResult?> BuildObjects(Dr2Install install, StageRef st, RouteCorridor inCorridor,
        Options options, string outDir, List<GroundCell> cells)
    {
        var ensPath = $"{st.RouteDir}/objects.ens";
        if (!install.Exists(ensPath))
        {
            return null;
        }

        const string physPath = "tracks/generic/physparams.xml";
        _physParams ??= StageObjects.LoadPhysParams(await install.FetchAsync(physPath));
        var all = StageObjects.LoadInstances(await install.FetchAsync(ensPath), out var cloth);
        var instances = all.Where(i => inCorridor.Contains(i.World.Translation)).ToList();

        string Resolve(string file) =>
            install.Exists($"{st.RouteDir}/{file}") ? $"{st.RouteDir}/{file}"
            : install.Exists($"{st.LocDir}/{file}") ? $"{st.LocDir}/{file}"
            : throw new FileNotFoundException($"object template library '{file}' not found in {st.RouteDir} or {st.LocDir}");

        var templates = new Dictionary<string, ObjectTemplate>(StringComparer.Ordinal);
        var templateSources = new List<string>();
        foreach (var file in instances.Select(i => i.TemplateFile).Distinct())
        {
            var path = Resolve(file);
            templateSources.Add(path);
            foreach (var (k, v) in StageObjects.LoadTemplates(await install.FetchAsync(path), path, _physParams))
            {
                templates[$"{file}#{k}"] = v;
            }
        }

        var missingTemplates = instances.Select(i => $"{i.TemplateFile}#{i.Template}").Where(k => !templates.ContainsKey(k)).Distinct().ToList();
        if (missingTemplates.Count > 0)
        {
            throw new InvalidDataException($"objects.ens references unknown templates: {string.Join(", ", missingTemplates.Take(10))}");
        }

        var used = instances.Select(i => templates[$"{i.TemplateFile}#{i.Template}"]).Distinct().ToList();
        var unmapped = used.SelectMany(t => t.Shapes.Select(s => s.Props)).Distinct()
            .Where(p => used.Any(t => t.Shapes.Any(s => s.Props == p && options.SurfaceMap.Wall(t.Id, p) is null))).Order().ToList();
        if (unmapped.Count > 0)
        {
            throw new InvalidDataException(
                $"unmapped DR2 object shape class(es) {string.Join(", ", unmapped.Select(c => $"'{c}'"))}; add them to [walls] in {options.SurfaceMap.Path}");
        }

        var unknownKinds = used.SelectMany(t => t.Shapes).Where(s => s.Kind is not ("box" or "cylinder" or "cone" or "sphere" or "vcquadtree"))
            .Select(s => s.Kind).Distinct().ToList();

        // World-space collision primitives.
        var world = new List<(Matrix4x4 World, string Wall, ObjectShape Shape)>();
        var meshShapes = new List<object>();
        foreach (var inst in instances)
        {
            var template = templates[$"{inst.TemplateFile}#{inst.Template}"];
            foreach (var shape in template.Shapes)
            {
                var wall = options.SurfaceMap.Wall(template.Id, shape.Props)!;
                if (wall == SurfaceMap.NoCollision)
                {
                    continue;
                }

                if (shape.Kind == "vcquadtree")
                {
                    meshShapes.Add(new { instance = inst.Id, template = template.Id, file = shape.MeshFile, acr_wall = wall, transform = M(shape.Local * inst.World) });
                    continue;
                }

                world.Add((shape.Local * inst.World, wall, shape));
            }
        }

        var collisionTriangles = StageObjects.WriteCollisionGlb(world, Path.Combine(outDir, "objects_collision.glb"));
        Console.WriteLine($"objects: {instances.Count}/{all.Count} instances in corridor, {used.Count} templates, " +
                          $"{world.Count} primitives -> objects_collision.glb ({collisionTriangles} triangles)");

        // Render prototypes (one glTF node per template, template space).
        object? render = null;
        object? shapeCheck = null;
        var renderTriangles = 0;
        if (options.Render)
        {
            var pssgs = new Dictionary<string, PssgFile?>(StringComparer.Ordinal);
            async Task<PssgFile?> Pssg(string file)
            {
                if (!pssgs.TryGetValue(file, out var p))
                {
                    var path = install.Exists($"{st.RouteDir}/{file}") ? $"{st.RouteDir}/{file}"
                        : install.Exists($"{st.LocDir}/{file}") ? $"{st.LocDir}/{file}" : null;
                    pssgs[file] = p = path is null ? null : PssgCommands.Open(await install.FetchAsync(path));
                }

                return p;
            }

            var prototypes = new List<(string Name, IReadOnlyList<PssgNode> Nodes)>();
            var missingRenderables = new List<string>();
            foreach (var t in used)
            {
                var nodes = new List<PssgNode>();
                foreach (var uri in t.Renderables)
                {
                    var (file, node) = (uri[..uri.IndexOf('#')], uri[(uri.IndexOf('#') + 1)..]);
                    if ((await Pssg(file))?.TryGetObject<PssgNode>(node.AsMemory()) is { } n)
                    {
                        // trees.pssg: the renderable is the rigid-body node; the mesh is its LOD sibling under the
                        // same "<name> Root".
                        var hasMesh = n.Elements<PssgVisibleRenderNode>().Any();
                        nodes.Add(!hasMesh && n.ParentElement is PssgNode root ? root : n);
                    }
                    else
                    {
                        missingRenderables.Add(uri);
                    }
                }

                if (nodes.Count > 0)
                {
                    prototypes.Add((t.Id, nodes));
                }
            }

            var glb = Path.Combine(outDir, "render", "objects.glb");
            var (result, protoStats) = PssgMeshExport.ExportPrototypes(prototypes, glb);
            File.WriteAllText(Path.ChangeExtension(glb, ".materials.json"),
                JsonSerializer.Serialize(result.Materials.Where(x => x.Triangles > 0), Json.Options));
            renderTriangles = result.Triangles;
            Console.WriteLine($"render/objects.glb: {protoStats.Count} prototypes, {result.Triangles} triangles");
            render = new
            {
                file = "render/objects.glb",
                materials = "render/objects.materials.json",
                kind = "object_prototypes",
                notes = "one glTF node per template id, in template space; place with objects.instances[].transform",
                prototypes = protoStats.Count,
                triangles = result.Triangles,
                missing_renderables = missingRenderables.Distinct().ToList(),
                bounds = protoStats.ToDictionary(kv => kv.Key, kv => Bounds(kv.Value.Min, kv.Value.Max)),
            };
            shapeCheck = ShapeConventionCheck(used, protoStats);
        }

        // Instance origins should sit on the physics ground (objects are authored with their base at the origin).
        var step = Math.Max(1, instances.Count / 2000);
        var originDy = instances.Where((_, i) => i % step == 0)
            .Select(i => (i.World.Translation.Y - Dr2Ground.HeightAt(cells, i.World.Translation.X, i.World.Translation.Z)))
            .Where(d => d is not null).Select(d => MathF.Abs(d!.Value)).Order().ToList();
        var groundCheck = new
        {
            sampled = originDy.Count,
            median_abs_dy_m = originDy.Count == 0 ? (float?)null : Round(originDy[originDy.Count / 2], 3),
            p90_abs_dy_m = originDy.Count == 0 ? (float?)null : Round(originDy[originDy.Count * 9 / 10], 3),
        };
        shapeCheck = new { origin_vs_ground = groundCheck, bounds_vs_render = shapeCheck };

        var shapeCounts = world.GroupBy(w => w.Wall).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
        var json = new
        {
            sources = new { instances = ensPath, templates = templateSources, shape_classes = physPath },
            corridor_m = options.Corridor,
            instances_total = all.Count,
            instances_in_corridor = instances.Count,
            cloth_instances_skipped = cloth,
            unknown_shape_kinds = unknownKinds,
            collision = new
            {
                file = "objects_collision.glb",
                notes = "world-space tessellated primitives (8-segment cylinders/cones/spheres), one glTF material per ACR wall material",
                triangles = collisionTriangles,
                meshes = shapeCounts.Select(kv => new
                {
                    gltf_material = kv.Key,
                    acr_physical_material = kv.Key,
                    acr_asset_path = SurfaceMap.AssetPrefix + kv.Key,
                    primitives = kv.Value,
                }).ToList(),
                mesh_shapes = meshShapes,
            },
            render,
            wall_classes = used.SelectMany(t => t.Shapes.Select(s => (t, s))).GroupBy(x => x.s.Props).OrderBy(g => g.Key).Select(g => new
            {
                shape_class = g.Key,
                dr2_material = g.First().s.Material,
                dr2_core_material = g.First().s.CoreMaterial,
                acr_wall = g.Select(x => options.SurfaceMap.Wall(x.t.Id, x.s.Props)).Distinct().ToList(),
                templates = g.Select(x => x.t.Id).Distinct().Count(),
            }).ToList(),
            templates = used.OrderBy(t => t.Id).ToDictionary(t => t.Id, t => new
            {
                source = t.Source,
                renderables = t.Renderables,
                inactive_subentities = t.InactiveSubentities,
                constraints = t.Constraints,
                bodies = t.Bodies.Select(b => new
                {
                    sub_entity = b.SubEntity,
                    @static = b.Static,
                    renderable = b.Renderable,
                    shapes = b.Shapes.Select(s => new
                    {
                        id = s.Id,
                        kind = s.Kind,
                        dimensions = s.Dimensions,
                        transform = M(s.Local),
                        shape_class = s.Props,
                        dr2_material = s.Material,
                        acr_wall = options.SurfaceMap.Wall(t.Id, s.Props),
                        breaks_on_impulse = s.BreakImpulse,
                        mesh_file = s.MeshFile,
                    }).ToList(),
                }).ToList(),
            }),
            instances = instances.Select(i => new
            {
                id = i.Id,
                template = i.Template,
                kind = i.Kind,
                tag = i.Tag,
                transform = M(i.World),
            }).ToList(),
        };

        return new ObjectsResult(json, shapeCheck, instances.Count, world.Count, collisionTriangles, renderTriangles);
    }

    /// <summary>
    /// Compares each template's collision bounds with its render prototype bounds (template space) under the chosen
    /// primitive conventions and the alternatives, as evidence for the conventions in docs/dr2-formats.md.
    /// </summary>
    private static object ShapeConventionCheck(List<ObjectTemplate> templates,
        Dictionary<string, (Vector3 Min, Vector3 Max, int Triangles)> render)
    {
        static ObjectShape Shift(ObjectShape s, float dy) => s with { Local = Matrix4x4.CreateTranslation(0, dy, 0) * s.Local };
        var variants = new Dictionary<string, Func<ObjectShape, ObjectShape>>
        {
            ["chosen"] = s => s,
            ["cone_origin_at_base"] = s => s.Kind == "cone" ? Shift(s, s.Dimensions.GetValueOrDefault("fullHeight") / 4) : s,
            ["cone_origin_at_apex"] = s => s.Kind == "cone" ? Shift(s, -s.Dimensions.GetValueOrDefault("fullHeight") * 3 / 4) : s,
            ["cylinder_origin_at_base"] = s => s.Kind == "cylinder" ? Shift(s, s.Dimensions.GetValueOrDefault("height") / 2) : s,
            ["box_width_length_swapped"] = s => s.Kind == "box"
                ? s with
                {
                    Dimensions = new Dictionary<string, float>(s.Dimensions)
                    {
                        ["width"] = s.Dimensions.GetValueOrDefault("length"),
                        ["length"] = s.Dimensions.GetValueOrDefault("width"),
                    },
                }
                : s,
        };

        var result = new Dictionary<string, object>();
        foreach (var kind in new[] { "box", "cylinder", "cone", "sphere" })
        {
            var subset = templates.Where(t => render.ContainsKey(t.Id) && t.Shapes.Any(s => s.Kind == kind)).ToList();
            result[kind] = new
            {
                templates = subset.Count,
                median_bounds_iou = subset.Count == 0 ? null : variants.ToDictionary(v => v.Key, v => Round(Median(subset.Select(t =>
                {
                    var b = StageObjects.ShapeBounds(t.Shapes.Select(v.Value));
                    return b is null ? 0 : Iou(b.Value.Min, b.Value.Max, render[t.Id].Min, render[t.Id].Max);
                })), 3)),
            };
        }

        return result;
    }

    private static float Iou(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax)
    {
        static float Vol(Vector3 d) => MathF.Max(d.X, 0) * MathF.Max(d.Y, 0) * MathF.Max(d.Z, 0);
        var inter = Vol(Vector3.Min(aMax, bMax) - Vector3.Max(aMin, bMin));
        var union = Vol(aMax - aMin) + Vol(bMax - bMin) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Decomposed transform (position, rotation quaternion xyzw, scale) plus the raw row-major matrix.</summary>
    private static object M(Matrix4x4 m)
    {
        var ok = Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation);
        return new
        {
            position = V(translation),
            rotation = ok ? new[] { Round(rotation.X, 5), Round(rotation.Y, 5), Round(rotation.Z, 5), Round(rotation.W, 5) } : null,
            scale = ok && Vector3.Distance(scale, Vector3.One) > 1e-3f ? V(scale, 4) : null,
            matrix = ok ? null : new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 },
        };
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

/// <summary><c>route_N/resetlines.cqtc</c>: chunk table and REST bounds only (payload not decoded).</summary>
internal static class ResetLines
{
    public static object ReadHeader(string path)
    {
        var d = File.ReadAllBytes(path);
        var count = BitConverter.ToInt32(d, 4);
        var chunks = new List<object>();
        object? bounds = null;
        for (var i = 0; i < count && 8 + i * 28 + 28 <= d.Length; i++)
        {
            var e = 8 + i * 28;
            var (offset, size) = (BitConverter.ToInt64(d, e), BitConverter.ToInt64(d, e + 8));
            var tag = Encoding.ASCII.GetString(d, e + 16, 4);
            chunks.Add(new { tag, offset, size });
            if (tag == "REST" && bounds is null && offset + 24 <= d.Length)
            {
                var f = Enumerable.Range(0, 6).Select(k => BitConverter.ToSingle(d, (int)offset + 4 * k)).ToArray();
                bounds = StageIr.Bounds(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]));
            }
        }

        return new { source = Path.GetFileName(path), bytes = d.Length, header_word = BitConverter.ToInt32(d, 0), chunks, rest_bounds = bounds };
    }
}
