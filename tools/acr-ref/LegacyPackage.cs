using System.Buffers.Binary;
using System.Text;

namespace AcrRef;

/// A cooked legacy package (.uasset + .uexp) as written by `retoc to-legacy` for UE 5.6 (unversioned,
/// FilterEditorOnly). Supports appending names and imports and replacing export data; everything else in
/// the header is copied verbatim and only its offsets are shifted.
///
/// Header order (retoc legacy_asset.rs): summary, name map, import map (32 B each), export map (96 B each),
/// then cell maps / depends / asset registry / preload dependencies / data resources up to TotalHeaderSize.
internal sealed class LegacyPackage
{
    const int ImportSize = 32, ExportSize = 96;

    readonly byte[] _header;
    readonly int _namesOffset, _namesEnd, _importsOffset, _exportsOffset, _exportsEnd, _totalHeaderSize;
    readonly int _origNameCount, _origImportCount;
    readonly Dictionary<string, int> _fieldPos = new();
    readonly List<int> _shiftablePos = new(), _generationsPos = new();
    readonly List<string> _names = new();
    readonly List<int> _nameHashes = new();
    readonly List<byte[]> _imports = new();
    readonly List<byte[]> _exports = new();
    readonly List<byte[]> _exportData = new();
    readonly byte[] _uexpTag;

    public string PackageName { get; }
    public IReadOnlyList<string> Names => _names;
    public int ExportCount => _exports.Count;
    public int ImportCount => _imports.Count;

    public LegacyPackage(string uassetPath)
    {
        _header = File.ReadAllBytes(uassetPath);
        var uexp = File.ReadAllBytes(Path.ChangeExtension(uassetPath, ".uexp"));
        var d = _header;
        int p = 0;
        int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
        string FStr()
        {
            int len = I32();
            if (len == 0) return "";
            string s = len > 0 ? Encoding.Latin1.GetString(d, p, len - 1) : Encoding.Unicode.GetString(d, p, -len * 2 - 2);
            p += len > 0 ? len : -len * 2;
            return s;
        }
        void Field(string name) { _fieldPos[name] = p; p += 4; }

        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x9E2A83C1) throw new InvalidDataException("not a package");
        p = 4;
        int legacy = I32();
        if (legacy != -9) throw new NotSupportedException($"legacy file version {legacy}, expected -9 (UE 5.6)");
        p += 4 * 4;                                   // UE3, UE4, UE5, licensee versions
        p += 20;                                      // saved hash
        Field("TotalHeaderSize");
        int customVersions = I32(); p += customVersions * 20;
        PackageName = FStr();
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4;
        if ((flags & 0x80000000) == 0) throw new NotSupportedException("package is not FilterEditorOnly");
        Field("NameCount"); Field("NameOffset");
        Field("SoftObjectPathsCount"); Field("SoftObjectPathsOffset");
        p += 8;                                       // gatherable text data count/offset
        Field("ExportCount"); Field("ExportOffset");
        Field("ImportCount"); Field("ImportOffset");
        Field("CellExportCount"); Field("CellExportOffset");
        Field("CellImportCount"); Field("CellImportOffset");
        Field("MetaDataOffset"); Field("DependsOffset");
        Field("SoftPackageReferencesCount"); Field("SoftPackageReferencesOffset");
        Field("SearchableNamesOffset"); Field("ThumbnailTableOffset");
        int generations = I32();
        for (int g = 0; g < generations; g++) { _generationsPos.Add(p); p += 8; }
        for (int e = 0; e < 2; e++) { p += 2 + 2 + 2 + 4; FStr(); }   // saved-by / compatible-with engine versions
        p += 4 + 4 + 4;                               // compression flags, compressed chunk count, package source
        int additional = I32(); for (int a = 0; a < additional; a++) FStr();
        Field("AssetRegistryDataOffset");
        _fieldPos["BulkDataStartOffset"] = p; p += 8;
        Field("WorldTileInfoDataOffset");
        int chunkIds = I32(); p += chunkIds * 4;
        Field("PreloadDependencyCount"); Field("PreloadDependencyOffset");
        Field("NamesReferencedFromExportDataCount");
        p += 8;                                       // payload TOC offset
        Field("DataResourceOffset");

