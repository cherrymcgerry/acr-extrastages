namespace AcrRef;

/// BakedSimpleCollisionData: TArray&lt;FSimpleColliderAggregatedData { Boxes, Spheres, Capsules, PhysMaterial, Owner }&gt;
/// with FKBoxElem / FKSphereElem / FKSphylElem (UE units, cm, Z up).
internal static class SimpleColliders
{
    public sealed class Info
    {
        public int Entries, Boxes, Spheres, Capsules;
        public List<int> CenterAt = new();   // byte offset of each shape's FVector Center
        public int End;
    }

    public static Info Parse(byte[] d, int start, int boolSize = 1)
    {
        var info = new Info();
        int p = start;
        int n = BitConverter.ToInt32(d, p); p += 4;
        for (int i = 0; i < n; i++)
        {
            info.Entries++;
            foreach (var s in Unv.ReadHeader(d, ref p))
            {
                if (s.Zero) continue;
                switch (s.Index)
                {
                    case 0: info.Boxes += Shapes(d, ref p, info, [24, 24, 4, 4, 4, 4, 8, boolSize, 1]); break;
                    case 1: info.Spheres += Shapes(d, ref p, info, [24, 4, 4, 8, boolSize, 1]); break;
                    case 2: info.Capsules += Shapes(d, ref p, info, [24, 24, 4, 4, 4, 8, boolSize, 1]); break;
                    case 3: p += 4; break;
                    case 4:
                        p += 16;
                        int len = BitConverter.ToInt32(d, p); p += 4;
                        p += len >= 0 ? len : -len * 2;
                        break;
                    default: throw new InvalidDataException($"simple collider property {s.Index}");
                }
            }
        }
        info.End = p;
        return info;
    }

    /// Shape arrays: slot 0 is always the FVector Center; sizes[] gives each slot's value size.
    static int Shapes(byte[] d, ref int p, Info info, int[] sizes)
    {
        int n = BitConverter.ToInt32(d, p); p += 4;
        for (int i = 0; i < n; i++)
            foreach (var s in Unv.ReadHeader(d, ref p))
            {
                if (s.Zero) continue;
                if (s.Index >= sizes.Length) throw new InvalidDataException($"shape property {s.Index}");
                if (s.Index == 0) info.CenterAt.Add(p);
                p += sizes[s.Index];
            }
        return n;
    }

    /// Parses with 1-byte bools, falling back to bools carried only by the zero mask.
    public static Info ParseExact(byte[] d, int start, int expectedEnd)
    {
        foreach (var boolSize in new[] { 1, 0 })
        {
            try
            {
                var info = Parse(d, start, boolSize);
                if (info.End == expectedEnd) return info;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        }
        throw new InvalidDataException("could not parse BakedSimpleCollisionData to the expected end");
    }
}
