using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;

namespace AcrRef;

/// Read-only surveys for drop-in textures (v1.8):
///   tex-scan --filter <regex> [--limit N]   Texture2D: format, size, mips, NeverStream, VT, mip 0 bulk flags
///   mic-scan --filter <regex> [--limit N]   MaterialInstanceConstant: parent chain, root blend mode, texture params
///                                           (with each default texture's VT flag)
internal static class TexScan
{
    public static int Textures(Options o)
    {
        using var p = Program.Open(o);
        var re = new Regex(o.Filter ?? ".", RegexOptions.IgnoreCase);
        int n = 0;
        foreach (var path in p.Files.Keys.Where(k => k.EndsWith(".uasset") && re.IsMatch(k)).Order())
        {
            if (n >= (o.Limit ?? 50)) break;
            try
            {
                var tex = p.LoadPackage(path).GetExports().OfType<UTexture2D>().FirstOrDefault();
                if (tex?.PlatformData is not { } pd) continue;
                n++;
                var m0 = pd.Mips.Length > 0 ? pd.Mips[0] : null;
                Console.WriteLine($"{path}: {pd.SizeX}x{pd.SizeY} {pd.PixelFormat} mips {pd.Mips.Length} first {pd.FirstMipToSerialize} " +
                                  $"VT {pd.VTData is not null} NeverStream {tex.GetOrDefault<bool>("NeverStream")} SRGB {tex.SRGB} " +
                                  $"comp {tex.GetOrDefault<FName>("CompressionSettings").Text} lod {tex.GetOrDefault<FName>("LODGroup").Text} " +
                                  $"mip0 {(m0 is null ? "-" : $"{m0.SizeX}x{m0.SizeY} flags {m0.BulkData?.Header.BulkDataFlags} size {m0.BulkData?.Header.SizeOnDisk}")} " +
                                  $"last {(pd.Mips.Length > 0 ? pd.Mips[^1].BulkData?.Header.BulkDataFlags.ToString() : "-")}");
            }
            catch (Exception e) { Console.WriteLine($"{path}: {e.GetType().Name} {e.Message}"); }
        }
        return 0;
    }

    /// tex-mean --filter <regex>: mean RGBA of a small mip (<= 64 px) of each matching BC1/BC3/BC5 Texture2D.
    public static int Means(Options o)
    {
        using var p = Program.Open(o);
        var re = new Regex(o.Filter ?? "$^", RegexOptions.IgnoreCase);
        foreach (var path in p.Files.Keys.Where(k => k.EndsWith(".uasset") && re.IsMatch(k)).Order().Take(o.Limit ?? 20))
        {
            var tex = p.LoadPackage(path).GetExports().OfType<UTexture2D>().FirstOrDefault();
            if (tex?.PlatformData is not { } pd) continue;
            var fmt = pd.PixelFormat.ToString() switch { "PF_DXT1" => Dds.Fmt.BC1, "PF_DXT5" => Dds.Fmt.BC3, "PF_BC5" => Dds.Fmt.BC5, _ => (Dds.Fmt?)null };
            var mip = pd.Mips.FirstOrDefault(m => m.SizeX <= 64 && m.BulkData?.Data is { Length: > 0 });
            if (fmt is null || mip is null) { Console.WriteLine($"{path}: {pd.PixelFormat}, no small mip decoded"); continue; }
            var px = Dds.DecodeBlocks(fmt.Value, mip.BulkData!.Data!, mip.SizeX, mip.SizeY);
            var mean = Enumerable.Range(0, 4).Select(c => Enumerable.Range(0, px.Length / 4).Average(i => px[i * 4 + c])).ToArray();
            Console.WriteLine($"{path}: {pd.PixelFormat} mip {mip.SizeX}x{mip.SizeY} mean RGBA {mean[0]:0} {mean[1]:0} {mean[2]:0} {mean[3]:0}");
        }
        return 0;
    }

