using System.Buffers.Binary;
using System.Text;
using AcrRef;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;

namespace AcrAdditive;

/// A cooked UDataTable (legacy package, unversioned properties) split into rows whose top-level properties are kept as
/// raw value bytes. Rows can be copied, edited property by property and appended; untouched rows are written back
/// byte for byte. The walker sizes values from the Dumper-7 usmap schema.
///
/// Export layout: DataTable properties, uint32 object-GUID flag, int32 NumRows, then NumRows × (FName key, row struct).
internal sealed class DbTable
{
    public sealed class Value
    {
        public int Index;
        public bool Zero;
        public byte[] Bytes = [];
    }

    public sealed class Row
    {
        public string Key = "";
        public List<Value> Values = new();
        public byte[]? Raw;                          // original bytes (header + values); null once edited

        public Value Get(int index) => Values.Single(v => v.Index == index);
        public void Set(int index, byte[] bytes)
        {
            var v = Values.FirstOrDefault(x => x.Index == index);
            if (v is null) { v = new Value { Index = index }; Values.Add(v); Values.Sort((a, b) => a.Index.CompareTo(b.Index)); }
            v.Zero = false;
            v.Bytes = bytes;
            Raw = null;
        }
        public Row Clone(string key) => new()
        {
            Key = key,
            Values = Values.Select(v => new Value { Index = v.Index, Zero = v.Zero, Bytes = (byte[])v.Bytes.Clone() }).ToList(),
            Raw = null,
        };
    }

    public readonly LegacyPackage Package;
    public readonly string RowStruct;
    public readonly Schema Types;
    byte[] _prefix = [];
    public readonly List<Row> Rows = new();

