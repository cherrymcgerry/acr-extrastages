using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EgoEngineLibrary.Archive.Jpk;

namespace Dr2Extract;

internal readonly record struct GroundTriangle(Vector3 A, Vector3 B, Vector3 C, string Material, int Flags)
{
    public Vector3 Centroid => (A + B + C) / 3f;
}

internal sealed record GroundCell(string Name, Vector3 BoundsMin, Vector3 BoundsMax, List<GroundTriangle> Triangles);

/// <summary>
/// Reader for the DiRT Rally 2.0 physics ground (<c>route_N/track.jpk</c>): a JPK of quad-tree leaf cells
/// (<c>qt_XX_..._.vcqtc</c>) plus <c>qt.info</c> (overall bounds). EgoEngineLibrary's VcQuadTreeFile targets older
/// EGO games (negative counts, 8-byte packed vertices) and cannot read this variant.
/// </summary>
/// <remarks>
/// vcqtc layout (little endian), as observed in DR2:
/// <code>
/// 0x00 float3 boundsMin, float3 boundsMax
/// 0x18 int numTriangles, numVertices, numMaterials (all positive)
/// 0x24 uint verticesOffset, nodesOffset, trianglesOffset, triangleRefsOffset
/// 0x34 char[4] material code x numMaterials (e.g. "GR3+"; unused slots repeat the last code)
///      zero padding, then at verticesOffset-48: "VCQT" uint32 version(1) 8 bytes zero,
///      float4 offset (minX, 0, minZ, 1), float4 scale (sizeX, 1, sizeZ, 1)
/// verticesOffset: numVertices x { float x01, float yWorld, float z01, uint pad }  world = (x01*sx+ox, y, z01*sz+oz)
/// nodesOffset:    uint16 big-endian nodes (bit 15 = leaf, 0xFFFF = empty leaf)   (not needed for extraction)
/// trianglesOffset: numTriangles x 4 bytes: b0 = v0 >> 2; b1 = (v0 &amp; 3) &lt;&lt; 6 | flags(3) &lt;&lt; 3 | material(3);
///                  b2 = v1 - v0; b3 = v2 - v0
/// </code>
/// </remarks>
internal static class Dr2Ground
{
    private const string InfoEntryName = "qt.info";

    public static (Vector3 Min, Vector3 Max, List<GroundCell> Cells) ReadJpk(string path)
    {
        using var stream = File.OpenRead(path);
        var jpk = new JpkFile();
        jpk.Read(stream);
        var cells = new List<GroundCell>(jpk.Entries.Count);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var entry in jpk.Entries)
        {
            if (entry.Name.Equals(InfoEntryName, StringComparison.OrdinalIgnoreCase))
            {
                min = ReadVector3(entry.Data, 0);
                max = ReadVector3(entry.Data, 12);
                continue;
            }

            cells.Add(ReadVcqtc(entry.Name, entry.Data));
        }

        return (min, max, cells);
    }

    public static GroundCell ReadVcqtc(string name, byte[] d)
    {
        var boundsMin = ReadVector3(d, 0x00);
        var boundsMax = ReadVector3(d, 0x0C);
        var numTriangles = ReadInt(d, 0x18);
        var numVertices = ReadInt(d, 0x1C);
        var numMaterials = ReadInt(d, 0x20);
        var verticesOffset = ReadInt(d, 0x24);
        var trianglesOffset = ReadInt(d, 0x2C);
        if (numTriangles < 0 || numVertices < 0 || numMaterials is < 0 or > 8)
        {
            throw new InvalidDataException($"{name}: unexpected vcqtc counts t={numTriangles} v={numVertices} m={numMaterials}");
        }

        var magicOffset = verticesOffset - 48;
        if (magicOffset < 0x34 || Encoding.ASCII.GetString(d, magicOffset, 4) != "VCQT")
        {
            throw new InvalidDataException($"{name}: VCQT block not found before vertices");
        }

        var offset = ReadVector3(d, verticesOffset - 32);
        var scale = ReadVector3(d, verticesOffset - 16);

        var materials = new string[numMaterials];
        for (var i = 0; i < numMaterials; i++)
        {
            materials[i] = Encoding.ASCII.GetString(d, 0x34 + i * 4, 4);
        }

        var vertices = new Vector3[numVertices];
        for (var i = 0; i < numVertices; i++)
        {
            var v = ReadVector3(d, verticesOffset + i * 16);
            vertices[i] = new Vector3(v.X * scale.X + offset.X, v.Y, v.Z * scale.Z + offset.Z);
        }

        var triangles = new List<GroundTriangle>(numTriangles);
        for (var i = 0; i < numTriangles; i++)
        {
            var p = trianglesOffset + i * 4;
            int b0 = d[p], b1 = d[p + 1];
            var v0 = (b0 << 2) | (b1 >> 6);
            var flags = (b1 >> 3) & 0x7;
            var material = b1 & 0x7;
            var v1 = v0 + d[p + 2];
            var v2 = v0 + d[p + 3];
            if (v2 >= numVertices || v1 >= numVertices || material >= numMaterials)
            {
                throw new InvalidDataException($"{name}: triangle {i} out of range (v={v0},{v1},{v2} m={material})");
            }

            triangles.Add(new GroundTriangle(vertices[v0], vertices[v1], vertices[v2], materials[material], flags));
        }

        return new GroundCell(name, boundsMin, boundsMax, triangles);
    }

    /// <summary>Highest ground surface Y under (x, z), or null if no triangle covers that point.</summary>
    public static float? HeightAt(IEnumerable<GroundCell> cells, float x, float z)
    {
        float? best = null;
        foreach (var cell in cells)
        {
            if (x < cell.BoundsMin.X || x > cell.BoundsMax.X || z < cell.BoundsMin.Z || z > cell.BoundsMax.Z)
            {
                continue;
            }

            foreach (var t in cell.Triangles)
            {
                if (TryInterpolateY(t, x, z, out var y) && (best is null || y > best))
                {
                    best = y;
                }
            }
        }

        return best;
    }

    private static bool TryInterpolateY(in GroundTriangle t, float x, float z, out float y)
    {
        y = 0;
        var d = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
        if (MathF.Abs(d) < 1e-9f)
        {
            return false;
        }

        var u = ((t.B.Z - t.C.Z) * (x - t.C.X) + (t.C.X - t.B.X) * (z - t.C.Z)) / d;
        var v = ((t.C.Z - t.A.Z) * (x - t.C.X) + (t.A.X - t.C.X) * (z - t.C.Z)) / d;
        var w = 1 - u - v;
        if (u < -1e-5f || v < -1e-5f || w < -1e-5f)
        {
            return false;
        }

        y = u * t.A.Y + v * t.B.Y + w * t.C.Y;
        return true;
    }

    private static int ReadInt(byte[] d, int offset) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(offset));

    private static Vector3 ReadVector3(byte[] d, int offset) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(offset)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(offset + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(offset + 8)));
}
