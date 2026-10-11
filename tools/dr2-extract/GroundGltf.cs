using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Dr2Extract;

internal sealed record GroundStats(int Triangles, Vector3 Min, Vector3 Max, SortedDictionary<string, int> TrianglesPerMaterial,
    int UpFacing);

internal static class GroundGltf
{
    /// <summary>
    /// Writes one mesh with one primitive per surface material; the glTF material name is the DR2 surface code.
    /// Positions are written untransformed (DR2 native metres).
    /// </summary>
    public static GroundStats Write(IEnumerable<GroundTriangle> triangles, string path, string meshName = "collision")
    {
        var mesh = new MeshBuilder<VertexPosition>(meshName);
        var materials = new Dictionary<string, MaterialBuilder>();
        var perMaterial = new SortedDictionary<string, int>(StringComparer.Ordinal);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var count = 0;
        var upFacing = 0;
        foreach (var t in triangles)
        {
            if (!materials.TryGetValue(t.Material, out var material))
            {
                material = new MaterialBuilder(t.Material).WithMetallicRoughnessShader().WithBaseColor(Vector4.One);
                materials.Add(t.Material, material);
            }

            mesh.UsePrimitive(material).AddTriangle(new VertexPosition(t.A), new VertexPosition(t.B), new VertexPosition(t.C));
            perMaterial[t.Material] = perMaterial.GetValueOrDefault(t.Material) + 1;
            min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
            max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
            if (Vector3.Cross(t.B - t.A, t.C - t.A).Y > 0)
            {
                upFacing++;
            }

            count++;
        }

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, new NodeBuilder(meshName));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        scene.ToGltf2().SaveGLB(path);
        return new GroundStats(count, min, max, perMaterial, upFacing);
    }
}
