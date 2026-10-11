using System.Buffers.Binary;

namespace AcrRef;

/// FUnversionedHeader read/write (UE 5.x unversioned property serialization).
/// Fragment = uint16: skip (7 bits) | hasZero 0x80 | isLast 0x100 | valueNum (7 bits) << 9,
/// followed by a zero mask over the values of fragments that have hasZero set.
internal static class Unv
{
    public readonly record struct Slot(int Index, bool Zero);

    public static List<Slot> ReadHeader(byte[] d, ref int p)
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
        var mask = new bool[zeroCount];
        if (zeroCount > 0)
        {
            int bytes = zeroCount <= 8 ? 1 : zeroCount <= 16 ? 2 : (zeroCount + 31) / 32 * 4;
            for (int b = 0; b < zeroCount; b++) mask[b] = ((d[p + b / 8] >> (b % 8)) & 1) != 0;
            p += bytes;
        }
        var result = new List<Slot>();
        int bit = 0;
        foreach (var f in frags)
            for (int i = 0; i < f.num; i++)
                result.Add(new Slot(f.first + i, f.zeroes && mask[bit++]));
        return result;
    }

    public static void WriteHeader(BinaryWriter w, IReadOnlyList<Slot> slots)
    {
        var frags = new List<(int skip, int start, int num, bool hasZero)>();
        int prevEnd = 0, i = 0;
        while (i < slots.Count)
        {
            int skip = slots[i].Index - prevEnd;
            if (skip < 0) throw new ArgumentException("slots must be sorted and unique");
            while (skip > 127) { frags.Add((127, i, 0, false)); skip -= 127; }
            int j = i;
            while (j + 1 < slots.Count && slots[j + 1].Index == slots[j].Index + 1 && j + 1 - i < 127) j++;
            bool hz = false;
            for (int k = i; k <= j; k++) hz |= slots[k].Zero;
            frags.Add((skip, i, j - i + 1, hz));
            prevEnd = slots[j].Index + 1;
            i = j + 1;
        }
        if (frags.Count == 0) frags.Add((0, 0, 0, false));
        var bits = new List<bool>();
        for (int f = 0; f < frags.Count; f++)
        {
            var (skip, start, num, hz) = frags[f];
            w.Write((ushort)(skip | (hz ? 0x80 : 0) | (f == frags.Count - 1 ? 0x100 : 0) | (num << 9)));
            if (hz) for (int k = start; k < start + num; k++) bits.Add(slots[k].Zero);
        }
        if (bits.Count == 0) return;
        int bytes = bits.Count <= 8 ? 1 : bits.Count <= 16 ? 2 : (bits.Count + 31) / 32 * 4;
        var mask = new byte[bytes];
        for (int b = 0; b < bits.Count; b++) if (bits[b]) mask[b / 8] |= (byte)(1 << (b % 8));
        w.Write(mask);
    }

    public static bool AllZero(ReadOnlySpan<byte> s)
    {
        foreach (var b in s) if (b != 0) return false;
        return true;
    }
}
