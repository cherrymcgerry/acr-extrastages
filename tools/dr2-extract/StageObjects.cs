using System.Globalization;
using System.Numerics;
using System.Xml;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Dr2Extract;

/// <summary>physparams.xml TEMPLATESHAPEPROPS: shape class -> DR2 object material (4-char) and mass source.</summary>
internal sealed record ShapeProps(string Id, string Material, string? CoreMaterial, string? MassSource, string? BodyOverride);

/// <summary>
/// Collision primitive in template space. Conventions (see docs/dr2-formats.md): box = full extents
/// (width, height, length) along local (x, y, z), centred; cylinder = axis local y, centred; cone = axis local +y
/// towards the apex, origin at the centroid (h/4 above the base); sphere centred; vcquadtree = mesh file.
/// </summary>
internal sealed record ObjectShape(string Id, string Kind, Dictionary<string, float> Dimensions, Matrix4x4 Local,
    string Props, string? Material, string? CoreMaterial, string? BreakImpulse, string? MeshFile);

internal sealed record ObjectBody(string SubEntity, bool Static, List<ObjectShape> Shapes, string? Renderable);

internal sealed record ObjectTemplate(string Id, string Source, List<ObjectBody> Bodies, List<string> Renderables,
    int InactiveSubentities, int Constraints)
{
    public IEnumerable<ObjectShape> Shapes => Bodies.SelectMany(b => b.Shapes);
}

internal sealed record ObjectInstance(string Id, string Kind, string TemplateFile, string Template, Matrix4x4 World,
    string? Tag);

internal static class StageObjects
{
    public static XmlDocument LoadXml(string path)
    {
        using var fs = File.OpenRead(path);
        var first = fs.ReadByte();
        fs.Position = 0;
        if (first == '<' || first == 0xEF)
        {
            var doc = new XmlDocument();
            doc.Load(fs);
            return doc;
        }

        return XmlCommands.Load(path);
    }

