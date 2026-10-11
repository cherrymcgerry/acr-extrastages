using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcrRef;

/// Drop-in DR2 textures (v1.8): new Texture2D and MaterialInstanceConstant packages cloned from cooked ACR templates
/// ([dr2_textures] in config/visuals.toml), written next to the overridden host packages in the mod folder.
///   Texture2D: template = a NeverStream texture with every mip inline in its export (no .ubulk): its property block
///     is kept (ImportedSize patched), the platform data is rewritten for the DR2 pixel format, size and mips (BCn
///     blocks copied as they are), and the data resource table gets one inline entry per mip.
///   MaterialInstanceConstant: template = an M_Simple child (opaque, lit, static switches for base colour, ORM and
///     normal textures); its three texture imports are renamed to the new packages (the cooked shader permutation is
///     M_Simple's and unchanged).
internal sealed class Dr2Materials
{
    readonly Toml _cfg;
    readonly string _legacy, _outRoot, _texDir, _root;
    readonly int _maxSize;
    readonly Dictionary<string, string> _textures = new(), _mics = new();
    public readonly JsonArray Log = new();
    public long TextureBytes;
    public int TextureCount => _textures.Count;
    public int MicCount => _mics.Count;
    string? _orm, _normal;

    public Dr2Materials(Toml cfg, string irDir, string legacy, string outRoot)
    {
        _cfg = cfg; _legacy = legacy; _outRoot = outRoot;
        _texDir = Path.Combine(irDir, "textures");
        _root = cfg.Str("dr2_textures", "package_root").TrimEnd('/');
        _maxSize = (int)cfg.Num("dr2_textures", "max_size");
    }

