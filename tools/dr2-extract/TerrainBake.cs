using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Dr2Extract;

/// <summary>Options of <c>stage-ir --bake-ground</c>; see docs/stage-ir.md (ground bake).</summary>
internal sealed record GroundBakeOptions(
    double HighMPerPx,
    double LowMPerPx,
    float HighCorridor,
    float Corridor,
    int TilePx,
    double BudgetMb,
    bool ExcludeRoad,
    bool Decals,
    string UvMode)
{
    public static GroundBakeOptions Parse(string[] args, float corridor) => new(
        double.Parse(Program.Option(args, "--bake-high-res") ?? "0.05", CultureInfo.InvariantCulture),
        double.Parse(Program.Option(args, "--bake-low-res") ?? "0.5", CultureInfo.InvariantCulture),
        float.Parse(Program.Option(args, "--bake-high-corridor") ?? "200", CultureInfo.InvariantCulture),
        corridor,
        int.Parse(Program.Option(args, "--bake-tile") ?? "2048", CultureInfo.InvariantCulture),
        double.Parse(Program.Option(args, "--bake-budget-mb") ?? "150", CultureInfo.InvariantCulture),
        args.Contains("--bake-exclude-road"),
        !args.Contains("--bake-no-decals"),
        Program.Option(args, "--bake-uv") ?? "A");
}

/// <summary>
/// Pre-painted ground: DR2's <c>terrain_array.fx</c> shading (three texture-array layers per triangle, blended by
/// per-vertex 0/1 weights, times the section colour map and lightmap) plus the road decals, rasterised top-down into
/// square tiles in IR coordinates (UV = (x - min_x) / size, (z - min_z) / size; row 0 = min_z). Writes BC1 DDS with
/// mips, PNG previews, road masks and manifest.json to <c>render/ground_bake/</c>. Decode notes: docs/dr2-formats.md.
/// </summary>
internal static class TerrainBake
{
    private const float BaseUvScale = 0.125f;

    private sealed class LayerParams
    {
        public required string Id { get; init; }
        public required float[] DifUv { get; init; }
        public required float[] Scale { get; init; }
        public required float[] Thickness { get; init; }
        public required string Diffuse { get; init; }
        public string? ColourMap { get; init; }
        public string? Lightmap { get; init; }
    }

    private struct TerrainTri
    {
        public int Shader;
        public byte La, Lb, Lc;
        public Vector3 P0, P1, P2;
        public Vector2 A0, A1, A2; // ST0.xy: section atlas UV (colour map, lightmap)
        public Vector2 R0, R1, R2; // ST0.zw
        public Vector2 M0, M1, M2; // ST1.xy: metric mesh UV (1/8 m)
        public Vector2 W0, W1, W2; // ST1.zw: weights of layers b and c
        public float AtlasPerMetre;
    }

    private struct DecalTri
    {
        public int Texture;
        public int Lightmap;
        public Vector3 P0, P1, P2;
        public Vector2 U0, U1, U2;
        public Vector2 A0, A1, A2;
        public float C0, C1, C2;
        public float UvPerMetre;
    }

    private sealed record Tile(string Name, string Tier, double MinX, double MinZ, double Size, int Px, double CoreMinX,
        double CoreMinZ, double CoreSize);

    public static async Task<object> Run(Dr2Install install, StageRef st, ProgressTrack progress, string outDir,
        GroundBakeOptions o)
    {
        var timer = Stopwatch.StartNew();
        var dir = Path.Combine(outDir, "render", "ground_bake");
        Directory.CreateDirectory(dir);
        if (Program.Option(Environment.GetCommandLineArgs(), "--bake-probe") is null)
        {
            foreach (var stale in Directory.GetFiles(dir))
            {
                File.Delete(stale);
            }
        }

        Console.WriteLine("ground bake: reading tracksplit.pssg...");
        var pssgPath = $"{st.LocDir}/tracksplit.pssg";
        var pssg = PssgCommands.Open(await install.FetchAsync(pssgPath));
        var textures = new TextureCache(pssg, install, st);
        var corridor = new RouteCorridor(progress.Gates.Select(g => g.Crossing).ToList(), o.Corridor);

        var shaders = new List<LayerParams>();
        var shaderIndex = new Dictionary<PssgShaderInstance, int>();
        var tris = new List<TerrainTri>();
        var decals = new List<DecalTri>();
        var decalTextures = new List<string>();
        var lightmaps = new List<string>();
        var skipped = new Dictionary<string, int>();
        var library = pssg.Elements<PssgLibrary>().First(x => x.Type == "NODE");
        foreach (var root in library.ChildElements.OfType<PssgNode>())
        {
            Walk(root, Matrix4x4.Identity);
        }

        void Walk(PssgNode node, Matrix4x4 parent)
        {
            var world = node.ChildElements.OfType<PssgTransform>().FirstOrDefault()?.Transform * parent ?? parent;
            if (node is PssgVisibleRenderNode visible && !node.Id.Contains("BATCH_", StringComparison.Ordinal))
            {
                var terrain = node.Id.Contains("SHADOWCASTING_", StringComparison.Ordinal);
                var decal = o.Decals && node.Id.Contains("DECAL_", StringComparison.Ordinal);
                if (terrain || decal)
                {
                    foreach (var stream in visible.RenderInstances.OfType<PssgRenderStreamInstance>())
                    {
                        var shader = stream.TryGetShaderInstance();
                        var group = shader?.ShaderGroup.TrimStart('#');
                        if (shader is null || (terrain && group != "terrain_array.fx") || (decal && group != "decal_ao.fx"))
                        {
                            var k = $"{(terrain ? "SHADOWCASTING" : "DECAL")} {group}";
                            skipped[k] = skipped.GetValueOrDefault(k) + 1;
                            continue;
                        }

                        var rds = new RdsReader(stream.GetRenderDataSource());
                        if (terrain)
                        {
                            AddTerrain(rds, Shader(shader), world);
                        }
                        else
                        {
                            AddDecal(rds, shader, world);
                        }
                    }
                }
            }

            foreach (var child in node.ChildElements.OfType<PssgNode>())
            {
                Walk(child, world);
            }
        }

        int Shader(PssgShaderInstance shader)
        {
            if (shaderIndex.TryGetValue(shader, out var i))
            {
                return i;
            }

            var defs = shader.TryGetShaderGroup()!.InputDefinitions.ToList();
            var constants = new Dictionary<string, float>();
            var tex = new Dictionary<string, string>();
            foreach (var input in shader.Inputs)
            {
                var name = defs[(int)input.ParameterId].InputName;
                if (input.Type == "texture")
                {
                    tex[name] = input.Texture;
                }
                else if (input.Value.Length >= 4)
                {
                    constants[name] = BinaryPrimitives.ReadSingleBigEndian(input.Value);
                }
            }

            float[] Array25(string prefix, float fallback) =>
                Enumerable.Range(1, 25).Select(n => constants.GetValueOrDefault($"{prefix}{n}", fallback)).ToArray();
            shaders.Add(new LayerParams
            {
                Id = shader.Id,
                DifUv = Array25("DifUV", 1),
                Scale = Array25("WorldUVScale", BaseUvScale),
                Thickness = Array25("Thickness", 1),
                Diffuse = tex.GetValueOrDefault("TDiffuse") ?? throw new InvalidDataException($"{shader.Id}: no TDiffuse"),
                ColourMap = tex.GetValueOrDefault("TColourMap"),
                Lightmap = tex.GetValueOrDefault("TAmbientOcclusion"),
            });
            shaderIndex[shader] = shaders.Count - 1;
            return shaders.Count - 1;
        }

        void AddTerrain(RdsReader rds, int shader, Matrix4x4 world)
        {
            foreach (var (a, b, c) in rds.Triangles())
            {
                var t = new TerrainTri { Shader = shader };
                t.P0 = Vector3.Transform(rds.Position(a), world);
                t.P1 = Vector3.Transform(rds.Position(b), world);
                t.P2 = Vector3.Transform(rds.Position(c), world);
                if (!corridor.Contains((t.P0 + t.P1 + t.P2) / 3))
                {
                    continue;
                }

                // docs/dr2-formats.md: Color G,B,A bytes are slice×8+3 for layers a,b,c (R unused here).
                var col = rds.Raw("Color", 0, a);
                static byte Layer(float channel) => (byte)Math.Max(0, ((int)channel - 3) / 8);
                (t.La, t.Lb, t.Lc) = (Layer(col.Y), Layer(col.Z), Layer(col.W));
                Fill(rds, a, out t.A0, out t.R0, out t.M0, out t.W0);
                Fill(rds, b, out t.A1, out t.R1, out t.M1, out t.W1);
                Fill(rds, c, out t.A2, out t.R2, out t.M2, out t.W2);
                var area = Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0).Length() / 2;
                var uvArea = MathF.Abs(Cross(t.A1 - t.A0, t.A2 - t.A0)) / 2;
                t.AtlasPerMetre = area > 1e-6f ? MathF.Sqrt(uvArea / area) : 0;
                tris.Add(t);
            }
        }