    public static Dictionary<string, ShapeProps> LoadPhysParams(string path) =>
        LoadXml(path).SelectNodes("//TEMPLATESHAPEPROPS")!.Cast<XmlElement>()
            .Select(e => new ShapeProps(e.GetAttribute("id"), e.GetAttribute("material"), Attr(e, "corematerial"),
                Attr(e, "massSource"), Attr(e, "bodyOverride")))
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Last());

    /// <summary>
    /// <c>route_N/objects.ens</c> (CSSGXml text): TEMPLATEBASICENTITYINSTANCE / TEMPLATEENTITYINSTANCE with a
    /// row-major TEMPLATETRANSFORM (translation in the last row). Cloth instances (cables, tape) are only counted.
    /// </summary>
    public static List<ObjectInstance> LoadInstances(string path, out int clothInstances)
    {
        var doc = LoadXml(path);
        clothInstances = doc.SelectNodes("//TEMPLATECLOTHINSTANCE")!.Count;
        var result = new List<ObjectInstance>();
        foreach (XmlElement e in doc.SelectNodes("//TEMPLATEBASICENTITYINSTANCE | //TEMPLATEENTITYINSTANCE")!)
        {
            var uri = e.GetAttribute("uri");
            var hash = uri.IndexOf('#');
            var transform = e.SelectSingleNode("TEMPLATETRANSFORM")?.InnerText
                            ?? throw new InvalidDataException($"{path}: instance {e.GetAttribute("id")} has no transform");
            result.Add(new ObjectInstance(e.GetAttribute("id"),
                e.Name == "TEMPLATEBASICENTITYINSTANCE" ? "basic" : "entity",
                hash > 0 ? uri[..hash] : "", uri[(hash + 1)..], Matrix(transform), Attr(e, "instance_tag")));
        }

        return result;
    }

    /// <summary>
    /// Template library (<c>route_objecttypes.pssg</c>, CSSGXml text): TEMPLATEENTITYREFERENCE -> TEMPLATEENTITY with
    /// sub-entities (rigid body + shapes + renderable). Sub-entities deleted by the entity's <c>initial</c> behaviour
    /// (damage-state pieces) are left out.
    /// </summary>
    public static Dictionary<string, ObjectTemplate> LoadTemplates(string path, string source,
        IReadOnlyDictionary<string, ShapeProps> props)
    {
        var doc = LoadXml(path);
        var byId = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        foreach (XmlElement e in doc.SelectNodes("//*[@id]")!)
        {
            byId.TryAdd(e.GetAttribute("id"), e);
        }

        XmlElement? Ref(string uri) => byId.GetValueOrDefault(uri.TrimStart('#'));
        var result = new Dictionary<string, ObjectTemplate>(StringComparer.Ordinal);
        foreach (XmlElement reference in doc.SelectNodes("//TEMPLATEENTITYREFERENCE")!)
        {
            var id = reference.GetAttribute("id");
            if (Ref(reference.GetAttribute("uri")) is not { Name: "TEMPLATEENTITY" } entity)
            {
                result[id] = new ObjectTemplate(id, source, [], [], 0, 0);
                continue;
            }

            var deleted = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElement behaviour in entity.SelectNodes("TEMPLATEBEHAVIOUR")!)
            {
                if (!behaviour.GetAttribute("id").StartsWith("initial", StringComparison.Ordinal) ||
                    Ref(behaviour.GetAttribute("typeuri")) is not { } type)
                {
                    continue;
                }

                foreach (XmlElement change in type.SelectNodes("TEMPLATECOMPONENTCHANGE")!)
                {
                    var target = change.GetAttribute("componenturi").TrimStart('#');
                    _ = change.GetAttribute("operation") == "delete" ? deleted.Add(target) : deleted.Remove(target);
                }
            }

            var bodies = new List<ObjectBody>();
            var renderables = new List<string>();
            var inactive = 0;
            foreach (XmlElement sub in entity.SelectNodes("TEMPLATESUBENTITY")!)
            {
                if (deleted.Contains(sub.GetAttribute("id")))
                {
                    inactive++;
                    continue;
                }

                var renderable = sub.SelectSingleNode("TEMPLATETRANSFORMABLE") is XmlElement t && Ref(t.GetAttribute("uri")) is { } r
                    ? r.GetAttribute("uri")
                    : null;
                if (renderable is not null && !renderables.Contains(renderable))
                {
                    renderables.Add(renderable);
                }

                foreach (XmlElement body in sub.SelectNodes("TEMPLATERIGIDBODY")!)
                {
                    var shapes = new List<ObjectShape>();
                    foreach (XmlElement rs in body.SelectNodes("TEMPLATERIGIDBODYSHAPE")!)
                    {
                        if (Ref(rs.GetAttribute("uri")) is not { } shape)
                        {
                            continue;
                        }

                        var local = rs.SelectSingleNode("TEMPLATETRANSFORM") is { } lt ? Matrix(lt.InnerText) : Matrix4x4.Identity;
                        var propsId = shape.GetAttribute("props").Split('#').Last();
                        props.TryGetValue(propsId, out var p);
                        var dims = shape.Attributes!.Cast<XmlAttribute>()
                            .Where(a => a.Name is not ("id" or "props" or "filename") &&
                                        float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                            .ToDictionary(a => a.Name, a => float.Parse(a.Value, CultureInfo.InvariantCulture));
                        var impulse = (rs.SelectSingleNode("TEMPLATEBEHAVIOUR") as XmlElement)?.GetAttribute("impulse").Split('#').Last();
                        shapes.Add(new ObjectShape(shape.GetAttribute("id"),
                            shape.Name["TEMPLATESHAPE".Length..].ToLowerInvariant(), dims, local, propsId, p?.Material,
                            p?.CoreMaterial, string.IsNullOrEmpty(impulse) ? null : impulse, Attr(shape, "filename")));
                    }

                    bodies.Add(new ObjectBody(sub.GetAttribute("id"), body.GetAttribute("infMass") == "1", shapes, renderable));
                }
            }

            result[id] = new ObjectTemplate(id, source, bodies, renderables, inactive,
                entity.SelectNodes("TEMPLATECONSTRAINT")!.Count);
        }

        return result;
    }

    /// <summary>Axis-aligned bounds of a template's collision shapes in template space (vcquadtree shapes skipped).</summary>
    public static (Vector3 Min, Vector3 Max)? ShapeBounds(IEnumerable<ObjectShape> shapes)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var any = false;
        foreach (var s in shapes)
        {
            foreach (var p in Tessellate(s).SelectMany(t => new[] { t.A, t.B, t.C }))
            {
                var w = Vector3.Transform(p, s.Local);
                (min, max, any) = (Vector3.Min(min, w), Vector3.Max(max, w), true);
            }
        }

        return any ? (min, max) : null;
    }

    /// <summary>Triangles of a primitive in shape space (empty for mesh shapes and unknown kinds).</summary>
    public static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Tessellate(ObjectShape s, int segments = 8)
    {
        float D(string n) => s.Dimensions.GetValueOrDefault(n);
        switch (s.Kind)
        {
            case "box":
            {
                var h = new Vector3(D("width"), D("height"), D("length")) / 2;
                Vector3 P(int i) => new((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);
                int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
                foreach (var f in faces)
                {
                    yield return (P(f[0]), P(f[1]), P(f[2]));
                    yield return (P(f[0]), P(f[2]), P(f[3]));
                }

                break;
            }
            case "cylinder" or "cone":
            {
                var cone = s.Kind == "cone";
                var (r, height) = cone ? (D("baseRadius"), D("fullHeight")) : (D("radius"), D("height"));
                var (y0, y1) = cone ? (-height / 4, height * 3 / 4) : (-height / 2, height / 2);
                var r1 = cone ? 0 : r;
                for (var i = 0; i < segments; i++)
                {
                    var (a0, a1) = (2 * MathF.PI * i / segments, 2 * MathF.PI * (i + 1) / segments);
                    var (b0, b1) = (new Vector3(r * MathF.Cos(a0), y0, r * MathF.Sin(a0)), new Vector3(r * MathF.Cos(a1), y0, r * MathF.Sin(a1)));
                    var (t0, t1) = (new Vector3(r1 * MathF.Cos(a0), y1, r1 * MathF.Sin(a0)), new Vector3(r1 * MathF.Cos(a1), y1, r1 * MathF.Sin(a1)));
                    yield return (b0, t1, b1);
                    if (!cone)
                    {
                        yield return (b0, t0, t1);
                        yield return (new Vector3(0, y1, 0), t1, t0);
                    }

                    yield return (new Vector3(0, y0, 0), b0, b1);
                }

                break;
            }
            case "sphere":
            {
                var r = D("radius");
                var rings = segments / 2;
                Vector3 P(int ring, int seg)
                {
                    var (phi, theta) = (MathF.PI * ring / rings, 2 * MathF.PI * seg / segments);
                    return new Vector3(r * MathF.Sin(phi) * MathF.Cos(theta), r * MathF.Cos(phi), r * MathF.Sin(phi) * MathF.Sin(theta));
                }

                for (var ring = 0; ring < rings; ring++)
                {
                    for (var seg = 0; seg < segments; seg++)
                    {
                        yield return (P(ring, seg), P(ring + 1, seg + 1), P(ring + 1, seg));
                        yield return (P(ring, seg), P(ring, seg + 1), P(ring + 1, seg + 1));
                    }
                }

                break;
            }
        }
    }

    /// <summary>World-space collision primitives, one glTF primitive per ACR wall material (like collision.glb).</summary>
    public static int WriteCollisionGlb(IEnumerable<(Matrix4x4 World, string Wall, ObjectShape Shape)> shapes, string path)
    {
        var mesh = new MeshBuilder<VertexPosition>("objects_collision");
        var materials = new Dictionary<string, MaterialBuilder>();
        var triangles = 0;
        foreach (var (world, wall, shape) in shapes)
        {
            if (!materials.TryGetValue(wall, out var material))
            {
                materials[wall] = material = new MaterialBuilder(wall).WithMetallicRoughnessShader().WithBaseColor(Vector4.One);
            }

            var primitive = mesh.UsePrimitive(material);
            foreach (var (a, b, c) in Tessellate(shape))
            {
                primitive.AddTriangle(new VertexPosition(Vector3.Transform(a, world)), new VertexPosition(Vector3.Transform(b, world)),
                    new VertexPosition(Vector3.Transform(c, world)));
                triangles++;
            }
        }

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        scene.ToGltf2().SaveGLB(path);
        return triangles;
    }

    public static Matrix4x4 Matrix(string text)
    {
        var v = text.Split((char[])[' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (v.Length != 16)
        {
            throw new InvalidDataException($"expected 16 matrix values, got {v.Length}");
        }

        return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
    }

    private static string? Attr(XmlElement e, string name) => e.HasAttribute(name) ? e.GetAttribute(name) : null;
}