    /// legacy-dump <file.uasset> [--limit bytes]: names, imports, exports, header tail and export data (hex).
    public static int Dump(Options o)
    {
        var pkg = new LegacyPackage(o.Positional[0]);
        Console.WriteLine($"package {pkg.PackageName}");
        for (int i = 0; i < pkg.Names.Count; i++) Console.WriteLine($"  name {i}: {pkg.Names[i]}");
        for (int i = 0; i < pkg.ImportCount; i++)
            Console.WriteLine($"  import {-i - 1}: {pkg.ImportClassPackage(i)}.{pkg.ImportClassName(i)} {pkg.ImportName(i)} outer {pkg.ImportOuter(i)}");
        for (int e = 0; e < pkg.ExportCount; e++)
            Console.WriteLine($"  export {e + 1}: {pkg.ExportName(e)} class {pkg.ExportClass(e)} outer {pkg.ExportOuter(e)} flags {pkg.ExportFlags(e):X} " +
                              $"size {pkg.GetExportData(e).Length} deps {string.Join(",", pkg.ExportDeps(e).Select(d => $"{d.kind}:{d.index}"))}");
        Console.WriteLine("  " + string.Join(" ", pkg.HeaderFieldNames.Select(f => $"{f}={pkg.HeaderField(f)}")));
        var h = File.ReadAllBytes(o.Positional[0]);
        Console.WriteLine($"  header {h.Length} bytes; tail from export map end:");
        Hex(h, pkg.HeaderTailStart, h.Length - pkg.HeaderTailStart);
        for (int e = 0; e < pkg.ExportCount; e++)
        {
            Console.WriteLine($"  export {e + 1} data:");
            var d = pkg.GetExportData(e);
            Hex(d, 0, Math.Min(d.Length, o.Limit ?? 512));
        }
        return 0;
    }

    static void Hex(byte[] d, int at, int n)
    {
        for (int i = 0; i < n; i += 16)
            Console.WriteLine($"    {i:X5}: {string.Join(" ", d.Skip(at + i).Take(Math.Min(16, n - i)).Select(b => b.ToString("X2")))}");
    }

    public static int Materials(Options o)
    {
        using var p = Program.Open(o);
        var re = new Regex(o.Filter ?? ".", RegexOptions.IgnoreCase);
        int n = 0;
        foreach (var path in p.Files.Keys.Where(k => k.EndsWith(".uasset") && re.IsMatch(k)).Order())
        {
            if (n >= (o.Limit ?? 50)) break;
            try
            {
                var mic = p.LoadPackage(path).GetExports().OfType<UMaterialInstanceConstant>().FirstOrDefault();
                if (mic is null) continue;
                n++;
                var chain = new List<string>();
                UMaterialInterface? cur = mic;
                UMaterial? root = null;
                while (cur is UMaterialInstance mi && chain.Count < 8) { cur = mi.Parent?.Load<UMaterialInterface>(); chain.Add(cur?.Name ?? "?"); }
                root = cur as UMaterial;
                var texs = mic.TextureParameterValues.Select(t =>
                {
                    var tx = t.ParameterValue.Load<UTexture>();
                    bool vt = tx is UTexture2D t2 && (t2.PlatformData?.VTData is not null || t2.GetOrDefault<bool>("VirtualTextureStreaming"));
                    return $"{t.ParameterInfo.Name.Text}={tx?.Name}{(vt ? "[VT]" : "")}";
                });
                Console.WriteLine($"{path}: parents {string.Join(" > ", chain)}; blend {root?.GetOrDefault<FName>("BlendMode").Text} " +
                                  $"twoSided {root?.GetOrDefault<bool>("TwoSided")}; tex {string.Join(", ", texs)}; " +
                                  $"scalars {mic.ScalarParameterValues.Length} vectors {mic.VectorParameterValues.Length}");
            }
            catch (Exception e) { Console.WriteLine($"{path}: {e.GetType().Name} {e.Message}"); }
        }
        return 0;
    }
}