        void AddDecal(RdsReader rds, PssgShaderInstance shader, Matrix4x4 world)
        {
            var defs = shader.TryGetShaderGroup()!.InputDefinitions.ToList();
            var tex = shader.Inputs.Where(i => i.Type == "texture").ToDictionary(i => defs[(int)i.ParameterId].InputName, i => i.Texture);
            if (!tex.TryGetValue("TDiffuseAlphaMap", out var diffuse))
            {
                return;
            }

            var ti = decalTextures.IndexOf(diffuse);
            if (ti < 0)
            {
                decalTextures.Add(diffuse);
                ti = decalTextures.Count - 1;
            }

            var li = -1;
            if (tex.TryGetValue("TAmbientOcclusionMap", out var lm))
            {
                li = lightmaps.IndexOf(lm);
                if (li < 0)
                {
                    lightmaps.Add(lm);
                    li = lightmaps.Count - 1;
                }
            }

            foreach (var (a, b, c) in rds.Triangles())
            {
                var d = new DecalTri { Texture = ti, Lightmap = li };
                d.P0 = Vector3.Transform(rds.Position(a), world);
                d.P1 = Vector3.Transform(rds.Position(b), world);
                d.P2 = Vector3.Transform(rds.Position(c), world);
                if (!corridor.Contains((d.P0 + d.P1 + d.P2) / 3))
                {
                    continue;
                }

                (d.U0, d.U1, d.U2) = (rds.Uv(a, 0), rds.Uv(b, 0), rds.Uv(c, 0));
                (d.A0, d.A1, d.A2) = (rds.Uv(a, 1), rds.Uv(b, 1), rds.Uv(c, 1));
                (d.C0, d.C1, d.C2) = (rds.Color(a).W, rds.Color(b).W, rds.Color(c).W);
                var area = Vector3.Cross(d.P1 - d.P0, d.P2 - d.P0).Length() / 2;
                d.UvPerMetre = area > 1e-6f ? MathF.Sqrt(MathF.Abs(Cross(d.U1 - d.U0, d.U2 - d.U0)) / 2 / area) : 0;
                decals.Add(d);
            }
        }

        Console.WriteLine($"ground bake: {tris.Count} terrain triangles ({shaders.Count} terrain_array shaders), " +
                          $"{decals.Count} decal triangles within {o.Corridor} m; skipped instances: " +
                          (skipped.Count == 0 ? "none" : string.Join(", ", skipped.Select(s => $"{s.Key} x{s.Value}"))));
        if (tris.Count == 0)
        {
            throw new InvalidDataException("ground bake: no terrain_array triangles in the corridor");
        }

        // Textures: every terrain_array shader of this location shares one diffuse array (checked here).
        var arrays = shaders.Select(s => s.Diffuse).Distinct().ToList();
        if (arrays.Count != 1)
        {
            throw new NotSupportedException($"ground bake: {arrays.Count} different TDiffuse arrays ({string.Join(", ", arrays)})");
        }

        var slices = textures.Array(arrays[0]);
        var colourMaps = shaders.Select(s => s.ColourMap is null ? null : textures.Get(s.ColourMap)).ToArray();
        var shaderLightmaps = shaders.Select(s => s.Lightmap is null ? null : textures.Get(s.Lightmap)).ToArray();
        var decalTex = decalTextures.Select(textures.Get).ToArray();
        var decalLm = lightmaps.Select(textures.Get).ToArray();
        var maxLayer = tris.Max(t => Math.Max(t.La, Math.Max(t.Lb, t.Lc)));
        if (maxLayer >= slices.Length)
        {
            // Rare out-of-range Color encodings (seen on poland_rally_01); clamp so bake can proceed.
            var clamped = 0;
            var last = (byte)(slices.Length - 1);
            for (var i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                if (t.La > last) { t.La = last; clamped++; }
                if (t.Lb > last) { t.Lb = last; clamped++; }
                if (t.Lc > last) { t.Lc = last; clamped++; }
                tris[i] = t;
            }

            Console.WriteLine($"ground bake: warning: layer index {maxLayer} >= {slices.Length} slices; clamped {clamped} vertex refs");
        }

