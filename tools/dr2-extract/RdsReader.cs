using System.Buffers.Binary;
using System.Numerics;
using EgoEngineLibrary.Graphics.Pssg.Elements;

namespace Dr2Extract;

/// <summary>
/// Minimal PSSG RENDERDATASOURCE reader (big-endian). Unlike EgoEngineLibrary's RenderDataSourceReader it accepts
/// DR2 terrain streams: <c>uint_color_argb</c> ST streams (packed per-vertex data, not UVs) are ignored, and
/// <c>uint_color_argb</c> Color streams are decoded as RGBA.
/// </summary>
internal sealed class RdsReader
{
    private sealed record Attribute(string DataType, uint Offset, uint Stride, byte[] Data);

    private readonly string _indexFormat;
    private readonly byte[] _indices;
    private readonly Attribute? _position;
    private readonly Attribute? _normal;
    private readonly Attribute? _color;
    private readonly List<Attribute> _uvSets = [];
    private readonly List<(string RenderType, Attribute Attr)> _all = [];

    public int IndexCount { get; }

    /// <summary>Every stream as (render type, data type), in declaration order.</summary>
    public IEnumerable<(string RenderType, string DataType)> Layout => _all.Select(a => (a.RenderType, a.Attr.DataType));

    public bool HasNormals => _normal is not null;

    public bool HasColors => _color is not null;

    public int UvSetCount => _uvSets.Count;

    public RdsReader(PssgRenderDataSource rds)
    {
        var index = rds.IndexSource ?? throw new InvalidDataException($"RDS {rds.Id} has no RENDERINDEXSOURCE");
        if (index.Primitive != "triangles")
        {
            throw new NotSupportedException($"RDS {rds.Id}: primitive {index.Primitive}");
        }

        _indexFormat = index.Format;
        _indices = index.Data.Value;
        IndexCount = (int)index.Count;
        foreach (var stream in rds.Streams)
        {
            var block = stream.GetDataBlock();
            var desc = block.Streams.ElementAt((int)stream.SubStream);
            var attr = new Attribute(desc.DataType, desc.Offset, desc.Stride, block.Data.Value);
            _all.Add((desc.RenderType, attr));
            switch (desc.RenderType)
            {
                case "Vertex":
                    _position = attr;
                    break;
                case "Normal":
                    _normal = attr;
                    break;
                case "Color":
                    _color ??= attr;
                    break;
                case "ST":
                    switch (desc.DataType)
                    {
                        case "float2" or "half2":
                            _uvSets.Add(attr);
                            break;
                        case "float4" or "half4":
                            var half = desc.DataType == "half4";
                            _uvSets.Add(attr with { DataType = half ? "half2" : "float2" });
                            _uvSets.Add(attr with { DataType = half ? "half2" : "float2", Offset = attr.Offset + (half ? 4u : 8u) });
                            break;
                    }

                    break;
            }
        }

        if (_position is null)
        {
            throw new InvalidDataException($"RDS {rds.Id} has no Vertex stream");
        }
    }

    public IEnumerable<(uint A, uint B, uint C)> Triangles()
    {
        var stride = _indexFormat switch
        {
            "ushort" => 2,
            "uint" => 4,
            _ => throw new NotSupportedException($"index format {_indexFormat}"),
        };
        for (var i = 0; i + 2 < IndexCount; i += 3)
        {
            yield return (Index(i, stride), Index(i + 1, stride), Index(i + 2, stride));
        }
    }

    private uint Index(int i, int stride) => stride == 2
        ? BinaryPrimitives.ReadUInt16BigEndian(_indices.AsSpan(i * 2))
        : BinaryPrimitives.ReadUInt32BigEndian(_indices.AsSpan(i * 4));

    public Vector3 Position(uint v) => ReadVector3(_position!, v);

    public Vector3 Normal(uint v) => _normal is null ? Vector3.Zero : ReadVector3(_normal, v);