    public DbTable(string uassetPath, Schema types, string rowStruct)
    {
        Package = new LegacyPackage(uassetPath);
        Types = types;
        RowStruct = rowStruct;
        if (Package.ExportCount != 1) throw new NotSupportedException($"{Package.PackageName}: {Package.ExportCount} exports");
        var d = Package.GetExportData(0);
        int p = 0;
        Types.SkipStruct(d, ref p, "DataTable", Package);
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)) != 0) throw new InvalidDataException("object GUID flag set");
        p += 4;
        _prefix = d[..p];
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        for (int i = 0; i < n; i++)
        {
            var key = Package.NameAt(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)));
            p += 8;
            int start = p;
            var row = new Row { Key = key };
            foreach (var s in Unv.ReadHeader(d, ref p))
            {
                int vs = p;
                if (!s.Zero) Types.SkipProperty(d, ref p, Types.Property(rowStruct, s.Index), Package);
                row.Values.Add(new Value { Index = s.Index, Zero = s.Zero, Bytes = d[vs..p] });
            }
            row.Raw = d[start..p];
            Rows.Add(row);
        }
        if (p != d.Length) throw new InvalidDataException($"{Package.PackageName}: rows end at {p} of {d.Length}");
    }

    public Row this[string key] => Rows.SingleOrDefault(r => r.Key == key) ?? throw new KeyNotFoundException($"{Package.PackageName}: no row {key}");
    public bool Has(string key) => Rows.Any(r => r.Key == key);

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(_prefix);
        w.Write(Rows.Count);
        foreach (var r in Rows)
        {
            var (i, n) = SplitName(r.Key);
            w.Write(Package.AddExportName(i)); w.Write(n);
            if (r.Raw is not null) { w.Write(r.Raw); continue; }
            Unv.WriteHeader(w, r.Values.Select(v => new Unv.Slot(v.Index, v.Zero)).ToList());
            foreach (var v in r.Values) if (!v.Zero) w.Write(v.Bytes);
        }
        return ms.ToArray();
    }

    public void Save(string uassetOut)
    {
        Package.SetExportData(0, Write());
        Package.SaveTo(uassetOut);
    }

    // ---- value builders (names go into the export-data name range) ----

    public static (string, int) SplitName(string s)
    {
        int u = s.LastIndexOf('_');
        if (u > 0 && u < s.Length - 1 && s[(u + 1)..].All(char.IsAsciiDigit) && (s[u + 1] != '0' || u + 2 == s.Length) && s.Length - u - 1 < 10)
            return (s[..u], int.Parse(s[(u + 1)..]) + 1);
        return (s, 0);
    }

    public byte[] FName(string s)
    {
        var (b, n) = SplitName(s);
        var o = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(o, Package.AddExportName(b));
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), n);
        return o;
    }

    public string ReadFName(byte[] d, int at) => Package.NameAt(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4)));

    /// FText with no history and a culture-invariant display string (shown as-is in every language).
    public static byte[] InvariantText(string s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(2u);                 // ETextFlag::CultureInvariant
        w.Write((sbyte)-1);          // ETextHistoryType::None
        w.Write(1u);                 // bHasCultureInvariantString
        WriteFString(w, s);
        return ms.ToArray();
    }

    public static void WriteFString(BinaryWriter w, string s)
    {
        if (s.Length == 0) w.Write(0);
        else if (s.All(c => c < 128)) { w.Write(s.Length + 1); w.Write(Encoding.ASCII.GetBytes(s)); w.Write((byte)0); }
        else { w.Write(-(s.Length + 1)); w.Write(Encoding.Unicode.GetBytes(s)); w.Write((short)0); }
    }

    /// FDBReference {Table, Row} as an unversioned struct.
    public byte[] DbRef(string table, string row)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        Unv.WriteHeader(w, [new(0, false), new(1, false)]);
        w.Write(FName(table)); w.Write(FName(row));
        return ms.ToArray();
    }

    public (string table, string row) ReadDbRef(byte[] d, ref int p)
    {
        string table = "None", row = "None";
        foreach (var s in Unv.ReadHeader(d, ref p))
        {
            if (s.Zero) continue;
            var n = ReadFName(d, p); p += 8;
            if (s.Index == 0) table = n; else row = n;
        }
        return (table, row);
    }

    /// TSet/TArray of FDBReference: (int32 removed = 0 for sets), int32 count, elements.
    public List<(string table, string row)> ReadDbRefs(byte[] d, bool isSet)
    {
        int p = 0;
        if (isSet && BinaryPrimitives.ReadInt32LittleEndian(d) != 0) throw new InvalidDataException("set with removed elements");
        if (isSet) p += 4;
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        var res = new List<(string, string)>();
        for (int i = 0; i < n; i++) res.Add(ReadDbRef(d, ref p));
        if (p != d.Length) throw new InvalidDataException("DBReference list has trailing bytes");
        return res;
    }

    public byte[] DbRefs(IEnumerable<(string table, string row)> refs, bool isSet)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var l = refs.ToList();
        if (isSet) w.Write(0);
        w.Write(l.Count);
        foreach (var (t, r) in l) w.Write(DbRef(t, r));
        return ms.ToArray();
    }

    /// FSoftObjectPath {FTopLevelAssetPath(package, asset), FString subpath} (no soft-path table in these packages).
    public byte[] SoftPath(string objectPath)
    {
        int dot = objectPath.LastIndexOf('.');
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(FName(objectPath[..dot])); w.Write(FName(objectPath[(dot + 1)..]));
        WriteFString(w, "");
        return ms.ToArray();
    }
}

/// Unversioned value sizes from the usmap schema (only the property kinds that occur in ACR database rows).
internal sealed class Schema
{
    readonly TypeMappings _m;
    public Schema(string usmap) => _m = new FileUsmapTypeMappingsProvider(usmap, StringComparer.OrdinalIgnoreCase).MappingsForGame!;

    /// Property at an unversioned index: own properties first, then each super's (see acr-ref schema).
    public PropertyType Property(string type, int index)
    {
        if (!_m.Types.TryGetValue(type, out var s)) throw new KeyNotFoundException($"type {type} not in mappings");
        int baseIndex = 0;
        for (var cur = s; cur is not null; cur = cur.Super?.Value)
        {
            foreach (var (i, p) in cur.Properties)
                if (index >= baseIndex + i && index < baseIndex + i + Math.Max(1, (int)p.ArraySize)) return p.MappingType;
            baseIndex += cur.PropertyCount;
        }
        throw new KeyNotFoundException($"{type} has no property index {index}");
    }

    public int IndexOf(string type, string name)
    {
        var s = _m.Types[type];
        int baseIndex = 0;
        for (var cur = s; cur is not null; cur = cur.Super?.Value)
        {
            foreach (var (i, p) in cur.Properties) if (p.Name == name) return baseIndex + i;
            baseIndex += cur.PropertyCount;
        }
        throw new KeyNotFoundException($"{type}.{name}");
    }