        // Road layers: slices whose area lies mostly on the road band (within the gates' left/right edges + 1 m).
        var band = new RoadBand(progress, 1f);
        var sliceArea = new double[slices.Length];
        var sliceRoad = new double[slices.Length];
        foreach (var t in tris)
        {
            var area = Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0).Length() / 2;
            var onRoad = band.Contains((t.P0 + t.P1 + t.P2) / 3);
            foreach (var l in Visible(t))
            {
                sliceArea[l] += area;
                if (onRoad)
                {
                    sliceRoad[l] += area;
                }
            }
        }

        var roadLayers = Enumerable.Range(0, slices.Length).Where(l => sliceArea[l] > 0 && sliceRoad[l] / sliceArea[l] > 0.6).ToHashSet();
        var fallbackLayer = Enumerable.Range(0, slices.Length).Where(l => !roadLayers.Contains(l)).MaxBy(l => sliceArea[l]);
        Console.WriteLine("ground bake: layer use (m², share on the road band): " + string.Join(" ",
            Enumerable.Range(0, slices.Length).Where(l => sliceArea[l] > 0)
                .Select(l => $"{l}:{sliceArea[l]:F0}/{sliceRoad[l] / sliceArea[l]:P0}{(roadLayers.Contains(l) ? "*" : "")}")));

        // Tiles.
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var t in tris)
        {
            foreach (var p in new[] { t.P0, t.P1, t.P2 })
            {
                min = Vector2.Min(min, new Vector2(p.X, p.Z));
                max = Vector2.Max(max, new Vector2(p.X, p.Z));
            }
        }

        var extent = tris.Select(t =>
        {
            var c = (t.P0 + t.P1 + t.P2) / 3;
            return new[] { t.P0, t.P1, t.P2 }.Max(p => MathF.Max(MathF.Abs(p.X - c.X), MathF.Abs(p.Z - c.Z)));
        }).Order().ToList();
        var reach = extent[(int)(0.999 * (extent.Count - 1))];
        var triGrid = new TriGrid(tris.Select(t => (Vector2.Min(Xz(t.P0), Vector2.Min(Xz(t.P1), Xz(t.P2))),
            Vector2.Max(Xz(t.P0), Vector2.Max(Xz(t.P1), Xz(t.P2))))).ToList(), 32);
        var decalGrid = new TriGrid(decals.Select(t => (Vector2.Min(Xz(t.P0), Vector2.Min(Xz(t.P1), Xz(t.P2))),
            Vector2.Max(Xz(t.P0), Vector2.Max(Xz(t.P1), Xz(t.P2))))).ToList(), 32);
        var centre = progress.Gates.Select(g => g.Crossing).ToList();

        List<Tile> Layout(string tier, double res, Func<double, double, double, bool> keep)
        {
            var margin = Math.Clamp((int)Math.Ceiling(reach / res) + 2, 8, o.TilePx / 8);
            var core = (o.TilePx - 2 * margin) * res;
            var list = new List<Tile>();
            var nx = (int)Math.Ceiling((max.X - min.X) / core);
            var nz = (int)Math.Ceiling((max.Y - min.Y) / core);
            for (var j = 0; j < nz; j++)
            {
                for (var i = 0; i < nx; i++)
                {
                    var cx = min.X + i * core;
                    var cz = min.Y + j * core;
                    if (!keep(cx, cz, core) || !triGrid.Any(new Vector2((float)cx, (float)cz), new Vector2((float)(cx + core), (float)(cz + core))))
                    {
                        continue;
                    }

                    list.Add(new Tile($"{tier}_{i:D2}_{j:D2}", tier, cx - margin * res, cz - margin * res, o.TilePx * res,
                        o.TilePx, cx, cz, core));
                }
            }

            return list;
        }

        bool NearRoute(double x, double z, double size)
        {
            var r = o.HighCorridor;
            for (var i = 0; i + 1 < centre.Count; i++)
            {
                var a = centre[i];
                var b = centre[i + 1];
                var steps = Math.Max(1, (int)(Vector3.Distance(a, b) / 10));
                for (var s = 0; s <= steps; s++)
                {
                    var p = Vector3.Lerp(a, b, s / (float)steps);
                    var dx = Math.Max(Math.Max(x - p.X, 0), p.X - (x + size));
                    var dz = Math.Max(Math.Max(z - p.Z, 0), p.Z - (z + size));
                    if (dx * dx + dz * dz <= r * r)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var tileBytes = Bc1Bytes(o.TilePx);
        var low = Layout("low", o.LowMPerPx, (_, _, _) => true);
        var highRes = o.HighMPerPx;
        List<Tile> high;
        while (true)
        {
            high = Layout("high", highRes, NearRoute);
            var mb = (high.Count + low.Count) * tileBytes / 1e6;
            Console.WriteLine($"ground bake: high tier at {highRes * 100:F2} cm/px: {high.Count} tiles + {low.Count} low tiles = {mb:F0} MB");
            if (mb <= o.BudgetMb || highRes >= o.LowMPerPx / 2)
            {
                break;
            }

            highRes = Math.Round(highRes * 1.04, 4);
        }

        // Bake.
        if (Program.Option(Environment.GetCommandLineArgs(), "--bake-probe") is { } probe)
        {
            // diagnostic: one square around the start gate (or "x,z,size_m"), written as probe_<uv mode>.png
            var p = probe.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var g = progress.GateOf("start").Crossing;
            var (px, pz, size) = p.Length == 3 ? (p[0], p[1], p[2]) : (g.X, g.Z, p.Length == 1 ? p[0] : 80);
            var probeCtx = new Shading(tris, decals, shaders, slices, colourMaps, shaderLightmaps, decalTex, decalLm,
                roadLayers, fallbackLayer, o);
            var n = (int)(size / 0.05);
            var (img, mask) = probeCtx.BakeTile(px - size / 2, pz - size / 2, 0.05, n, triGrid, decalGrid);
            var file = Path.Combine(dir, $"probe_{o.UvMode}{(o.Decals ? "" : "_nodecals")}{(o.ExcludeRoad ? "_noroad" : "")}.png");
            img.SaveAsPng(file);
            img.Dispose();
            mask.Dispose();
            Console.WriteLine($"wrote {file}");
            return new { probe = file };
        }

        var ctx = new Shading(tris, decals, shaders, slices, colourMaps, shaderLightmaps, decalTex, decalLm, roadLayers,
            fallbackLayer, o);
        var manifestTiles = new List<object>();
        var lowImages = new Dictionary<Tile, Image<Rgb24>>();
        var startGate = progress.GateOf("start").Crossing;
        var closeupHalf = 40.0;
        Image<Rgb24>? closeup = null;
        foreach (var tile in high.Concat(low))
        {
            var (image, road) = ctx.BakeTile(tile.MinX, tile.MinZ, tile.Size / tile.Px, tile.Px, triGrid, decalGrid);
            var dds = $"{tile.Name}.dds";
            var png = $"{tile.Name}.png";
            var mask = $"{tile.Name}_road.png";
            WriteBc1(image, Path.Combine(dir, dds));
            using (var preview = image.Clone(x => x.Resize(256, 256)))
            {
                preview.SaveAsPng(Path.Combine(dir, png));
            }

            road.SaveAsPng(Path.Combine(dir, mask));
            road.Dispose();
            if (tile.Tier == "high" && closeup is null && startGate.X >= tile.CoreMinX && startGate.X < tile.CoreMinX + tile.CoreSize &&
                startGate.Z >= tile.CoreMinZ && startGate.Z < tile.CoreMinZ + tile.CoreSize)
            {
                var res = tile.Size / tile.Px;
                var n = Math.Min(tile.Px, (int)(2 * closeupHalf / res));
                var x0 = Math.Clamp((int)((startGate.X - closeupHalf - tile.MinX) / res), 0, tile.Px - n);
                var z0 = Math.Clamp((int)((startGate.Z - closeupHalf - tile.MinZ) / res), 0, tile.Px - n);
                closeup = image.Clone(x => x.Crop(new Rectangle(x0, z0, n, n)));
            }

            if (tile.Tier == "low")
            {
                lowImages[tile] = image;
            }
            else
            {
                image.Dispose();
            }

            manifestTiles.Add(new
            {
                name = tile.Name,
                tier = tile.Tier,
                dds,
                preview = png,
                road_mask = mask,
                px = tile.Px,
                m_per_px = Math.Round(tile.Size / tile.Px, 5),
                size_m = Math.Round(tile.Size, 4),
                bounds = new { min_x = Math.Round(tile.MinX, 4), min_z = Math.Round(tile.MinZ, 4), max_x = Math.Round(tile.MinX + tile.Size, 4), max_z = Math.Round(tile.MinZ + tile.Size, 4) },
                assign_bounds = new
                {
                    min_x = Math.Round(tile.CoreMinX, 4), min_z = Math.Round(tile.CoreMinZ, 4),
                    max_x = Math.Round(tile.CoreMinX + tile.CoreSize, 4), max_z = Math.Round(tile.CoreMinZ + tile.CoreSize, 4),
                },
            });
            Console.WriteLine($"  {tile.Name}: {tile.Size / tile.Px * 100:F1} cm/px at ({tile.MinX:F0}, {tile.MinZ:F0}), {timer.Elapsed.TotalSeconds:F0} s");
        }

        // Triangle assignment check (what the ACR side will do): centroid in a high tile's core, else a low tile's.
        int assigned = 0, inside = 0, unassigned = 0;
        foreach (var t in tris)
        {
            var c = (t.P0 + t.P1 + t.P2) / 3;
            var tile = high.FirstOrDefault(x => InCore(x, c)) ?? low.FirstOrDefault(x => InCore(x, c));
            if (tile is null)
            {
                unassigned++;
                continue;
            }

            assigned++;
            if (new[] { t.P0, t.P1, t.P2 }.All(p => p.X >= tile.MinX && p.Z >= tile.MinZ && p.X <= tile.MinX + tile.Size && p.Z <= tile.MinZ + tile.Size))
            {
                inside++;
            }
        }

        // Previews: overview from the low tier, the same area from DR2's own low LOD, and a close-up at the start.
        var overviewRes = Math.Max(o.LowMPerPx, Math.Max(max.X - min.X, max.Y - min.Y) / 4096.0);
        using (var overview = Mosaic(lowImages, min, max, overviewRes))
        {
            overview.SaveAsPng(Path.Combine(dir, "overview.png"));
        }

        foreach (var img in lowImages.Values)
        {
            img.Dispose();
        }

        var lowLod = LowLodReference(pssg, textures, corridor, min, max, overviewRes);
        lowLod?.SaveAsPng(Path.Combine(dir, "overview_dr2_lowlod.png"));
        lowLod?.Dispose();
        closeup?.SaveAsPng(Path.Combine(dir, "closeup_start.png"));
        closeup?.Dispose();

        var totalBytes = Directory.GetFiles(dir, "*.dds").Sum(f => new FileInfo(f).Length);
        var manifest = new
        {
            schema = "ground_bake/1",
            stage = st.Id,
            source = pssgPath,
            coordinates = "IR (DR2 world) metres. Texture of a tile spans bounds; u = (x - bounds.min_x) / size_m, " +
                          "v = (z - bounds.min_z) / size_m, v = 0 is the first (top) row of the DDS. Assign each terrain " +
                          "triangle to the high tile whose assign_bounds contain its centroid, else to such a low tile; " +
                          "bounds extend assign_bounds by a margin so neighbouring triangles stay inside the texture.",
            format = "BC1 (DXT1) DDS, sRGB colour, full mip chain",
            formula = "colour = layered_height_blend(layer a, b, c) * 2 * colour_map * 2 * lightmap, then decals (alpha) * 2 * lightmap; see docs/dr2-formats.md",
            uv_mode = o.UvMode,
            road = new
            {
                excluded = o.ExcludeRoad,
                layers = roadLayers.Order(),
                mask = "per tile *_road.png (8-bit, tile resolution / 4): share of road layers in the blend",
            },
            decals = o.Decals,
            tiers = new object[]
            {
                new { tier = "high", m_per_px = highRes, tiles = high.Count, corridor_m = o.HighCorridor },
                new { tier = "low", m_per_px = o.LowMPerPx, tiles = low.Count, corridor_m = o.Corridor },
            },
            tile_px = o.TilePx,
            dds_bytes = totalBytes,
            triangles = new { total = tris.Count, assigned, fully_inside_tile = inside, unassigned },
            previews = new[] { "overview.png", "overview_dr2_lowlod.png", "closeup_start.png" },
            tiles = manifestTiles,
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, Json.Options));
        Console.WriteLine($"ground bake: {high.Count} high ({highRes * 100:F1} cm/px) + {low.Count} low ({o.LowMPerPx * 100:F0} cm/px) tiles, " +
                          $"{totalBytes / 1e6:F1} MB DDS, {inside}/{assigned} triangles fully inside their tile, {unassigned} unassigned, " +
                          $"{timer.Elapsed.TotalSeconds:F0} s -> {dir}");
        return new
        {
            manifest = "render/ground_bake/manifest.json",
            high_tiles = high.Count,
            low_tiles = low.Count,
            high_m_per_px = highRes,
            low_m_per_px = o.LowMPerPx,
            dds_bytes = totalBytes,
        };
    }

    private static bool InCore(Tile t, Vector3 c) =>
        c.X >= t.CoreMinX && c.Z >= t.CoreMinZ && c.X < t.CoreMinX + t.CoreSize && c.Z < t.CoreMinZ + t.CoreSize;

    private static IEnumerable<int> Visible(TerrainTri t)
    {
        if (t.W0.X + t.W1.X + t.W2.X > 0)
        {
            yield return t.La;
        }

        if (t.W0.Y + t.W1.Y + t.W2.Y > 0)
        {
            yield return t.Lb;
        }

        if (new[] { t.W0, t.W1, t.W2 }.Any(w => w.X + w.Y < 1))
        {
            yield return t.Lc;
        }
    }

    /// <summary>terrain_array vertex: ST0 = (atlas u, v, mesh u', v'), ST1 = (mesh u, v, weight b, weight c).</summary>
    private static void Fill(RdsReader rds, uint v, out Vector2 atlas, out Vector2 other, out Vector2 mesh, out Vector2 weights)
    {
        var st0 = rds.Raw("ST", 0, v);
        var st1 = rds.Raw("ST", 1, v);
        atlas = new Vector2(st0.X, st0.Y);
        other = new Vector2(st0.Z, st0.W);
        mesh = new Vector2(st1.X, st1.Y);
        weights = new Vector2(st1.Z, st1.W);
    }

    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static long Bc1Bytes(int px)
    {
        long total = 128;
        for (var s = px; ; s /= 2)
        {
            var blocks = Math.Max(1, (s + 3) / 4);
            total += (long)blocks * blocks * 8;
            if (s == 1)
            {
                return total;
            }
        }
    }

    private static void WriteBc1(Image<Rgb24> image, string path)
    {
        var rgba = new byte[image.Width * image.Height * 4];
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var i = (y * image.Width + x) * 4;
                    (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (row[x].R, row[x].G, row[x].B, 255);
                }
            }
        });
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.Format = CompressionFormat.Bc1;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.Options.IsParallel = true;
        using var fs = File.Create(path);
        encoder.EncodeToStream(rgba, image.Width, image.Height, PixelFormat.Rgba32, fs);
    }

    private static Image<Rgb24> Mosaic(Dictionary<Tile, Image<Rgb24>> tiles, Vector2 min, Vector2 max, double res)
    {
        var w = (int)Math.Ceiling((max.X - min.X) / res);
        var h = (int)Math.Ceiling((max.Y - min.Y) / res);
        var outImage = new Image<Rgb24>(w, h, new Rgb24(0, 0, 0));
        foreach (var (tile, image) in tiles)
        {
            var scale = tile.Size / tile.Px / res;
            var core = (int)Math.Round(tile.CoreSize / res);
            using var resized = image.Clone(x => x.Resize((int)Math.Round(tile.Px * scale), (int)Math.Round(tile.Px * scale)));
            var offset = (int)Math.Round((tile.CoreMinX - tile.MinX) / res);
            using var coreImage = resized.Clone(x => x.Crop(new Rectangle(offset, offset, Math.Min(core, resized.Width - offset), Math.Min(core, resized.Height - offset))));
            outImage.Mutate(x => x.DrawImage(coreImage, new Point((int)Math.Round((tile.CoreMinX - min.X) / res), (int)Math.Round((tile.CoreMinZ - min.Y) / res)), 1f));
        }

        return outImage;
    }

    /// <summary>DR2's own low-LOD terrain (LOW_ nodes, terrain_lod.fx diffuse) rasterised top-down, for comparison.</summary>
    private static Image<Rgb24>? LowLodReference(PssgFile pssg, TextureCache textures, RouteCorridor corridor, Vector2 min,
        Vector2 max, double res)
    {
        var w = (int)Math.Ceiling((max.X - min.X) / res);
        var h = (int)Math.Ceiling((max.Y - min.Y) / res);
        var depth = new float[w * h];
        Array.Fill(depth, float.MinValue);
        var colour = new Rgb24[w * h];
        var library = pssg.Elements<PssgLibrary>().First(x => x.Type == "NODE");
        var any = false;
        foreach (var root in library.ChildElements.OfType<PssgNode>())
        {
            Walk(root, Matrix4x4.Identity);
        }

        void Walk(PssgNode node, Matrix4x4 parent)
        {
            var world = node.ChildElements.OfType<PssgTransform>().FirstOrDefault()?.Transform * parent ?? parent;
            if (node is PssgVisibleRenderNode visible && node.Id.StartsWith("LOW_", StringComparison.Ordinal))
            {
                foreach (var stream in visible.RenderInstances.OfType<PssgRenderStreamInstance>())
                {
                    var shader = stream.TryGetShaderInstance();
                    if (shader?.ShaderGroup.TrimStart('#') != "terrain_lod.fx")
                    {
                        continue;
                    }

                    var defs = shader.TryGetShaderGroup()!.InputDefinitions.ToList();
                    var texName = shader.Inputs.Where(i => i.Type == "texture")
                        .FirstOrDefault(i => defs[(int)i.ParameterId].InputName == "TDiffuseAlphaMap")?.Texture;
                    var tex = texName is null ? null : textures.Get(texName);
                    if (tex is null)
                    {
                        continue;
                    }

                    var rds = new RdsReader(stream.GetRenderDataSource());
                    foreach (var (a, b, c) in rds.Triangles())
                    {
                        var p0 = Vector3.Transform(rds.Position(a), world);
                        var p1 = Vector3.Transform(rds.Position(b), world);
                        var p2 = Vector3.Transform(rds.Position(c), world);
                        if (!corridor.Contains((p0 + p1 + p2) / 3))
                        {
                            continue;
                        }

                        var (u0, u1, u2) = (rds.Uv(a, 0), rds.Uv(b, 0), rds.Uv(c, 0));
                        any = true;
                        Raster.Triangle(p0, p1, p2, min.X, min.Y, res, w, h, (i, j, b0, b1, b2, y) =>
                        {
                            var k = j * w + i;
                            if (y <= depth[k])
                            {
                                return;
                            }

                            depth[k] = y;
                            var uv = u0 * b0 + u1 * b1 + u2 * b2;
                            var s = tex.Sample(uv, 0, false);
                            colour[k] = Srgb(new Vector3(s.X, s.Y, s.Z));
                        });
                    }
                }
            }

            foreach (var child in node.ChildElements.OfType<PssgNode>())
            {
                Walk(child, world);
            }
        }

        return any ? Image.LoadPixelData<Rgb24>(colour, w, h) : null;
    }

    internal static Rgb24 Srgb(Vector3 linear) => new(Encode(linear.X), Encode(linear.Y), Encode(linear.Z));

    private static byte Encode(float v)
    {
        v = Math.Clamp(v, 0, 1);
        var s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255), 0, 255);
    }

    /// <summary>Per-tile rasterisation and shading.</summary>
    private sealed class Shading(
        List<TerrainTri> tris,
        List<DecalTri> decals,
        List<LayerParams> shaders,
        BakeTextureImage[] slices,
        BakeTextureImage?[] colourMaps,
        BakeTextureImage?[] lightmaps,
        BakeTextureImage?[] decalTextures,
        BakeTextureImage?[] decalLightmaps,
        HashSet<int> roadLayers,
        int fallbackLayer,
        GroundBakeOptions o)
    {
        private readonly float[] _meanHeight = slices.Select(s => s.MeanAlpha).ToArray();

        public (Image<Rgb24> Colour, Image<L8> Road) BakeTile(double minX, double minZ, double res, int n, TriGrid grid, TriGrid decalGrid)
        {
            var ids = new int[n * n];
            var b1 = new float[n * n];
            var b2 = new float[n * n];
            var depth = new float[n * n];
            Array.Fill(ids, -1);
            Array.Fill(depth, float.MinValue);
            var lo = new Vector2((float)minX, (float)minZ);
            var hi = new Vector2((float)(minX + n * res), (float)(minZ + n * res));
            foreach (var id in grid.Query(lo, hi))
            {
                var t = tris[id];
                Raster.Triangle(t.P0, t.P1, t.P2, minX, minZ, res, n, n, (i, j, w0, w1, w2, y) =>
                {
                    var k = j * n + i;
                    if (y > depth[k])
                    {
                        (depth[k], ids[k], b1[k], b2[k]) = (y, id, w1, w2);
                    }
                });
            }

            var dIds = new int[n * n];
            var d1 = new float[n * n];
            var d2 = new float[n * n];
            Array.Fill(dIds, -1);
            var dDepth = new float[n * n];
            Array.Fill(dDepth, float.MinValue);
            foreach (var id in decalGrid.Query(lo, hi))
            {
                var t = decals[id];
                Raster.Triangle(t.P0, t.P1, t.P2, minX, minZ, res, n, n, (i, j, w0, w1, w2, y) =>
                {
                    var k = j * n + i;
                    // a decal lies on the terrain: skip it where the visible terrain is well above it (bridges)
                    if (y > dDepth[k] && (ids[k] < 0 || y > depth[k] - 0.5f))
                    {
                        (dDepth[k], dIds[k], d1[k], d2[k]) = (y, id, w1, w2);
                    }
                });
            }

            var colour = new Rgb24[n * n];
            var road = new byte[n / 4 * (n / 4)];
            var roadAcc = new float[n * n];
            Parallel.For(0, n, j =>
            {
                for (var i = 0; i < n; i++)
                {
                    var k = j * n + i;
                    var id = ids[k];
                    if (id < 0)
                    {
                        colour[k] = new Rgb24(0, 0, 0);
                        continue;
                    }

                    var x = (float)(minX + (i + 0.5) * res);
                    var z = (float)(minZ + (j + 0.5) * res);
                    var (c, roadShare) = ShadeTerrain(tris[id], 1 - b1[k] - b2[k], b1[k], b2[k], x, z, (float)res);
                    if (dIds[k] >= 0)
                    {
                        c = ShadeDecal(decals[dIds[k]], 1 - d1[k] - d2[k], d1[k], d2[k], (float)res, c);
                    }

                    roadAcc[k] = roadShare;
                    colour[k] = Srgb(c);
                }
            });
            for (var j = 0; j < n / 4; j++)
            {
                for (var i = 0; i < n / 4; i++)
                {
                    float s = 0;
                    for (var dj = 0; dj < 4; dj++)
                    {
                        for (var di = 0; di < 4; di++)
                        {
                            s += roadAcc[(j * 4 + dj) * n + i * 4 + di];
                        }
                    }

                    road[j * (n / 4) + i] = (byte)Math.Clamp((int)(s / 16 * 255 + 0.5f), 0, 255);
                }
            }

            return (Image.LoadPixelData<Rgb24>(colour, n, n), Image.LoadPixelData<L8>(road, n / 4, n / 4));
        }

        private (Vector3 Colour, float Road) ShadeTerrain(in TerrainTri t, float w0, float w1, float w2, float x, float z, float res)
        {
            var sh = shaders[t.Shader];
            var atlas = t.A0 * w0 + t.A1 * w1 + t.A2 * w2;
            var mesh = t.M0 * w0 + t.M1 * w1 + t.M2 * w2;
            var other = t.R0 * w0 + t.R1 * w1 + t.R2 * w2;
            var wt = t.W0 * w0 + t.W1 * w1 + t.W2 * w2;

            // Vertex selector (z, w): (1,0) = layer a, (0,1) = layer b, (0,0) = layer c (verified on seams); (1,1)
            // only occurs on single-layer triangles.
            Span<int> layer = [t.La, t.Lb, t.Lc];
            var wa = Math.Clamp(wt.X, 0, 1);
            var wb = Math.Clamp(wt.Y, 0, 1);
            Span<float> amount = [wa, wb, Math.Max(0, 1 - wa - wb)];
            if (amount[0] + amount[1] + amount[2] <= 1e-6f)
            {
                amount[0] = 1;
            }

            var excluded = 0;
            if (o.ExcludeRoad)
            {
                for (var l = 0; l < 3; l++)
                {
                    if (roadLayers.Contains(layer[l]))
                    {
                        excluded++;
                        layer[l] = fallbackLayer;
                    }
                }
            }

            Span<Vector3> colours = stackalloc Vector3[3];
            Span<float> scores = stackalloc float[3];
            var best = float.MinValue;
            for (var l = 0; l < 3; l++)
            {
                var s = layer[l];
                scores[l] = float.MinValue;
                if (amount[l] <= 1e-4f)
                {
                    continue;
                }

                var tex = slices[s];
                var scale = sh.Scale[s];
                Vector2 uv = (o.UvMode, sh.DifUv[s] == 0) switch
                {
                    ("A", true) => other * (scale / BaseUvScale),
                    ("B", true) => mesh * (scale / BaseUvScale),
                    ("C", true) => other * (scale / BaseUvScale),
                    ("C", false) => mesh * (scale / BaseUvScale),
                    ("D", _) => mesh * (scale / BaseUvScale),
                    _ => new Vector2(x, z) * scale,
                };
                var level = MathF.Log2(MathF.Max(1, tex.Width * scale * res));
                var c = tex.Sample(uv, level, true);
                colours[l] = new Vector3(c.X, c.Y, c.Z);
                scores[l] = amount[l] + (c.W - _meanHeight[s]) * HeightInfluence * sh.Thickness[s];
                best = MathF.Max(best, scores[l]);
            }

            // Height blend: layers within BlendDepth of the highest score mix, weighted by how far above that floor.
            Vector3 colour = default;
            float sum = 0, road = 0;
            for (var l = 0; l < 3; l++)
            {
                var b = MathF.Max(scores[l] - (best - BlendDepth), 0);
                colour += colours[l] * b;
                road += (roadLayers.Contains(layer[l]) ? b : 0);
                sum += b;
            }

            colour /= sum;
            road /= sum;

            var cm = colourMaps[t.Shader];
            if (cm is not null)
            {
                var level = MathF.Log2(MathF.Max(1, cm.Width * t.AtlasPerMetre * res));
                var s = cm.Sample(atlas, level, false);
                colour *= 2 * new Vector3(s.X, s.Y, s.Z);
            }

            var lm = lightmaps[t.Shader];
            if (lm is not null)
            {
                var level = MathF.Log2(MathF.Max(1, lm.Width * t.AtlasPerMetre * res));
                var s = lm.Sample(atlas, level, false);
                colour *= 2 * new Vector3(s.X, s.Y, s.Z);
            }

            return (colour, excluded == 3 ? 0 : road);
        }

        private Vector3 ShadeDecal(in DecalTri d, float w0, float w1, float w2, float res, Vector3 under)
        {
            var tex = decalTextures[d.Texture];
            if (tex is null)
            {
                return under;
            }

            var uv = d.U0 * w0 + d.U1 * w1 + d.U2 * w2;
            var level = MathF.Log2(MathF.Max(1, tex.Width * d.UvPerMetre * res));
            var c = tex.Sample(uv, level, true);
            var colour = new Vector3(c.X, c.Y, c.Z);
            if (d.Lightmap >= 0 && decalLightmaps[d.Lightmap] is { } lm)
            {
                var s = lm.Sample(d.A0 * w0 + d.A1 * w1 + d.A2 * w2, 2, false);
                colour *= 2 * new Vector3(s.X, s.Y, s.Z);
            }

            return Vector3.Lerp(under, colour, Math.Clamp(c.W, 0, 1));
        }

        /// <summary>Weight of a layer's height (slice alpha minus its mean) against its vertex weight, times Thickness.</summary>
        private const float HeightInfluence = 0.5f;

        /// <summary>Score range that still mixes; smaller = crisper height-blend edges.</summary>
        private const float BlendDepth = 0.2f;
    }

    /// <summary>Lazily decoded textures by PSSG id, from tracksplit.pssg then the location's other texture PSSGs.</summary>
    private sealed class TextureCache(PssgFile primary, Dr2Install install, StageRef st)
    {
        private readonly Dictionary<string, BakeTextureImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
        private List<PssgFile>? _others;

        public BakeTextureImage? Get(string reference)
        {
            var id = reference[(reference.IndexOf('#') + 1)..];
            if (_cache.TryGetValue(id, out var hit))
            {
                return hit;
            }

            var texture = Find(id);
            var image = texture is null ? null : BakeTextureImage.FromPssg(texture, -1);
            Console.WriteLine(image is null
                ? $"  texture {id}: not found"
                : $"  texture {id}: {texture!.TexelFormat} {image.Width}x{image.Height}, mean rgb {image.MeanRgb:F3} alpha {image.MeanAlpha:F3}");
            return _cache[id] = image;
        }

        public BakeTextureImage[] Array(string reference)
        {
            var id = reference[(reference.IndexOf('#') + 1)..];
            var texture = Find(id) ?? throw new FileNotFoundException($"texture array {id} not found");
            var list = Enumerable.Range(0, (int)texture.ImageBlockCount).AsParallel().AsOrdered()
                .Select(i => BakeTextureImage.FromPssg(texture, i)).ToArray();
            Console.WriteLine($"  texture array {id}: {texture.TexelFormat} {texture.Width}x{texture.Height} x{list.Length}; " +
                              "alpha mean/std per slice: " + string.Join(" ", list.Select((s, i) => $"{i}:{s.MeanAlpha:F2}/{s.StdAlpha:F2}")));
            return list;
        }

        private PssgTexture? Find(string id)
        {
            var t = primary.TryGetObject<PssgTexture>(id.AsMemory());
            if (t is not null)
            {
                return t;
            }

            _others ??= install.List($"{st.LocDir}/")
                .Where(p => p.EndsWith(".pssg", StringComparison.OrdinalIgnoreCase) && p.Contains("textures", StringComparison.OrdinalIgnoreCase)
                            && p.Count(c => c == '/') == st.LocDir.Count(c => c == '/') + 1)
                .Select(p => PssgCommands.Open(install.FetchAsync(p).GetAwaiter().GetResult())).ToList();
            return _others.Select(f => f.TryGetObject<PssgTexture>(id.AsMemory())).Where(x => x is not null).MaxBy(x => (long)x!.Width * x.Height);
        }
    }
}

