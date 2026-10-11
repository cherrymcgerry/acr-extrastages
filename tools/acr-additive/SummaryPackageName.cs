using System.Buffers.Binary;
using System.Text;

namespace AcrAdditive;

/// Rewrites FPackageFileSummary.PackageName of a saved legacy header and shifts every absolute offset behind it
/// (summary offset fields, export serial offsets, BulkDataStartOffset, payload TOC). Field order as in LegacyPackage.
internal static class SummaryPackageName
{
    public static string Read(byte[] h) => Parse(h).name;

    static (string name, int nameAt, int nameEnd, List<int> off32, List<int> off64, int exportCountPos, int exportOffsetPos) Parse(byte[] h)
    {
        int p = 0;
        int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(p)); p += 4; return v; }
        string FStr()
        {
            int len = I32();
            if (len == 0) return "";
            string s = len > 0 ? Encoding.Latin1.GetString(h, p, len - 1) : Encoding.Unicode.GetString(h, p, -len * 2 - 2);
            p += len > 0 ? len : -len * 2;
            return s;
        }
        var off32 = new List<int>();
        var off64 = new List<int>();
        void Off() { off32.Add(p); p += 4; }
        void Cnt() { p += 4; }

        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x9E2A83C1) throw new InvalidDataException("not a package");
        p = 4;
        if (I32() != -9) throw new NotSupportedException("expected legacy file version -9 (UE 5.6)");
        p += 16 + 20;                                 // UE3/UE4/UE5/licensee versions, saved hash
        Off();                                        // TotalHeaderSize
        int customVersions = I32(); p += customVersions * 20;
        int nameAt = p;
        var name = FStr();
        int nameEnd = p;
        p += 4;                                       // package flags
        Cnt(); int nameOffsetPos = p; Off();          // names
        Cnt(); Off();                                 // soft object paths
        Cnt(); Off();                                 // gatherable text data
        int exportCountPos = p; Cnt(); int exportOffsetPos = p; Off();
        Cnt(); Off();                                 // imports
        Cnt(); Off(); Cnt(); Off();                   // cell exports, cell imports
        Off(); Off();                                 // metadata, depends
        Cnt(); Off();                                 // soft package references
        Off(); Off();                                 // searchable names, thumbnail table
        int generations = I32(); p += generations * 8;
        for (int e = 0; e < 2; e++) { p += 10; FStr(); }
        p += 12;                                      // compression flags, compressed chunks, package source
        int additional = I32(); for (int a = 0; a < additional; a++) FStr();
        Off();                                        // asset registry data
        off64.Add(p); p += 8;                         // bulk data start
        Off();                                        // world tile info
        int chunkIds = I32(); p += chunkIds * 4;
        Cnt(); Off();                                 // preload dependencies
        Cnt();                                        // names referenced from export data
        off64.Add(p); p += 8;                         // payload TOC
        Off();                                        // data resources
        int namesOffset = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(nameOffsetPos));
        if (p != namesOffset) throw new InvalidDataException($"summary parse ended at {p}, names at {namesOffset}");
        return (name, nameAt, nameEnd, off32, off64, exportCountPos, exportOffsetPos);
    }

    public static byte[] Rewrite(byte[] h, string newName)
    {
        var (_, nameAt, nameEnd, off32, off64, exportCountPos, exportOffsetPos) = Parse(h);
        bool ascii = newName.All(c => c < 128);
        var bytes = ascii ? Encoding.ASCII.GetBytes(newName + "\0") : Encoding.Unicode.GetBytes(newName + "\0");
        int len = ascii ? bytes.Length : -(bytes.Length / 2);
        int delta = 4 + bytes.Length - (nameEnd - nameAt);

        var o = new byte[h.Length + delta];
        h.AsSpan(0, nameAt).CopyTo(o);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(nameAt), len);
        bytes.CopyTo(o, nameAt + 4);
        h.AsSpan(nameEnd).CopyTo(o.AsSpan(nameEnd + delta));
        int At(int pos) => pos >= nameEnd ? pos + delta : pos;

        foreach (var f in off32)
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(f));
            if (v > 0 && v >= nameEnd) BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(At(f)), v + delta);
        }
        foreach (var f in off64)
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(f));
            if (v > 0) BinaryPrimitives.WriteInt64LittleEndian(o.AsSpan(At(f)), v + delta);
        }
        int exportCount = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(exportCountPos));
        int exportOffset = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(exportOffsetPos)) + delta;
        for (int e = 0; e < exportCount; e++)
        {
            int at = exportOffset + e * 96 + 36;
            BinaryPrimitives.WriteInt64LittleEndian(o.AsSpan(at), BinaryPrimitives.ReadInt64LittleEndian(o.AsSpan(at)) + delta);
        }
        return o;
    }
}