    string LegacyFile(string package)
    {
        if (!package.StartsWith("/Game/")) throw new ArgumentException($"{package}: only /Game packages");
        return Path.Combine(_legacy, "acr", "Content", package["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
    }

    static string Clean(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    public static string PixelFormat(Dds.Fmt f) => f switch
    {
        Dds.Fmt.BC1 => "PF_DXT1", Dds.Fmt.BC3 => "PF_DXT5", Dds.Fmt.BC4 => "PF_BC4", Dds.Fmt.BC5 => "PF_BC5", Dds.Fmt.BC7 => "PF_BC7",
        _ => throw new NotSupportedException(f.ToString()),
    };

    /// A new Texture2D package (path returned) from BCn mips (mip 0 first); mips wider or taller than max_size are dropped.
    public string Texture(string name, Dds.Fmt fmt, bool linear, int w, int h, IReadOnlyList<byte[]> mips)
    {
        if (_textures.TryGetValue(name, out var known)) return known;
        int first = 0;
        while (first < mips.Count - 1 && Math.Max(w >> first, h >> first) > _maxSize) first++;
        int mw = Math.Max(1, w >> first), mh = Math.Max(1, h >> first);
        var path = $"{_root}/Tex/T_{Clean(name)}";
        var tpl = _cfg.Str("dr2_textures", linear ? "linear_template" : "texture_template");
        var pkg = new LegacyPackage(LegacyFile(tpl));
        if (pkg.ExportCount != 1 || pkg.ExportClass(0) != "Texture2D") throw new InvalidDataException($"{tpl}: not a single Texture2D export");
        string tplPf = pkg.Names[1], objName = path[(path.LastIndexOf('/') + 1)..];
        if (!tplPf.StartsWith("PF_") || pkg.Names[2] != pkg.ExportName(0) || pkg.Names[3] != pkg.PackageName)
            throw new InvalidDataException($"{tpl}: name map is not [None, PF_*, object, package, ...]");
        var (data, payloads) = TextureExport(pkg.GetExportData(0), tplPf, PixelFormat(fmt), mw, mh, mips.Skip(first).ToList());

        var dr = pkg.DataResources();
        int version = BinaryPrimitives.ReadInt32LittleEndian(dr);
        var entry0 = dr.AsSpan(8, LegacyPackage.DataResourceSize).ToArray();
        if (BinaryPrimitives.ReadUInt32LittleEndian(entry0.AsSpan(40)) != 0x48) throw new InvalidDataException($"{tpl}: mip 0 is not inline (SingleUse | ForceInlinePayload)");
        var table = new byte[8 + payloads.Count * LegacyPackage.DataResourceSize];
        BinaryPrimitives.WriteInt32LittleEndian(table, version);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(4), payloads.Count);
        for (int i = 0; i < payloads.Count; i++)
        {
            var e = table.AsSpan(8 + i * LegacyPackage.DataResourceSize, LegacyPackage.DataResourceSize);
            entry0.CopyTo(e);
            BinaryPrimitives.WriteInt64LittleEndian(e[4..], payloads[i].offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[20..], payloads[i].size);
            BinaryPrimitives.WriteInt64LittleEndian(e[28..], payloads[i].size);
        }
        pkg.SetExportData(0, data);
        pkg.SetDataResources(table);
        pkg.RenameName(1, PixelFormat(fmt));
        pkg.RenameName(2, objName);
        pkg.RenameName(3, path);
        pkg.SetPackageName(path);
        var file = Path.Combine(_outRoot, "acr", "Content", path["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        pkg.SaveTo(file);
        TextureBytes += new FileInfo(Path.ChangeExtension(file, ".uexp")).Length;
        Log.Add($"{path}: {PixelFormat(fmt)} {(linear ? "linear" : "sRGB")} {mw}x{mh}, {payloads.Count} mips{(first > 0 ? $" ({first} above {_maxSize} dropped)" : "")}, from {name}");
        return _textures[name] = path;
    }

    /// The platform data of the template rewritten (FTexturePlatformData, UE 5.6 cooked, one pixel format):
    /// FName PixelFormat, int64 SkipOffset (from its own position to the trailing None), 16-byte placeholder, SizeX,
    /// SizeY, PackedData, FString PixelFormat, FirstMipToSerialize, mip count, per mip {int32 data resource index,
    /// inline payload, SizeX, SizeY, SizeZ}, bIsVirtual; then FName None.
    static (byte[] data, List<(long offset, long size)> payloads) TextureExport(byte[] tpl, string tplPf, string pf, int w, int h, List<byte[]> mips)
    {
        byte[] FStr(string s) { var b = new byte[4 + s.Length + 1]; BinaryPrimitives.WriteInt32LittleEndian(b, s.Length + 1); Encoding.ASCII.GetBytes(s).CopyTo(b, 4); return b; }
        var pat = FStr(tplPf);
        int strAt = tpl.AsSpan().IndexOf(pat);
        if (strAt < 0 || tpl.AsSpan(strAt + 1).IndexOf(pat) >= 0) throw new InvalidDataException($"pixel format string {tplPf} not unique in the template");
        int fnameAt = strAt - 12 - 16 - 8 - 8;
        if (BinaryPrimitives.ReadInt64LittleEndian(tpl.AsSpan(fnameAt)) != 1) throw new InvalidDataException("template platform data does not start with FName #1");
        int tw = BinaryPrimitives.ReadInt32LittleEndian(tpl.AsSpan(strAt - 12)), th = BinaryPrimitives.ReadInt32LittleEndian(tpl.AsSpan(strAt - 8));
        int packed = BinaryPrimitives.ReadInt32LittleEndian(tpl.AsSpan(strAt - 4));
        if (packed != 1) throw new NotSupportedException($"template PackedData {packed:X} (expected 1: one slice, no optional data)");
        var prefix = tpl.AsSpan(0, fnameAt).ToArray();
        var size = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(size, tw); BinaryPrimitives.WriteInt32LittleEndian(size.AsSpan(4), th);
        int imported = prefix.AsSpan().IndexOf(size);
        if (imported >= 0 && prefix.AsSpan(imported + 1).IndexOf(size) < 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(imported), w);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(imported + 4), h);
        }

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(prefix);
        bw.Write(1); bw.Write(0);
        long skipAt = ms.Position; bw.Write(0L);
        bw.Write(new byte[16]);
        bw.Write(w); bw.Write(h); bw.Write(packed);
        bw.Write(FStr(pf));
        bw.Write(0); bw.Write(mips.Count);
        var payloads = new List<(long, long)>();
        for (int i = 0; i < mips.Count; i++)
        {
            bw.Write(i);
            payloads.Add((ms.Position, mips[i].Length));
            bw.Write(mips[i]);
            bw.Write(Math.Max(1, w >> i)); bw.Write(Math.Max(1, h >> i)); bw.Write(1);
        }
        bw.Write(0);                               // bIsVirtual
        long end = ms.Position;
        bw.Write(0L);                              // None
        bw.Flush();
        var data = ms.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan((int)skipAt), end - skipAt);
        return (data, payloads);
    }

    public string Texture(string ddsName, bool linear = false)
    {
        var dds = new Dds(Path.Combine(_texDir, ddsName + ".dds"));
        return Texture(ddsName, dds.Format, linear, dds.Width, dds.Height, dds.Mips);
    }

    string Orm()
    {
        var c = _cfg.Arr("dr2_textures", "orm").Select(x => Convert.ToByte(x)).ToArray();
        var px = new byte[16 * 4];
        for (int i = 0; i < 16; i++) { px[i * 4] = c[0]; px[i * 4 + 1] = c[1]; px[i * 4 + 2] = c[2]; px[i * 4 + 3] = 255; }
        return _orm ??= Texture("flat_orm", Dds.Fmt.BC1, true, 4, 4, [Dds.EncodeBc1(px, 4, 4)]);
    }

    string Normal() => _normal ??= Texture("flat_normal", Dds.Fmt.BC5, true, 4, 4, [Dds.SolidBc5(128, 128)]);

    /// A MaterialInstanceConstant (path returned) with the given base colour texture, the flat ORM and normal.
    public string Mic(string name, string baseColor)
    {
        if (_mics.TryGetValue(name, out var known)) return known;
        var tpl = _cfg.Str("dr2_textures", "mic_template");
        var pkg = new LegacyPackage(LegacyFile(tpl));
        var path = $"{_root}/Mat/MI_{Clean(name)}";
        void Rename(string from, string to)
        {
            int i = pkg.FindName(from);
            if (i < 0) throw new InvalidDataException($"{tpl}: name {from} not found");
            pkg.RenameName(i, to);
        }
        string Obj(string p) => p[(p.LastIndexOf('/') + 1)..];
        foreach (var (key, tex) in new[] { ("mic_base_texture", baseColor), ("mic_orm_texture", Orm()), ("mic_normal_texture", Normal()) })
        {
            var old = _cfg.Str("dr2_textures", key);
            int imp = Enumerable.Range(0, pkg.ImportCount).Single(i => pkg.ImportName(i) == Obj(old) && pkg.ImportClassName(i) == "Texture2D");
            Rename(pkg.ImportName(-pkg.ImportOuter(imp) - 1), tex);
            Rename(Obj(old), Obj(tex));
        }
        Rename(pkg.ExportName(0), Obj(path));
        Rename(pkg.PackageName, path);
        pkg.SetPackageName(path);
        pkg.SaveTo(Path.Combine(_outRoot, "acr", "Content", path["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset"));
        Log.Add($"{path}: base colour {baseColor}");
        return _mics[name] = path;
    }

    /// Vista: DR2's large colour map (grey-centred tint) x 2 over its three detail layers' mean colours, mixed by the
    /// large blend map (R: layer 2, G: layer 3), baked at vista_size as BC1.
    public string VistaMic()
    {
        int size = (int)_cfg.Num("dr2_textures", "vista_size");
        var cm = new Dds(Path.Combine(_texDir, _cfg.Str("dr2_textures", "vista_colour_map") + ".dds"));
        var mk = new Dds(Path.Combine(_texDir, _cfg.Str("dr2_textures", "vista_blend_map") + ".dds"));
        var layers = _cfg.Arr("dr2_textures", "vista_layers").Cast<string>().Select(n =>
        {
            var d = new Dds(Path.Combine(_texDir, n + ".dds"));
            int m = Enumerable.Range(0, d.Mips.Count).First(i => d.MipWidth(i) <= 64);
            var p = d.Decode(m)!;
            return Enumerable.Range(0, 3).Select(c => Enumerable.Range(0, p.Length / 4).Average(i => (double)p[i * 4 + c])).ToArray();
        }).ToArray();
        byte[] At(Dds d) { int m = Enumerable.Range(0, d.Mips.Count).First(i => d.MipWidth(i) <= size); return d.Decode(m)!; }
        var c = At(cm); var k = At(mk);
        var px = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            double w2 = k[i * 4] / 255.0, w3 = k[i * 4 + 1] / 255.0, w1 = Math.Max(0, 1 - w2 - w3);
            for (int ch = 0; ch < 3; ch++)
            {
                double layer = w1 * layers[0][ch] + w2 * layers[1][ch] + w3 * layers[2][ch];
                px[i * 4 + ch] = (byte)Math.Clamp(2 * c[i * 4 + ch] / 255.0 * layer, 0, 255);
            }
            px[i * 4 + 3] = 255;
        }
        var name = _cfg.Str("dr2_textures", "vista_colour_map") + "_baked";
        PreviewPixels = (name, size, px);
        return Mic("vista", Texture(name, Dds.Fmt.BC1, false, size, size, Dds.Bc1Chain(px, size, size)));
    }
    public (string name, int size, byte[] px)? PreviewPixels;

    /// Objects: the DR2 material's diffuse (TDiffuseAlphaMap / TDiffuse*), null if not usable: no texture, a shader in
    /// object_skip_shaders (alpha-tested foliage, decals), or an unsupported format.
    public string? ObjectDiffuse(JsonNode material, out string? why)
    {
        why = null;
        var shader = material["shader_group"]?.GetValue<string>() ?? "";
        if (_cfg.Arr("dr2_textures", "object_skip_shaders").Cast<string>().Any(s => shader.Contains(s))) { why = $"shader {shader}"; return null; }
        var tex = material["textures"]!.AsArray().FirstOrDefault(t => t!["parameter"]!.GetValue<string>().StartsWith("TDiffuse"));
        if (tex is null) { why = "no diffuse texture"; return null; }
        var r = tex["texture"]!.GetValue<string>();
        var dds = Path.ChangeExtension(r[(r.IndexOf('#') + 1)..], null);
        if (!File.Exists(Path.Combine(_texDir, dds + ".dds"))) { why = "diffuse not exported from DR2"; return null; }
        try { _ = new Dds(Path.Combine(_texDir, dds + ".dds")); }
        catch (NotSupportedException e) { why = $"unsupported DDS ({e.Message[(e.Message.IndexOf(':') + 2)..]})"; return null; }
        return dds;
    }

    public string ObjectMic(string diffuse) => Mic("obj_" + diffuse, Texture(diffuse));
}

/// Drop-in DR2 textures (v1.8) helpers.
///   dds-preview <file.dds>... --out <dir> [--max 1024]   first mip at most --max px wide, as PNG (BC7 skipped)
internal static class DropIn
{
    /// vista-map <vista.glb> <colourmap.dds>: which of the 8 axis-aligned orientations of the glb plan bounds onto the
    /// colour map puts DR2's sea (the colour at the map's left edge) under the lowest vertices.
    public static int VistaMap(Options o)
    {
        var glb = new Glb(o.Positional[0]);
        var dds = new Dds(o.Positional[1]);
        int m = Enumerable.Range(0, dds.Mips.Count).First(i => dds.MipWidth(i) <= 512);
        var px = dds.Decode(m)!; int w = dds.MipWidth(m), h = dds.MipHeight(m);
        var v = glb.Primitives().SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3)
            .Select(i => (x: p.Positions[i * 3], y: p.Positions[i * 3 + 1], z: p.Positions[i * 3 + 2]))).ToList();
        float x0 = v.Min(q => q.x), x1 = v.Max(q => q.x), z0 = v.Min(q => q.z), z1 = v.Max(q => q.z);
        (int r, int g, int b) At(double u, double t)
        {
            int X = Math.Clamp((int)(u * w), 0, w - 1), Y = Math.Clamp((int)(t * h), 0, h - 1), k = (Y * w + X) * 4;
            return (px[k], px[k + 1], px[k + 2]);
        }
        var sea = At(0.02, 0.5);
        bool IsSea((int r, int g, int b) c) => Math.Abs(c.r - sea.r) + Math.Abs(c.g - sea.g) + Math.Abs(c.b - sea.b) < 8;
        Console.WriteLine($"sea colour {sea}; {v.Count} vertices, plan x {x0:0}..{x1:0} z {z0:0}..{z1:0}, height {v.Min(q => q.y):0.0}..{v.Max(q => q.y):0.0}");
        for (int k = 0; k < 8; k++)
        {
            bool swap = (k & 4) != 0, fu = (k & 1) != 0, fv = (k & 2) != 0;
            float low = v.Min(q => q.y) + 3, high = v.Select(q => q.y).Order().ElementAt(v.Count / 2);
            int lowN = 0, lowSea = 0, highN = 0, highSea = 0;
            foreach (var q in v)
            {
                double a = (q.x - x0) / (x1 - x0), b = (q.z - z0) / (z1 - z0);
                if (swap) (a, b) = (b, a);
                if (fu) a = 1 - a; if (fv) b = 1 - b;
                bool s = IsSea(At(a, b));
                if (q.y < low) { lowN++; if (s) lowSea++; }
                else if (q.y > high) { highN++; if (s) highSea++; }
            }
            Console.WriteLine($"  swap {swap} flipU {fu} flipV {fv}: sea-coloured under {lowSea * 100.0 / lowN:0.0}% of {lowN} sea-level vertices, " +
                              $"{highSea * 100.0 / highN:0.0}% of {highN} upper-half vertices");
        }
        if (o.Out is { } png)
        {
            // unflipped orientation: vista vertices red, the stage terrain (--terrain glb) blue
            var img = (byte[])px.Clone();
            void Dot(double a, double b, byte r, byte g, byte bl)
            {
                int X = Math.Clamp((int)(a * w), 0, w - 1), Y = Math.Clamp((int)(b * h), 0, h - 1), k = (Y * w + X) * 4;
                img[k] = r; img[k + 1] = g; img[k + 2] = bl; img[k + 3] = 255;
            }
            foreach (var q in v.Where((_, i) => i % 7 == 0)) Dot((q.x - x0) / (x1 - x0), (q.z - z0) / (z1 - z0), 255, 0, 0);
            if (o.Named.GetValueOrDefault("terrain") is { } tg)
                foreach (var p in new Glb(tg).Primitives())
                    for (int i = 0; i < p.Positions.Length / 3; i += 25)
                        Dot((p.Positions[i * 3] - x0) / (x1 - x0), (p.Positions[i * 3 + 2] - z0) / (z1 - z0), 0, 0, 255);
            for (int i = 3; i < img.Length; i += 4) img[i] = 255;
            Dds.WritePng(png, w, h, img);
        }
        return 0;
    }

    public static int Preview(Options o)
    {
        var outDir = o.Out ?? throw new ArgumentException("dds-preview needs --out");
        Directory.CreateDirectory(outDir);
        int max = int.Parse(o.Named.GetValueOrDefault("max", "1024"));
        foreach (var f in o.Positional)
        {
            var dds = new Dds(f);
            int m = Enumerable.Range(0, dds.Mips.Count).FirstOrDefault(i => dds.MipWidth(i) <= max);
            var px = dds.Decode(m);
            if (px is null) { Console.WriteLine($"{f}: {dds.Format} not decoded"); continue; }
            for (int i = 3; i < px.Length; i += 4) px[i] = 255; // DR2 diffuse alpha is specular, not coverage
            var png = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".png");
            Dds.WritePng(png, dds.MipWidth(m), dds.MipHeight(m), px);
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < px.Length; i += 4) { r += px[i]; g += px[i + 1]; b += px[i + 2]; }
            int n = px.Length / 4;
            Console.WriteLine($"{Path.GetFileName(f)}: {dds.Format}{(dds.Srgb ? " sRGB" : "")} {dds.Width}x{dds.Height} {dds.Mips.Count} mips; " +
                              $"mip {m} -> {png}; mean RGB {r / n:0} {g / n:0} {b / n:0}");
        }
        return 0;
    }
}
