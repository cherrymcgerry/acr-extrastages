using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace AcrRef;

/// Editor for legacy (retoc to-legacy) UBakedDataContainer packages with unversioned properties.
///
/// Export layout (verified on DA_WalesS4HafrenSouthBakedData, UE 5.6):
///   class header (present: BakedMeshCollisionData, BakedSimpleCollisionData, DataLayerTo{Mesh,Simple}CollisionDataIndices)
///   int32 N, N x FMeshColliderData { hdr, FTransform{hdr, quat d4, vec d3, vec d3}, Positions f32[], Colors f32[], Indices u32[],
///                                    PhysMaterial FPackageIndex, Owner FSoftObjectPath{FName pkg, FName asset, FString sub} }
///   BakedSimpleCollisionData (kept raw)
///   TMap&lt;FName, FCollidersStrides&gt; mesh map, TMap&lt;FName, FCollidersStrides&gt; simple map
///   int32 0 (no object GUID), uint32 package tag
/// Physics space: metres, Y up; physics (x, y, z) = UE (X, Z, Y) / 100.
internal static class BakedEdit
{
    const int SerialSizeOffset = 2985;
    const int BulkDataStartOffset = 280;

    sealed class Collider
    {
        public int Start, End, PositionsAt, PhysMatAt, OwnerAt;
        public float[] Positions = [];
        public uint[] Indices = [];
        public int PhysMaterial;
        public Vector3 Min, Max;
    }

    sealed class Stride { public uint Index, Num; }
    sealed class MapEntry { public int NameIndex, NameNumber; public List<Stride> Strides = new(); }