/// <summary>Decoded RGBA8 texture with a box-filtered mip chain; samples return sRGB-decoded colour and raw alpha.</summary>
internal sealed class BakeTextureImage
{
    private static readonly float[] SrgbToLinear = Enumerable.Range(0, 256)
        .Select(i => i / 255f <= 0.04045f ? i / 255f / 12.92f : MathF.Pow((i / 255f + 0.055f) / 1.055f, 2.4f)).ToArray();

    private readonly List<(int W, int H, byte[] Px)> _levels = [];
    private readonly bool _srgb;

    public int Width => _levels[0].W;

    public int Height => _levels[0].H;

    public Vector3 MeanRgb { get; }

    public float MeanAlpha { get; }

    public float StdAlpha { get; }

    private BakeTextureImage(byte[] rgba, int w, int h, bool srgb)
    {
        _srgb = srgb;
        _levels.Add((w, h, rgba));
        while (w > 1 || h > 1)
        {
            var (nw, nh) = (Math.Max(1, w / 2), Math.Max(1, h / 2));
            var src = _levels[^1].Px;
            var dst = new byte[nw * nh * 4];
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        var s = 0;
                        for (var dy = 0; dy < 2; dy++)
                        {
                            for (var dx = 0; dx < 2; dx++)
                            {
                                s += src[((Math.Min(y * 2 + dy, h - 1)) * w + Math.Min(x * 2 + dx, w - 1)) * 4 + c];
                            }
                        }

                        dst[(y * nw + x) * 4 + c] = (byte)((s + 2) / 4);
                    }
                }
            }

            _levels.Add((nw, nh, dst));
            (w, h) = (nw, nh);
        }

        double r = 0, g = 0, b = 0, a = 0, a2 = 0;
        var n = rgba.Length / 4;
        for (var i = 0; i < n; i++)
        {
            r += Decode(rgba[i * 4]);
            g += Decode(rgba[i * 4 + 1]);
            b += Decode(rgba[i * 4 + 2]);
            var av = rgba[i * 4 + 3] / 255.0;
            a += av;
            a2 += av * av;
        }

        MeanRgb = new Vector3((float)(r / n), (float)(g / n), (float)(b / n));
        MeanAlpha = (float)(a / n);
        StdAlpha = (float)Math.Sqrt(Math.Max(0, a2 / n - MeanAlpha * MeanAlpha));
    }

    private float Decode(byte v) => _srgb ? SrgbToLinear[v] : v / 255f;

    public static BakeTextureImage FromPssg(PssgTexture texture, int slice)
    {
        using var ms = new MemoryStream();
        if (slice >= 0 && texture.ImageBlockCount > 1)
        {
            ms.Write(StageTextures.SliceDds(texture, texture.ImageBlocks.ElementAt(slice).ImageData.Value));
        }
        else
        {
            texture.ToDdsFile().Write(ms);
        }

        ms.Position = 0;
        var dds = BCnEncoder.Shared.ImageFiles.DdsFile.Load(ms);
        var pixels = new BcDecoder().Decode(dds);
        var rgba = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (pixels[i].r, pixels[i].g, pixels[i].b, pixels[i].a);
        }

        // Colour textures are treated as sRGB unless they are the grey-centred data maps (colour map, lightmap),
        // which DR2 multiplies by 2 and which only stay neutral at 0.5 if read as linear data.
        var id = texture.Id;
        var data = id.Contains("colourmap", StringComparison.OrdinalIgnoreCase) || id.EndsWith("_lm.tga", StringComparison.OrdinalIgnoreCase)
                   || id.Contains("_lm", StringComparison.OrdinalIgnoreCase) || IsSectionColourMap(id);
        return new BakeTextureImage(rgba, (int)dds.header.dwWidth, (int)dds.header.dwHeight, !data);
    }

    /// <summary>Section colour maps of the first NZ location are named like <c>nz_01_a_01_d</c>.</summary>
    private static bool IsSectionColourMap(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-z]+_\d\d_[a-z]_\d\d_d\.tga$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Bilinear sample at a (fractional, rounded) mip level; xyz decoded colour, w alpha.</summary>
    public Vector4 Sample(Vector2 uv, float level, bool wrap)
    {
        var (w, h, px) = _levels[Math.Clamp((int)MathF.Round(level), 0, _levels.Count - 1)];
        var fx = uv.X * w - 0.5f;
        var fy = uv.Y * h - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var a = Texel(px, w, h, x0, y0, wrap);
        var b = Texel(px, w, h, x0 + 1, y0, wrap);
        var c = Texel(px, w, h, x0, y0 + 1, wrap);
        var d = Texel(px, w, h, x0 + 1, y0 + 1, wrap);
        return Vector4.Lerp(Vector4.Lerp(a, b, tx), Vector4.Lerp(c, d, tx), ty);
    }

    private Vector4 Texel(byte[] px, int w, int h, int x, int y, bool wrap)
    {
        if (wrap)
        {
            x = ((x % w) + w) % w;
            y = ((y % h) + h) % h;
        }
        else
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
        }

        var i = (y * w + x) * 4;
        return new Vector4(Decode(px[i]), Decode(px[i + 1]), Decode(px[i + 2]), px[i + 3] / 255f);
    }
}