    static readonly Dictionary<string, int> NativeSizes = new()
    {
        ["Vector2D"] = 16, ["Vector"] = 24, ["Rotator"] = 24, ["Quat"] = 32, ["Vector4"] = 32, ["Guid"] = 16,
        ["Color"] = 4, ["LinearColor"] = 16, ["IntPoint"] = 8, ["IntVector"] = 12, ["DateTime"] = 8, ["Timespan"] = 8,
    };

    public void SkipStruct(byte[] d, ref int p, string type, LegacyPackage pkg)
    {
        if (NativeSizes.TryGetValue(type, out var n)) { p += n; return; }
        switch (type)
        {
            case "GameplayTagContainer": { int c = I32(d, ref p); p += c * 8; return; }
            case "GameplayTag": p += 8; return;
            case "SoftObjectPath": p += 16; SkipFString(d, ref p); return;
        }
        foreach (var s in Unv.ReadHeader(d, ref p))
            if (!s.Zero) SkipProperty(d, ref p, Property(type, s.Index), pkg);
    }

    public void SkipProperty(byte[] d, ref int p, PropertyType t, LegacyPackage pkg)
    {
        switch (t.Type)
        {
            case "BoolProperty": case "Int8Property": p += 1; break;
            case "ByteProperty": p += 1; break;
            case "Int16Property": case "UInt16Property": p += 2; break;
            case "IntProperty": case "UInt32Property": case "FloatProperty": p += 4; break;
            case "Int64Property": case "UInt64Property": case "DoubleProperty": p += 8; break;
            case "NameProperty": p += 8; break;
            case "ObjectProperty": case "ClassProperty": case "InterfaceProperty": p += 4; break;
            case "SoftObjectProperty": case "SoftClassProperty": p += 16; SkipFString(d, ref p); break;
            case "StrProperty": SkipFString(d, ref p); break;
            case "TextProperty": SkipText(d, ref p); break;
            case "EnumProperty":
                if (t.InnerType is null) throw new NotSupportedException("enum without underlying type");
                SkipProperty(d, ref p, t.InnerType, pkg); break;
            case "StructProperty": SkipStruct(d, ref p, t.StructType!, pkg); break;
            case "OptionalProperty":
                { int set = I32(d, ref p); if (set != 0) SkipProperty(d, ref p, t.InnerType!, pkg); break; }
            case "ArrayProperty":
                { int c = I32(d, ref p); for (int i = 0; i < c; i++) SkipProperty(d, ref p, t.InnerType!, pkg); break; }
            case "SetProperty":
                {
                    int rem = I32(d, ref p); for (int i = 0; i < rem; i++) SkipProperty(d, ref p, t.InnerType!, pkg);
                    int c = I32(d, ref p); for (int i = 0; i < c; i++) SkipProperty(d, ref p, t.InnerType!, pkg); break;
                }
            case "MapProperty":
                {
                    int rem = I32(d, ref p); for (int i = 0; i < rem; i++) SkipProperty(d, ref p, t.InnerType!, pkg);
                    int c = I32(d, ref p);
                    for (int i = 0; i < c; i++) { SkipProperty(d, ref p, t.InnerType!, pkg); SkipProperty(d, ref p, t.ValueType!, pkg); }
                    break;
                }
            default: throw new NotSupportedException($"property type {t.Type}");
        }
        if (p > d.Length) throw new InvalidDataException("value runs past the export");
    }

    static int I32(byte[] d, ref int p) { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }

    public static void SkipFString(byte[] d, ref int p)
    {
        int len = I32(d, ref p);
        if (Math.Abs((long)len) > 1 << 20) throw new InvalidDataException($"implausible FString length {len}");
        p += len > 0 ? len : -len * 2;
    }

    public static void SkipText(byte[] d, ref int p)
    {
        p += 4;                                       // flags
        sbyte history = (sbyte)d[p++];
        switch (history)
        {
            case -1: { int has = I32(d, ref p); if (has != 0) SkipFString(d, ref p); break; }
            case 0: SkipFString(d, ref p); SkipFString(d, ref p); SkipFString(d, ref p); break;   // Base: namespace, key, source
            case 11: p += 8; SkipFString(d, ref p); break;                                       // StringTableEntry: table id, key
            default: throw new NotSupportedException($"FText history {history}");
        }
    }
}