        int Get(string f) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(_fieldPos[f]));
        _namesOffset = Get("NameOffset");
        if (p != _namesOffset) throw new InvalidDataException($"summary parse ended at {p}, name map starts at {_namesOffset}");
        _totalHeaderSize = Get("TotalHeaderSize");

        _origNameCount = Get("NameCount");
        for (int n = 0; n < _origNameCount; n++) { _names.Add(FStr()); _nameHashes.Add(I32()); }
        _namesEnd = p;
        _importsOffset = Get("ImportOffset");
        _origImportCount = Get("ImportCount");
        if (_importsOffset != _namesEnd || Get("SoftObjectPathsOffset") != _namesEnd)
            throw new InvalidDataException("import map does not directly follow the name map");
        for (int i = 0; i < _origImportCount; i++) _imports.Add(d.AsSpan(_importsOffset + i * ImportSize, ImportSize).ToArray());
        _exportsOffset = Get("ExportOffset");
        if (_exportsOffset != _importsOffset + _origImportCount * ImportSize) throw new InvalidDataException("export map does not follow the import map");
        int exportCount = Get("ExportCount");
        for (int e = 0; e < exportCount; e++) _exports.Add(d.AsSpan(_exportsOffset + e * ExportSize, ExportSize).ToArray());
        _exportsEnd = _exportsOffset + exportCount * ExportSize;

        foreach (var f in new[] { "CellExportOffset", "CellImportOffset", "MetaDataOffset", "DependsOffset", "SoftPackageReferencesOffset",
                                  "SearchableNamesOffset", "ThumbnailTableOffset", "AssetRegistryDataOffset",
                                  "WorldTileInfoDataOffset", "PreloadDependencyOffset", "DataResourceOffset" })
            _shiftablePos.Add(_fieldPos[f]);

        long dataTotal = 0;
        for (int e = 0; e < exportCount; e++)
        {
            long off = SerialOffset(e) - _totalHeaderSize, size = SerialSize(e);
            _exportData.Add(uexp.AsSpan((int)off, (int)size).ToArray());
            dataTotal += size;
        }
        if (dataTotal + 4 != uexp.Length) throw new InvalidDataException($"exports cover {dataTotal} of {uexp.Length - 4} uexp bytes");
        _uexpTag = uexp.AsSpan(uexp.Length - 4).ToArray();
        for (int e = 1; e < exportCount; e++)
            if (SerialOffset(e) < SerialOffset(e - 1)) throw new NotSupportedException("exports are not stored in export-map order");
    }

    long SerialSize(int e) => BinaryPrimitives.ReadInt64LittleEndian(_exports[e].AsSpan(28));
    long SerialOffset(int e) => BinaryPrimitives.ReadInt64LittleEndian(_exports[e].AsSpan(36));

    public string NameAt(int index, int number) => number == 0 ? _names[index] : $"{_names[index]}_{number - 1}";
    string FNameAt(byte[] entry, int at) => NameAt(BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at + 4)));

    public string ExportName(int e) => FNameAt(_exports[e], 16);
    public int ExportOuter(int e) => BinaryPrimitives.ReadInt32LittleEndian(_exports[e].AsSpan(12));
    public string ExportClass(int e)
    {
        int c = BinaryPrimitives.ReadInt32LittleEndian(_exports[e].AsSpan(0));
        return c < 0 ? ImportName(-c - 1) : c > 0 ? ExportName(c - 1) : "";
    }
    public string ImportName(int i) => FNameAt(_imports[i], 20);
    public string ImportClassName(int i) => FNameAt(_imports[i], 8);
    public string ImportClassPackage(int i) => FNameAt(_imports[i], 0);
    public int ImportOuter(int i) => BinaryPrimitives.ReadInt32LittleEndian(_imports[i].AsSpan(16));
    public byte[] ImportEntry(int i) => _imports[i];

    public int FindName(string s) => _names.IndexOf(s);
    public int FindExport(string name, string? cls = null, Func<int, bool>? pred = null)
    {
        var hits = Enumerable.Range(0, _exports.Count)
            .Where(e => ExportName(e) == name && (cls is null || ExportClass(e) == cls) && (pred is null || pred(e))).ToList();
        if (hits.Count != 1) throw new InvalidOperationException($"export {name} ({cls}) matched {hits.Count} times");
        return hits[0];
    }

    public byte[] GetExportData(int e) => _exportData[e];
    public void SetExportData(int e, byte[] data) => _exportData[e] = data;

    public int AddName(string s)
    {
        int i = _names.IndexOf(s);
        if (i >= 0) return i;
        _names.Add(s);
        _nameHashes.Add(0);
        return _names.Count - 1;
    }

    int? _exportNameCount;

    /// A name referenced from export data. IoStore keeps only the first NamesReferencedFromExportDataCount names for
    /// export data (the rest are header-only), so new ones are inserted at the end of that range and the header-only
    /// names after it move up by one (their references in the import and export maps are remapped); names added with
    /// AddName after the range would be dropped by `retoc to-zen`.
    public int AddExportName(string s)
    {
        _exportNameCount ??= BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(_fieldPos["NamesReferencedFromExportDataCount"]));
        int n = _exportNameCount.Value;
        int i = _names.IndexOf(s);
        if (i >= 0 && i < n) return i;
        if (i >= 0) throw new NotSupportedException($"{s} is a header-only name; moving it into the export-data range is not supported");
        if (BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(_fieldPos["SoftObjectPathsCount"])) != 0)
            throw new NotSupportedException("packages with soft object paths in the header are not supported");
        _names.Insert(n, s);
        _nameHashes.Insert(n, 0);
        void Shift(byte[] entry, int at)
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at));
            if (v >= n) BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(at), v + 1);
        }
        foreach (var imp in _imports) { Shift(imp, 0); Shift(imp, 8); Shift(imp, 20); }
        foreach (var exp in _exports) Shift(exp, 16);
        _exportNameCount = n + 1;
        return n;
    }

    /// Appends an import and returns its FPackageIndex (negative).
    public int AddImport(string classPackage, string className, int outerIndex, string objectName)
    {
        var e = new byte[ImportSize];
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0), AddName(classPackage));
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), AddName(className));
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), outerIndex);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(20), AddName(objectName));
        _imports.Add(e);
        return -_imports.Count;
    }

    public (byte[] uasset, byte[] uexp) Save()
    {
        using var nameBytes = new MemoryStream();
        for (int n = 0; n < _names.Count; n++)
        {
            var s = _names[n];
            bool ascii = s.All(c => c < 128);
            var bw = new BinaryWriter(nameBytes);
            if (ascii) { bw.Write(s.Length + 1); bw.Write(Encoding.ASCII.GetBytes(s)); bw.Write((byte)0); }
            else { bw.Write(-(s.Length + 1)); bw.Write(Encoding.Unicode.GetBytes(s)); bw.Write((short)0); }
            bw.Write(_nameHashes[n]); // new names: 0 (unused by the game, retoc writes 0)
        }
        int delta1 = (int)nameBytes.Length - (_namesEnd - _namesOffset);
        int delta2 = (_imports.Count - _origImportCount) * ImportSize;
        int delta = delta1 + delta2;
        int newTotal = _totalHeaderSize + delta;

        var h = new byte[newTotal];
        _header.AsSpan(0, _namesOffset).CopyTo(h);
        nameBytes.ToArray().CopyTo(h, _namesOffset);
        int p = _namesEnd + delta1;
        foreach (var imp in _imports) { imp.CopyTo(h, p); p += ImportSize; }
        int exportsAt = p;
        long offset = newTotal, end = newTotal;
        for (int e = 0; e < _exports.Count; e++)
        {
            var entry = (byte[])_exports[e].Clone();
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(28), _exportData[e].Length);
            BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(36), offset);
            offset += _exportData[e].Length;
            end = offset;
            entry.CopyTo(h, p); p += ExportSize;
        }
        _header.AsSpan(_exportsEnd, _totalHeaderSize - _exportsEnd).CopyTo(h.AsSpan(p));

        void Set32(string f, int v) => BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(_fieldPos[f]), v);
        Set32("TotalHeaderSize", newTotal);
        Set32("NameCount", _names.Count);
        if (_exportNameCount is { } enc) Set32("NamesReferencedFromExportDataCount", enc);
        Set32("SoftObjectPathsOffset", _namesEnd + delta1);
        Set32("ImportCount", _imports.Count);
        Set32("ImportOffset", _namesEnd + delta1);
        Set32("ExportOffset", exportsAt);
        foreach (var pos in _shiftablePos)
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(pos));
            if (v > 0 && v >= _exportsOffset) BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(pos), v + delta);
        }
        foreach (var g in _generationsPos) BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(g + 4), _names.Count);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(_fieldPos["BulkDataStartOffset"]), end);

        using var u = new MemoryStream();
        foreach (var data in _exportData) u.Write(data);
        u.Write(_uexpTag);
        return (h, u.ToArray());
    }

    public void SaveTo(string uassetPath)
    {
        var (h, u) = Save();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(uassetPath))!);
        File.WriteAllBytes(uassetPath, h);
        File.WriteAllBytes(Path.ChangeExtension(uassetPath, ".uexp"), u);
    }
}