    public Vector2 Uv(uint v, int set)
    {
        if (set >= _uvSets.Count)
        {
            return Vector2.Zero;
        }

        var a = _uvSets[set];
        var s = Span(a, v);
        return a.DataType == "half2"
            ? new Vector2((float)BinaryPrimitives.ReadHalfBigEndian(s), (float)BinaryPrimitives.ReadHalfBigEndian(s[2..]))
            : new Vector2(BinaryPrimitives.ReadSingleBigEndian(s), BinaryPrimitives.ReadSingleBigEndian(s[4..]));
    }

    public Vector4 Color(uint v)
    {
        if (_color is null)
        {
            return Vector4.One;
        }

        var s = Span(_color, v);
        return _color.DataType switch
        {
            "uint_color_argb" => new Vector4(s[1], s[2], s[3], s[0]) / 255f,
            "float4" => new Vector4(BinaryPrimitives.ReadSingleBigEndian(s), BinaryPrimitives.ReadSingleBigEndian(s[4..]),
                BinaryPrimitives.ReadSingleBigEndian(s[8..]), BinaryPrimitives.ReadSingleBigEndian(s[12..])),
            _ => throw new NotSupportedException($"Color data type {_color.DataType}"),
        };
    }

    /// <summary>
    /// Raw element <paramref name="v"/> of the <paramref name="ordinal"/>-th stream of <paramref name="renderType"/>
    /// as four values: floats/halves as is, <c>uint_color_argb</c> as its bytes in stored order (A R G B, 0..255).
    /// </summary>
    public Vector4 Raw(string renderType, int ordinal, uint v)
    {
        var a = _all.Where(x => x.RenderType == renderType).ElementAt(ordinal).Attr;
        var s = Span(a, v);
        return a.DataType switch
        {
            "uint_color_argb" => new Vector4(s[0], s[1], s[2], s[3]),
            "half4" => new Vector4((float)BinaryPrimitives.ReadHalfBigEndian(s), (float)BinaryPrimitives.ReadHalfBigEndian(s[2..]),
                (float)BinaryPrimitives.ReadHalfBigEndian(s[4..]), (float)BinaryPrimitives.ReadHalfBigEndian(s[6..])),
            "half2" => new Vector4((float)BinaryPrimitives.ReadHalfBigEndian(s), (float)BinaryPrimitives.ReadHalfBigEndian(s[2..]), 0, 0),
            "float2" => new Vector4(BinaryPrimitives.ReadSingleBigEndian(s), BinaryPrimitives.ReadSingleBigEndian(s[4..]), 0, 0),
            "float4" => new Vector4(BinaryPrimitives.ReadSingleBigEndian(s), BinaryPrimitives.ReadSingleBigEndian(s[4..]),
                BinaryPrimitives.ReadSingleBigEndian(s[8..]), BinaryPrimitives.ReadSingleBigEndian(s[12..])),
            _ => throw new NotSupportedException($"raw {renderType} {a.DataType}"),
        };
    }

    private static Span<byte> Span(Attribute a, uint v) => a.Data.AsSpan((int)(a.Stride * v + a.Offset));

    private static Vector3 ReadVector3(Attribute a, uint v)
    {
        var s = Span(a, v);
        return a.DataType switch
        {
            "float3" or "float4" => new Vector3(BinaryPrimitives.ReadSingleBigEndian(s), BinaryPrimitives.ReadSingleBigEndian(s[4..]),
                BinaryPrimitives.ReadSingleBigEndian(s[8..])),
            "half4" or "half3" => new Vector3((float)BinaryPrimitives.ReadHalfBigEndian(s), (float)BinaryPrimitives.ReadHalfBigEndian(s[2..]),
                (float)BinaryPrimitives.ReadHalfBigEndian(s[4..])),
            _ => throw new NotSupportedException($"vector data type {a.DataType}"),
        };
    }
}
