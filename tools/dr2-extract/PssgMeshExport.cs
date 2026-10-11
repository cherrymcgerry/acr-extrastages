using System.Globalization;
using System.Numerics;
using System.Text.Json;
using EgoEngineLibrary.Formats.Pssg;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Dr2Extract;

internal sealed record TextureRef(string Parameter, string Texture);

internal sealed record RenderMaterial(string Name, string ShaderGroup, List<TextureRef> Textures)
{
    public int Triangles { get; set; }
}

internal sealed record RenderExportResult(
    int Triangles,
    int RenderNodes,
    Vector3 Min,
    Vector3 Max,
    List<RenderMaterial> Materials,
    List<string> Skipped,
    int WindingMatchesNormals);

/// <summary>
/// Generic PSSG scene -> glTF for track geometry: walks the NODE library, bakes world transforms into vertices
/// (so output is in DR2 world coordinates), takes LOD0 of LOD nodes, and writes positions, normals, vertex colour,
/// two UV sets and one glTF material per PSSG shader instance (named after it; textures are listed in the result,
/// not embedded).
/// </summary>
internal static class PssgMeshExport
{
    public static int Run(string[] args)
    {
        var pos = Program.Positional(args);
        var pssg = PssgCommands.Open(pos[0]);
        Func<Vector3, bool>? keep = null;
        if (Program.Option(args, "--bounds") is { } b)
        {
            var v = b.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            keep = p => p.X >= v[0] && p.Z >= v[1] && p.X <= v[2] && p.Z <= v[3];
        }

        var result = Export(pssg, pos[1], keep, Program.Option(args, "--node"));
        Console.WriteLine($"wrote {pos[1]}: {result.RenderNodes} render nodes, {result.Triangles} triangles, " +
                          $"bounds {result.Min} .. {result.Max}");
        foreach (var m in result.Materials.Where(m => m.Triangles > 0))
        {
            Console.WriteLine($"  material {m.Name} ({m.ShaderGroup}) tris={m.Triangles} " +
                              string.Join(" ", m.Textures.Select(t => $"{t.Parameter}={t.Texture}")));
        }

        foreach (var g in result.Skipped.GroupBy(s => s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..]))
        {
            Console.WriteLine($"  skipped {g.Count()} instance(s): {g.Key} (e.g. {g.First().Split(": ")[0]})");
        }

        File.WriteAllText(Path.ChangeExtension(pos[1], ".materials.json"),
            JsonSerializer.Serialize(result.Materials, Json.Options));

        if (Program.Option(args, "--textures") is { } textureDir)
        {
            var sources = new List<PssgFile> { pssg };
            foreach (var extra in (Program.Option(args, "--texture-pssg") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                sources.Insert(0, PssgCommands.Open(extra));
            }

            var names = result.Materials.Where(m => m.Triangles > 0).SelectMany(m => m.Textures).Select(t => t.Texture);
            var (written, missing) = ExportTextures(names, sources, textureDir);
            Console.WriteLine($"textures: {written} written to {textureDir}, {missing.Count} not found");
            foreach (var m in missing.Take(20))
            {
                Console.WriteLine($"  missing texture {m}");
            }
        }

        return 0;
    }

