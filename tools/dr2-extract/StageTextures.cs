using System.Text.Json;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Formats.Pssg;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;

namespace Dr2Extract;

/// <summary>
/// <c>stage-textures &lt;stage_id&gt; [--ir work/ir] [--dr2 ...] [--cache ...]</c>: every texture referenced by the
/// stage IR's <c>render/*.materials.json</c> written as DDS to <c>&lt;ir&gt;/&lt;stage&gt;/textures/</c>. References are
/// <c>"file.pssg#name"</c> (alternatives separated by <c>|</c>) or <c>"#name"</c>; the files are looked up in the
/// route folder, then the location folder, and every name is also searched in tracksplit.pssg and the other
/// <c>*textures*.pssg</c> there. Leaves the IR itself untouched.
/// </summary>
internal static partial class StageTextures
{
    [GeneratedRegex(@"^(?<loc>.+)_route_(?<route>\d+)$")]
    private static partial Regex StageId();

    public static async Task<int> Run(string[] args)
    {
        var id = Program.Positional(args).FirstOrDefault() ?? throw new ArgumentException("stage-textures <stage_id>");
        var irDir = Path.Combine(Program.Option(args, "--ir") ?? Path.Combine("work", "ir"), id);
        var install = await Dr2Install.OpenAsync(Program.Option(args, "--dr2") ?? Dr2Install.DefaultRoot,
            Program.Option(args, "--cache") ?? Path.Combine("work", "dr2", "cache"));
        var m = StageId().Match(id);
        if (!m.Success) throw new ArgumentException($"stage id '{id}' is not <location>_route_<n>");
        var location = m.Groups["loc"].Value;
        var archive = install.LocationArchive(location);
        await install.OpenLocationAsync(archive);
        var st = new StageRef(archive, Path.GetFileNameWithoutExtension(archive).Split("__")[0], location, int.Parse(m.Groups["route"].Value));

        var refs = new List<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(irDir, "render"), "*.materials.json"))
            foreach (var mat in JsonDocument.Parse(File.ReadAllText(f)).RootElement.EnumerateArray())
                foreach (var t in mat.GetProperty("textures").EnumerateArray())
                    refs.Add(t.GetProperty("texture").GetString()!);
        refs = refs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var files = refs.Where(r => r.IndexOf('#') > 0).SelectMany(r => r[..r.IndexOf('#')].Split('|'))
            .Append("tracksplit.pssg")
            .Concat(install.List($"{st.RouteDir}/").Concat(install.List($"{st.LocDir}/"))
                .Where(p => p.EndsWith(".pssg", StringComparison.OrdinalIgnoreCase)
                            && (p.Contains("textures", StringComparison.OrdinalIgnoreCase) || p.EndsWith("/patchup_ot.pssg", StringComparison.OrdinalIgnoreCase))
                            && p.Count(c => c == '/') <= st.RouteDir.Count(c => c == '/') + 1)
                .Select(p => p[(p.LastIndexOf('/') + 1)..]))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sources = new List<PssgFile>();
        foreach (var file in files)
        {
            var path = install.Exists($"{st.RouteDir}/{file}") ? $"{st.RouteDir}/{file}"
                : install.Exists($"{st.LocDir}/{file}") ? $"{st.LocDir}/{file}" : null;
            if (path is null) { Console.WriteLine($"  source {file}: not found"); continue; }
            try
            {
                var pssg = PssgCommands.Open(await install.FetchAsync(path));
                pssg.TryGetObject<PssgTexture>("".AsMemory());
                sources.Add(pssg);
                Console.WriteLine($"  source {path}");
            }
            catch (Exception e) { Console.WriteLine($"  source {path}: skipped ({e.Message})"); }
        }

        var outDir = Path.Combine(irDir, "textures");
        Directory.CreateDirectory(outDir);
        var missing = new List<string>();
        var failed = new List<string>();
        int written = 0;
        foreach (var reference in refs)
        {
            var name = reference[(reference.IndexOf('#') + 1)..];
            // several sources can hold the same name (e.g. a 4x4 stand-in next to the full texture): take the largest
            var texture = sources.Select(s => s.TryGetObject<PssgTexture>(name.AsMemory())).Where(t => t is not null)
                .MaxBy(t => (long)t!.Width * t.Height);
            if (texture is null) { missing.Add(reference); continue; }
            var target = Path.Combine(outDir, Path.ChangeExtension(name, ".dds"));
            if (texture.ImageBlockCount > 1 && texture.ImageBlocks.All(b => b.TypeName == "Raw"))
            {
                // texture array: one Raw block per slice (ToDdsFile reads several blocks as cube faces)
                int slice = 0;
                foreach (var block in texture.ImageBlocks)
                    File.WriteAllBytes(Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(name)}_slice{slice++}.dds"),
                                       SliceDds(texture, block.ImageData.Value));
                Console.WriteLine($"  {name}: texture array, {slice} slices ({texture.TexelFormat} {texture.Width}x{texture.Height})");
                written++;
                continue;
            }
            try
            {
                using var fs = File.Create(target);
                texture.ToDdsFile().Write(fs);
                written++;
            }
            catch (Exception e)
            {
                File.Delete(target);
                failed.Add($"{reference}: {e.Message}");
            }
        }
        Console.WriteLine($"stage-textures {id}: {refs.Count} referenced, {written} written to {outDir}, {missing.Count} not found, {failed.Count} failed");
        foreach (var x in missing.Take(30)) Console.WriteLine($"  missing {x}");
        foreach (var x in failed.Take(30)) Console.WriteLine($"  failed {x}");
        return 0;
    }

    /// One texture-array slice as a DX10 DDS (all mips of the slice, as stored in its image block).
    internal static byte[] SliceDds(PssgTexture t, byte[] data)
    {
        uint dxgi = t.TexelFormat switch
        {
            "dxt1" => 71, "dxt1_srgb" => 72, "dxt3" => 74, "dxt3_srgb" => 75, "dxt5" => 77, "dxt5_srgb" => 78,
            "BC7" => 98, "BC7_srgb" => 99,
            _ => throw new NotSupportedException($"texture array texel format {t.TexelFormat}"),
        };
        uint mips = !t.AutoMipMap && t.MipMapCount > 0 ? t.MipMapCount + 1 : 1;
        uint w = t.Width, h = t.Height, blockBytes = dxgi is 71 or 72 ? 8u : 16u;
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(0x20534444u); bw.Write(124u); bw.Write(0x1u | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        bw.Write(h); bw.Write(w); bw.Write(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes);
        bw.Write(0u); bw.Write(mips);
        for (int i = 0; i < 11; i++) bw.Write(0u);
        bw.Write(32u); bw.Write(0x4u); bw.Write(0x30315844u); for (int i = 0; i < 5; i++) bw.Write(0u);
        bw.Write(0x1000u | 0x400000 | 0x8); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write(dxgi); bw.Write(3u); bw.Write(0u); bw.Write(1u); bw.Write(0u);
        bw.Write(data);
        return ms.ToArray();
    }
}
