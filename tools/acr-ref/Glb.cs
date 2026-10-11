using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace AcrRef;

/// Minimal binary glTF reader: triangle primitives with POSITION and indices, by material name.
/// Node transforms are not applied (stage IR glbs have them baked into the vertices).
internal sealed class Glb
{
    public JsonNode Json { get; }
    readonly byte[] _bin;

    public sealed record Primitive(string Mesh, string Material, float[] Positions, uint[] Indices)
    {
        /// TEXCOORD_n (2 floats per vertex), null if absent; filled by Primitives(withUv: true).
        public float[]?[] Uv { get; init; } = [];
        /// COLOR_0 (4 floats per vertex, 0..1), null if absent; filled by Primitives(withUv: true).
        public float[]? Color { get; init; }
    }

    public Glb(string path)
    {
        var d = File.ReadAllBytes(path);
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x46546C67) throw new InvalidDataException($"{path} is not a glb");
        int p = 12;
        JsonNode? json = null;
        byte[]? bin = null;
        while (p + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4));
            if (type == 0x4E4F534A) json = JsonNode.Parse(Encoding.UTF8.GetString(d, p + 8, len));
            else if (type == 0x004E4942) bin = d.AsSpan(p + 8, len).ToArray();
            p += 8 + len;
        }
        Json = json ?? throw new InvalidDataException("glb has no JSON chunk");
        _bin = bin ?? [];
    }

    public IEnumerable<Primitive> Primitives(bool withUv = false)
    {
        var materials = Json["materials"]?.AsArray();
        foreach (var mesh in Json["meshes"]!.AsArray())
            foreach (var prim in mesh!["primitives"]!.AsArray())
            {
                int mode = prim!["mode"]?.GetValue<int>() ?? 4;
                if (mode != 4) throw new NotSupportedException($"primitive mode {mode}");
                var mat = prim["material"] is { } m && materials is not null ? materials[m.GetValue<int>()]!["name"]!.GetValue<string>() : "";
                var pos = ReadFloats(prim["attributes"]!["POSITION"]!.GetValue<int>(), 3);
                uint[] idx = prim["indices"] is { } ia ? ReadIndices(ia.GetValue<int>())
                                                       : Enumerable.Range(0, pos.Length / 3).Select(i => (uint)i).ToArray();
                var uv = new float[]?[withUv ? 2 : 0];
                for (int k = 0; k < uv.Length; k++)
                    if (prim["attributes"]![$"TEXCOORD_{k}"] is { } ta) uv[k] = ReadFloats(ta.GetValue<int>(), 2);
                float[]? col = withUv && prim["attributes"]!["COLOR_0"] is { } ca && Json["accessors"]![ca.GetValue<int>()]!["type"]!.GetValue<string>() == "VEC4"
                    ? ReadFloats(ca.GetValue<int>(), 4) : null;
                yield return new Primitive(mesh["name"]?.GetValue<string>() ?? "", mat, pos, idx) { Uv = uv, Color = col };
            }
    }

    (int offset, int stride, int count) View(int accessor, int elemSize)
    {
        var a = Json["accessors"]![accessor]!;
        var v = Json["bufferViews"]![a["bufferView"]!.GetValue<int>()]!;
        int offset = (v["byteOffset"]?.GetValue<int>() ?? 0) + (a["byteOffset"]?.GetValue<int>() ?? 0);
        int stride = v["byteStride"]?.GetValue<int>() ?? elemSize;
        return (offset, stride, a["count"]!.GetValue<int>());
    }

    float[] ReadFloats(int accessor, int comps)
    {
        var a = Json["accessors"]![accessor]!;
        int type = a["componentType"]!.GetValue<int>();
        bool norm = a["normalized"]?.GetValue<bool>() ?? false;
        int size = type switch { 5126 => 4, 5123 or 5122 => 2, 5121 or 5120 => 1, _ => throw new NotSupportedException($"component type {type}") };
        if (type != 5126 && !norm) throw new NotSupportedException($"non-normalized integer attribute (type {type})");
        var (off, stride, count) = View(accessor, comps * size);
        var r = new float[count * comps];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < comps; c++)
            {
                var s = _bin.AsSpan(off + i * stride + c * size);
                r[i * comps + c] = type switch
                {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f,
                    5122 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1),
                    5121 => s[0] / 255f,
                    _ => Math.Max((sbyte)s[0] / 127f, -1),
                };
            }
        return r;
    }

    uint[] ReadIndices(int accessor)
    {
        int type = Json["accessors"]![accessor]!["componentType"]!.GetValue<int>();
        int size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new NotSupportedException($"index type {type}") };
        var (off, stride, count) = View(accessor, size);
        var r = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int at = off + i * stride;
            r[i] = size switch { 1 => _bin[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(_bin.AsSpan(at)), _ => BinaryPrimitives.ReadUInt32LittleEndian(_bin.AsSpan(at)) };
        }
        return r;
    }
}