    public static RenderExportResult Export(PssgFile pssg, string outGlb, Func<Vector3, bool>? keepTriangle,
        string? nodeFilter = null)
    {
        var library = pssg.Elements<PssgLibrary>().FirstOrDefault(x => x.Type == "NODE")
                      ?? pssg.Elements<PssgLibrary>().FirstOrDefault(x => x.Type == "YYY")
                      ?? throw new InvalidDataException("no NODE library in pssg");

        var scene = new SceneBuilder();
        var materials = new Dictionary<PssgShaderInstance, (MaterialBuilder Builder, RenderMaterial Info)>();
        var skipped = new List<string>();
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var triangles = 0;
        var renderNodes = 0;
        var windingMatches = 0;

        foreach (var root in library.ChildElements.OfType<PssgNode>())
        {
            Walk(root, Matrix4x4.Identity);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outGlb))!);
        scene.ToGltf2().SaveGLB(outGlb);

        // Batched terrain (batched_track.fx) selects terrain_array shader instances indirectly, so list every
        // shader in the file; unreferenced ones keep Triangles == 0.
        foreach (var shader in pssg.Elements<PssgShaderInstance>())
        {
            GetMaterial(shader);
        }

        return new RenderExportResult(triangles, renderNodes, min, max,
            materials.Values.Select(m => m.Info).ToList(), skipped, windingMatches);

        void Walk(PssgNode node, Matrix4x4 parentWorld)
        {
            var world = node.ChildElements.OfType<PssgTransform>().FirstOrDefault()?.Transform * parentWorld ?? parentWorld;
            // Track terrain tiles come in pairs: <KIND>_i_j with the real shaders and <KIND>BATCH_i_j drawing the same
            // surface again with batched_track.fx, so only the former is exported.
            var duplicate = node.Id.Contains("BATCH_", StringComparison.Ordinal);
            if (node is PssgVisibleRenderNode visible && !duplicate &&
                (nodeFilter is null || node.Id.Contains(nodeFilter, StringComparison.OrdinalIgnoreCase)))
            {
                ExportRenderNode(visible, world);
            }

            foreach (var child in node.ChildElements.OfType<PssgNode>())
            {
                Walk(child, world);
            }
        }

        void ExportRenderNode(PssgVisibleRenderNode node, Matrix4x4 world)
        {
            var instances = node.RenderInstances.ToList();
            if (instances.Count == 0 && node is PssgLodVisibleRenderNode lod)
            {
                instances = lod.LodRenderInstances.InstanceLists.FirstOrDefault()?.RenderInstances.ToList() ?? [];
            }

            var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture2, VertexEmpty>(node.Id);
            var added = 0;
            foreach (var instance in instances)
            {
                if (instance is not PssgRenderStreamInstance stream)
                {
                    skipped.Add($"{node.Id}: instance type {instance.Name}");
                    continue;
                }

                try
                {
                    var shader = stream.TryGetShaderInstance();
                    if (shader is null)
                    {
                        skipped.Add($"{node.Id}: missing shader {stream.Shader}");
                        continue;
                    }

                    var material = GetMaterial(shader);
                    var rds = new RdsReader(stream.GetRenderDataSource());
                    var primitive = mesh.UsePrimitive(material.Builder);
                    foreach (var (a, b, c) in rds.Triangles())
                    {
                        var pa = Vector3.Transform(rds.Position(a), world);
                        var pb = Vector3.Transform(rds.Position(b), world);
                        var pc = Vector3.Transform(rds.Position(c), world);
                        if (keepTriangle is not null && !keepTriangle((pa + pb + pc) / 3f))
                        {
                            continue;
                        }

                        var face = Vector3.Cross(pb - pa, pc - pa);
                        face = face.LengthSquared() > 0 ? Vector3.Normalize(face) : Vector3.UnitY;
                        if (rds.HasNormals && Vector3.Dot(face, Vector3.TransformNormal(rds.Normal(a) + rds.Normal(b) + rds.Normal(c), world)) > 0)
                        {
                            windingMatches++;
                        }

                        primitive.AddTriangle(Vertex(rds, a, pa, world, face), Vertex(rds, b, pb, world, face),
                            Vertex(rds, c, pc, world, face));
                        min = Vector3.Min(min, Vector3.Min(pa, Vector3.Min(pb, pc)));
                        max = Vector3.Max(max, Vector3.Max(pa, Vector3.Max(pb, pc)));
                        material.Info.Triangles++;
                        triangles++;
                        added++;
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or InvalidOperationException)
                {
                    skipped.Add($"{node.Id}: {ex.Message}");
                }
            }

            if (Environment.GetEnvironmentVariable("DR2_PSSG_NODES") is not null)
            {
                Console.WriteLine($"  node {node.Id} instances={instances.Count} kept={added} translation={world.Translation}");
            }

            if (added > 0)
            {
                scene.AddRigidMesh(mesh, new NodeBuilder(node.Id));
                renderNodes++;
            }
        }

        (MaterialBuilder Builder, RenderMaterial Info) GetMaterial(PssgShaderInstance shader)
        {
            if (materials.TryGetValue(shader, out var existing))
            {
                return existing;
            }

            var group = shader.TryGetShaderGroup();
            var definitions = group?.InputDefinitions.ToList();
            var textures = shader.Inputs.Where(i => i.Type == "texture")
                .Select(i => new TextureRef(
                    definitions is not null && i.ParameterId < definitions.Count ? definitions[(int)i.ParameterId].InputName : $"param{i.ParameterId}",
                    i.Texture))
                .ToList();
            var builder = new MaterialBuilder(shader.Id).WithMetallicRoughnessShader().WithBaseColor(Vector4.One);
            var entry = (builder, new RenderMaterial(shader.Id, shader.ShaderGroup.TrimStart('#'), textures));
            materials.Add(shader, entry);
            return entry;
        }
    }

    private static VertexBuilder<VertexPositionNormal, VertexColor1Texture2, VertexEmpty> Vertex(
        RdsReader rds, uint index, Vector3 position, Matrix4x4 world, Vector3 faceNormal)
    {
        var normal = rds.HasNormals ? Vector3.TransformNormal(rds.Normal(index), world) : Vector3.Zero;
        normal = float.IsFinite(normal.X) && normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : faceNormal;
        return new VertexBuilder<VertexPositionNormal, VertexColor1Texture2, VertexEmpty>(
            new VertexPositionNormal(position, normal),
            new VertexColor1Texture2(rds.Color(index), rds.Uv(index, 0), rds.Uv(index, 1)));
    }

    /// <summary>
    /// Writes each referenced texture (<c>"file.pssg#name"</c> or <c>"#name"</c>) as DDS, looking it up by id in
    /// <paramref name="sources"/> in order.
    /// </summary>
    public static (int Written, List<string> Missing) ExportTextures(IEnumerable<string> references,
        IReadOnlyList<PssgFile> sources, string dir)
    {
        Directory.CreateDirectory(dir);
        var missing = new List<string>();
        var written = 0;
        foreach (var reference in references.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var id = reference[(reference.IndexOf('#') + 1)..];
            var texture = sources.Select(s => s.TryGetObject<PssgTexture>(id.AsMemory())).FirstOrDefault(t => t is not null);
            if (texture is null)
            {
                missing.Add(reference);
                continue;
            }

            using var fs = File.Create(Path.Combine(dir, Path.ChangeExtension(id, ".dds")));
            texture.ToDdsFile().Write(fs);
            written++;
        }

        return (written, missing);
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