    public static int Run(Options o)
    {
        string Need(string k) => o.Named.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");
        var uexpPath = Need("uexp");
        var uassetPath = Path.ChangeExtension(uexpPath, ".uasset");
        var outDir = o.Out ?? throw new ArgumentException("--out <dir> is required");
        var refIdx = int.Parse(Need("ref-collider"));
        var at = ParseVec(Need("at"));          // physics x,z of the ramp's leading-edge centre
        var dir = ParseVec(Need("dir"));        // physics x,z driving direction
        float width = F(o.Named.GetValueOrDefault("width", "5")), length = F(o.Named.GetValueOrDefault("length", "8")),
              rise = F(o.Named.GetValueOrDefault("rise", "1")), sink = F(o.Named.GetValueOrDefault("sink", "0.05"));

        var data = File.ReadAllBytes(uexpPath);
        var (colliders, countAt, simpleStart) = ParseColliders(data);
        var (mapStart, meshMap, simpleMapStart) = LocateMaps(data, simpleStart);
        Console.WriteLine($"parsed {colliders.Count} mesh colliders, simple colliders {simpleStart}..{mapStart}, mesh map {mapStart}..{simpleMapStart}, {meshMap.Count} data-layer keys");
        if (!WriteMap(meshMap).AsSpan().SequenceEqual(data.AsSpan(mapStart, simpleMapStart - mapStart)))
            throw new InvalidDataException("stride-map writer does not reproduce the original bytes");

        var reference = colliders[refIdx];
        var groundSet = colliders.Where(c => c.PhysMaterial == reference.PhysMaterial).ToList();

        var fwd = Vector2.Normalize(new Vector2(dir[0], dir[1]));
        var side = new Vector2(-fwd.Y, fwd.X);
        var lead = new Vector2(at[0], at[1]);
        var corners = new[]
        {
            lead + side * (width / 2), lead - side * (width / 2),
            lead + fwd * length - side * (width / 2), lead + fwd * length + side * (width / 2),
        };
        var verts = new List<Vector3>();
        for (int i = 0; i < 4; i++)
        {
            var g = GroundHeight(groundSet, corners[i]) ?? GroundHeight(colliders, corners[i])
                ?? throw new InvalidOperationException($"no ground under ramp corner {corners[i]}");
            var y = i < 2 ? g - sink : g + rise;
            verts.Add(new Vector3(corners[i].X, y, corners[i].Y));
            Console.WriteLine($"corner {i}: x={corners[i].X:0.###} z={corners[i].Y:0.###} ground={g:0.###} -> y={y:0.###}");
        }
        // both windings so the quad is solid regardless of the solver's face orientation
        uint[] tris = [0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2];

        var newCollider = BuildCollider(data, reference, verts, tris);
        var newIndex = (uint)colliders.Count;
        var none = meshMap.FirstOrDefault(e => colliders.Count > 0 && e.Strides.Any(s => s.Index == 0))
                   ?? throw new InvalidOperationException("could not identify the always-active (None) stride set");
        none.Strides.Add(new Stride { Index = newIndex, Num = 1 });

        using var ms = new MemoryStream();
        ms.Write(data, 0, countAt);
        ms.Write(BitConverter.GetBytes(colliders.Count + 1));
        ms.Write(data, countAt + 4, simpleStart - countAt - 4);
        ms.Write(newCollider);
        ms.Write(data, simpleStart, mapStart - simpleStart);
        ms.Write(WriteMap(meshMap));
        ms.Write(data, simpleMapStart, data.Length - simpleMapStart);
        var outUexp = ms.ToArray();

        var header = File.ReadAllBytes(uassetPath);
        long delta = outUexp.LongLength - data.LongLength;
        PatchInt64(header, SerialSizeOffset, data.LongLength - 4, delta);
        PatchInt64(header, BulkDataStartOffset, header.LongLength + data.LongLength - 4, delta);

        Directory.CreateDirectory(outDir);
        var stem = Path.Combine(outDir, Path.GetFileNameWithoutExtension(uexpPath));
        File.WriteAllBytes(stem + ".uexp", outUexp);
        File.WriteAllBytes(stem + ".uasset", header);

        var (check, _, _) = ParseColliders(outUexp);
        var (_, checkMap, _) = LocateMaps(outUexp, check[^1].End);
        if (check.Count != colliders.Count + 1 || !checkMap.Any(e => e.Strides.Any(s => s.Index == newIndex)))
            throw new InvalidOperationException("re-parse of the edited package failed validation");
        Console.WriteLine($"appended collider #{newIndex} (material import {reference.PhysMaterial}, owner of #{refIdx}); uexp {data.Length} -> {outUexp.Length} bytes -> {stem}.uexp");
        return 0;
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    static float[] ParseVec(string s) => s.Split(',').Select(F).ToArray();

    static void PatchInt64(byte[] buf, int offset, long expected, long delta)
    {
        var cur = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset));
        if (cur != expected) throw new InvalidOperationException($"uasset field at {offset} is {cur}, expected {expected}; header layout differs from the verified one");
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset), cur + delta);
    }

    // ---- unversioned property header ----

    /// Returns (property index, isZero) for each serialized property slot.
    static List<(int idx, bool zero)> ReadHeader(byte[] d, ref int p)
    {
        var frags = new List<(int first, int num, bool zeroes)>();
        int idx = 0;
        while (true)
        {
            ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)); p += 2;
            idx += packed & 0x7f;
            int num = packed >> 9;
            frags.Add((idx, num, (packed & 0x80) != 0));
            idx += num;
            if ((packed & 0x100) != 0) break;
        }
        int zeroCount = frags.Where(f => f.zeroes).Sum(f => f.num);
        ulong mask = 0;
        if (zeroCount > 0)
        {
            if (zeroCount <= 8) { mask = d[p]; p += 1; }
            else if (zeroCount <= 16) { mask = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)); p += 2; }
            else
            {
                int words = (zeroCount + 31) / 32;
                if (words > 2) throw new NotSupportedException("zero mask wider than 64 bits");
                for (int w = 0; w < words; w++) { mask |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)) << (32 * w); p += 4; }
            }
        }
        var result = new List<(int, bool)>();
        int bit = 0;
        foreach (var f in frags)
            for (int i = 0; i < f.num; i++)
            {
                bool zero = f.zeroes && ((mask >> bit++) & 1) != 0;
                result.Add((f.first + i, zero));
            }
        return result;
    }

    static void WriteHeader(BinaryWriter w, int count, bool[] zero)
    {
        bool any = zero.Any(z => z);
        w.Write((ushort)((count << 9) | 0x100 | (any ? 0x80 : 0)));
        if (!any) return;
        if (count > 8) throw new NotSupportedException();
        byte m = 0;
        for (int i = 0; i < count; i++) if (zero[i]) m |= (byte)(1 << i);
        w.Write(m);
    }

    // ---- mesh colliders ----

    static (List<Collider>, int countAt, int end) ParseColliders(byte[] d)
    {
        int p = 0;
        var cls = ReadHeader(d, ref p);
        if (cls.Count < 1 || cls[0] != (0, false)) throw new InvalidDataException("BakedMeshCollisionData is not the first serialized property");
        int countAt = p;
        int n = BitConverter.ToInt32(d, p); p += 4;
        var list = new List<Collider>(n);
        for (int i = 0; i < n; i++)
        {
            var c = new Collider { Start = p };
            foreach (var (idx, zero) in ReadHeader(d, ref p))
            {
                if (zero) continue;
                switch (idx)
                {
                    case 0:
                        foreach (var (ti, tz) in ReadHeader(d, ref p)) if (!tz) p += ti == 0 ? 32 : 24;
                        break;
                    case 1:
                        c.PositionsAt = p;
                        c.Positions = new float[BitConverter.ToInt32(d, p)]; p += 4;
                        Buffer.BlockCopy(d, p, c.Positions, 0, c.Positions.Length * 4); p += c.Positions.Length * 4;
                        break;
                    case 2: p += 4 + BitConverter.ToInt32(d, p) * 4; break;
                    case 3:
                        c.Indices = new uint[BitConverter.ToInt32(d, p)]; p += 4;
                        Buffer.BlockCopy(d, p, c.Indices, 0, c.Indices.Length * 4); p += c.Indices.Length * 4;
                        break;
                    case 4: c.PhysMatAt = p; c.PhysMaterial = BitConverter.ToInt32(d, p); p += 4; break;
                    case 5:
                        c.OwnerAt = p;
                        p += 16;
                        int len = BitConverter.ToInt32(d, p); p += 4;
                        p += len >= 0 ? len : -len * 2;
                        break;
                    default: throw new InvalidDataException($"unexpected FMeshColliderData property {idx} in collider {i}");
                }
            }
            c.End = p;
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int k = 0; k + 2 < c.Positions.Length; k += 3)
            {
                var v = new Vector3(c.Positions[k], c.Positions[k + 1], c.Positions[k + 2]);
                mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v);
            }
            c.Min = mn; c.Max = mx;
            list.Add(c);
        }
        return (list, countAt, p);
    }

    static byte[] BuildCollider(byte[] d, Collider reference, List<Vector3> verts, uint[] tris)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0x0D00); // 6 properties, none zero
        int p = reference.Start;
        ReadHeader(d, ref p);
        int transformStart = p;
        foreach (var (ti, tz) in ReadHeader(d, ref p)) if (!tz) p += ti == 0 ? 32 : 24;
        w.Write(d, transformStart, p - transformStart);
        if (p != reference.PositionsAt) throw new InvalidDataException("reference collider layout unexpected");
        w.Write(verts.Count * 3);
        foreach (var v in verts) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        int colorsAt = reference.PositionsAt + 4 + reference.Positions.Length * 4;
        int colorsLen = 4 + BitConverter.ToInt32(d, colorsAt) * 4;
        w.Write(d, colorsAt, colorsLen);
        w.Write(tris.Length);
        foreach (var t in tris) w.Write(t);
        w.Write(reference.PhysMaterial);
        w.Write(d, reference.OwnerAt, reference.End - reference.OwnerAt);
        return ms.ToArray();
    }

    /// Height of the highest triangle under (x,z) among the given colliders.
    static float? GroundHeight(IEnumerable<Collider> colliders, Vector2 q)
    {
        float? best = null;
        foreach (var c in colliders)
        {
            if (q.X < c.Min.X || q.X > c.Max.X || q.Y < c.Min.Z || q.Y > c.Max.Z) continue;
            var pos = c.Positions;
            for (int t = 0; t + 2 < c.Indices.Length; t += 3)
            {
                Vector3 V(uint i) => new(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                var a = V(c.Indices[t]); var b = V(c.Indices[t + 1]); var e = V(c.Indices[t + 2]);
                float den = (b.Z - e.Z) * (a.X - e.X) + (e.X - b.X) * (a.Z - e.Z);
                if (MathF.Abs(den) < 1e-9f) continue;
                float l1 = ((b.Z - e.Z) * (q.X - e.X) + (e.X - b.X) * (q.Y - e.Z)) / den;
                float l2 = ((e.Z - a.Z) * (q.X - e.X) + (a.X - e.X) * (q.Y - e.Z)) / den;
                float l3 = 1 - l1 - l2;
                if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                float y = l1 * a.Y + l2 * b.Y + l3 * e.Y;
                if (best is null || y > best) best = y;
            }
        }
        return best;
    }

    // ---- data-layer stride maps ----

    static (int start, List<MapEntry> meshMap, int simpleMapStart) LocateMaps(byte[] d, int searchFrom)
    {
        int tail = d.Length - 8;
        if (BitConverter.ToInt32(d, tail) != 0 || BitConverter.ToUInt32(d, tail + 4) != 0x9E2A83C1)
            throw new InvalidDataException("unexpected export trailer");
        for (int s = Math.Max(searchFrom, tail - (4 << 20)); s < tail; s++)
        {
            if (BitConverter.ToInt32(d, s) != 0) continue;
            int k = BitConverter.ToInt32(d, s + 4);
            if (k is < 1 or > 256) continue;
            try
            {
                int p = s;
                var mesh = ReadMap(d, ref p);
                int simpleAt = p;
                ReadMap(d, ref p);
                if (p == tail) return (s, mesh, simpleAt);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException) { }
        }
        throw new InvalidDataException("could not locate the data-layer stride maps");
    }

    static List<MapEntry> ReadMap(byte[] d, ref int p)
    {
        if (BitConverter.ToInt32(d, p) != 0) throw new InvalidDataException("map has keys to remove");
        int n = BitConverter.ToInt32(d, p + 4); p += 8;
        if (n is < 0 or > 4096) throw new InvalidDataException();
        var list = new List<MapEntry>();
        for (int i = 0; i < n; i++)
        {
            var e = new MapEntry { NameIndex = BitConverter.ToInt32(d, p), NameNumber = BitConverter.ToInt32(d, p + 4) };
            p += 8;
            var h = ReadHeader(d, ref p);
            if (h.Count != 1 || h[0].idx != 0) throw new InvalidDataException();
            if (!h[0].zero)
            {
                if (BitConverter.ToInt32(d, p) != 0) throw new InvalidDataException();
                int m = BitConverter.ToInt32(d, p + 4); p += 8;
                if (m is < 0 or > 1_000_000) throw new InvalidDataException();
                for (int j = 0; j < m; j++)
                {
                    var st = new Stride();
                    foreach (var (idx, zero) in ReadHeader(d, ref p))
                    {
                        uint v = zero ? 0 : BitConverter.ToUInt32(d, p);
                        if (!zero) p += 4;
                        if (idx == 0) st.Index = v; else if (idx == 1) st.Num = v; else throw new InvalidDataException();
                    }
                    e.Strides.Add(st);
                }
            }
            list.Add(e);
        }
        return list;
    }

    static byte[] WriteMap(List<MapEntry> map)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0); w.Write(map.Count);
        foreach (var e in map)
        {
            w.Write(e.NameIndex); w.Write(e.NameNumber);
            WriteHeader(w, 1, [e.Strides.Count == 0]);
            if (e.Strides.Count == 0) continue;
            w.Write(0); w.Write(e.Strides.Count);
            foreach (var s in e.Strides)
            {
                WriteHeader(w, 2, [s.Index == 0, s.Num == 0]);
                if (s.Index != 0) w.Write(s.Index);
                if (s.Num != 0) w.Write(s.Num);
            }
        }
        return ms.ToArray();
    }
}