/// <summary>Uniform grid over triangle XZ bounds.</summary>
internal sealed class TriGrid
{
    private readonly float _cell;
    private readonly Dictionary<(int, int), List<int>> _cells = new();

    public TriGrid(IReadOnlyList<(Vector2 Min, Vector2 Max)> bounds, float cell)
    {
        _cell = cell;
        for (var i = 0; i < bounds.Count; i++)
        {
            var (lo, hi) = bounds[i];
            for (var x = C(lo.X); x <= C(hi.X); x++)
            {
                for (var z = C(lo.Y); z <= C(hi.Y); z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list))
                    {
                        _cells[(x, z)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }
    }

    private int C(float v) => (int)MathF.Floor(v / _cell);

    public IEnumerable<int> Query(Vector2 lo, Vector2 hi)
    {
        var seen = new HashSet<int>();
        for (var x = C(lo.X); x <= C(hi.X); x++)
        {
            for (var z = C(lo.Y); z <= C(hi.Y); z++)
            {
                if (_cells.TryGetValue((x, z), out var list))
                {
                    foreach (var i in list)
                    {
                        if (seen.Add(i))
                        {
                            yield return i;
                        }
                    }
                }
            }
        }
    }

    public bool Any(Vector2 lo, Vector2 hi)
    {
        for (var x = C(lo.X); x <= C(hi.X); x++)
        {
            for (var z = C(lo.Y); z <= C(hi.Y); z++)
            {
                if (_cells.ContainsKey((x, z)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>Top-down (XZ) triangle scan conversion onto a pixel grid; pixel (i, j) centre = min + (i + 0.5, j + 0.5) * res.</summary>
internal static class Raster
{
    public delegate void Pixel(int i, int j, float b0, float b1, float b2, float y);

    public static void Triangle(Vector3 p0, Vector3 p1, Vector3 p2, double minX, double minZ, double res, int w, int h, Pixel pixel)
    {
        double X(Vector3 p) => (p.X - minX) / res - 0.5;
        double Z(Vector3 p) => (p.Z - minZ) / res - 0.5;
        var (x0, z0, x1, z1, x2, z2) = (X(p0), Z(p0), X(p1), Z(p1), X(p2), Z(p2));
        var area = (x1 - x0) * (z2 - z0) - (x2 - x0) * (z1 - z0);
        if (Math.Abs(area) < 1e-12)
        {
            return;
        }

        var iMin = Math.Max(0, (int)Math.Ceiling(Math.Min(x0, Math.Min(x1, x2))));
        var iMax = Math.Min(w - 1, (int)Math.Floor(Math.Max(x0, Math.Max(x1, x2))));
        var jMin = Math.Max(0, (int)Math.Ceiling(Math.Min(z0, Math.Min(z1, z2))));
        var jMax = Math.Min(h - 1, (int)Math.Floor(Math.Max(z0, Math.Max(z1, z2))));
        const double eps = -1e-9;
        for (var j = jMin; j <= jMax; j++)
        {
            for (var i = iMin; i <= iMax; i++)
            {
                var e0 = ((x2 - x1) * (j - z1) - (i - x1) * (z2 - z1)) / area;
                var e1 = ((x0 - x2) * (j - z2) - (i - x2) * (z0 - z2)) / area;
                var e2 = 1 - e0 - e1;
                if (e0 < eps || e1 < eps || e2 < eps)
                {
                    continue;
                }

                pixel(i, j, (float)e0, (float)e1, (float)e2, (float)(e0 * p0.Y + e1 * p1.Y + e2 * p2.Y));
            }
        }
    }
}

/// <summary>Road band: XZ distance to the gate crossings' polyline within the local half-width (gate left..right) + pad.</summary>
internal sealed class RoadBand
{
    private readonly Dictionary<(int, int), List<(Vector2 A, Vector2 B, float HalfWidth)>> _grid = new();
    private const float Cell = 32;

    public RoadBand(ProgressTrack progress, float pad)
    {
        var gates = progress.Gates;
        for (var i = 0; i + 1 < gates.Count; i++)
        {
            var a = new Vector2(gates[i].Crossing.X, gates[i].Crossing.Z);
            var b = new Vector2(gates[i + 1].Crossing.X, gates[i + 1].Crossing.Z);
            var half = Math.Max(Vector3.Distance(gates[i].Left, gates[i].Right), Vector3.Distance(gates[i + 1].Left, gates[i + 1].Right)) / 2 + pad;
            var lo = Vector2.Min(a, b) - new Vector2(half);
            var hi = Vector2.Max(a, b) + new Vector2(half);
            for (var x = (int)MathF.Floor(lo.X / Cell); x <= (int)MathF.Floor(hi.X / Cell); x++)
            {
                for (var z = (int)MathF.Floor(lo.Y / Cell); z <= (int)MathF.Floor(hi.Y / Cell); z++)
                {
                    if (!_grid.TryGetValue((x, z), out var list))
                    {
                        _grid[(x, z)] = list = [];
                    }

                    list.Add((a, b, half));
                }
            }
        }
    }

    public bool Contains(Vector3 p)
    {
        if (!_grid.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell)), out var list))
        {
            return false;
        }

        var q = new Vector2(p.X, p.Z);
        foreach (var (a, b, half) in list)
        {
            var ab = b - a;
            var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            if (Vector2.DistanceSquared(q, a + t * ab) <= half * half)
            {
                return true;
            }
        }

        return false;
    }
}
