using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace AcrRef;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var opts = Options.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "list" => List(opts),
                "export" => Export(opts),
                "export-many" => ExportMany(opts),
                "raw" => Raw(opts),
                "baked-summary" => BakedSummary.Run(opts),
                "exports" => ExportFilter.Run(opts),
                "baked-add-ramp" => BakedEdit.Run(opts),
                "patch-f32" => PatchF32(opts),
                "xref" => Xref.Run(opts),
                "schema" => Schema.Run(opts),
                "stage-selftest" => StageSelfTest.Run(opts),
                "stage-build" => StageBuild.Run(opts),
                "stage-check" => StageBuild.RunCheck(opts),
                "stage-verify" => StageVerify.Run(opts),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 2;
        }
    }

    static void PrintUsage() => Console.WriteLine("""
        acr-ref — read-only Assetto Corsa Rally (UE 5.6 IoStore) inspector built on CUE4Parse

        usage:
          acr-ref list        [--filter <regex>] [--out <file>]
          acr-ref export      <package-path> [--out <file.json>]
          acr-ref export-many --filter <regex> --out <dir> [--limit N]
          acr-ref raw         <package-path> --out <dir>
          acr-ref baked-summary <exported BakedDataContainer .json> [--out <dir>]
          acr-ref exports     <exported .json> [--filter <regex on Type|Name> --out <file.json>]

        editing legacy packages (output of `retoc to-legacy`):
          acr-ref patch-f32   <file.uexp> --expect <f32> --value <f32> --context <f64,...> [--context-hex <hex>] --out <file.uexp>
                              replaces the unique f32 that is immediately followed by the given doubles, then the raw bytes
          acr-ref baked-add-ramp --uexp <BakedData.uexp> --ref-collider <i> --at <x,z> --dir <dx,dz>
                              [--width 5 --length 8 --rise 1 --sink 0.05] --out <dir>
                              appends an always-active ramp collider (physics space: metres, Y up)

        DR2 stage port (host: Hafren South forward; see docs/decisions.md D6, mods/acrdirt-stage/build.ps1):
          acr-ref stage-build    --ir <work/ir/stage> --legacy <retoc legacy root> --out <mod root> [--report f.json] [--keep-pacenotes 1]
          acr-ref stage-check    --ir <dir> --legacy <root> --report <f.json> [--out file]   offline collision/raycast/spline checks
          acr-ref stage-verify   --ir <dir> --game <dir with Paks incl. the built container> [--out file]   CUE4Parse re-read
          acr-ref stage-selftest --legacy <root>   byte-exact round trips of the host packages
          acr-ref schema <Type>...                 usmap property indices (unversioned serialization order)

        binary analysis:
          acr-ref xref <string>... [--callers-of <va,...>] [--exe acr.exe] [--fields 0x349,0x350] [--out file.txt]
                              disassembles functions that reference the strings, marking struct-offset accesses

        common options:
          --game  <install dir>   (default: Steam ACR install)
          --usmap <file.usmap>    (default: Dumper-7 mappings)

        package paths look like acr/Content/Levels/WalesS4HafrenSouth.umap
        """);

    static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        PrintUsage();
        return 1;
    }

    internal static DefaultFileProvider Open(Options o)
    {
        var oodle = Path.Combine(AppContext.BaseDirectory, OodleHelper.OODLE_NAME_CURRENT);
        if (!File.Exists(oodle) && !OodleHelper.DownloadOodleDll(ref oodle))
            throw new InvalidOperationException("could not download the Oodle runtime");
        OodleHelper.Initialize(oodle);

        var paks = Path.Combine(o.Game, "acr", "Content", "Paks");
        var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_UE5_6), StringComparer.OrdinalIgnoreCase);
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(o.Usmap, StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
        provider.PostMount();
        return provider;
    }

    static int List(Options o)
    {
        using var p = Open(o);
        var re = o.Filter is null ? null : new Regex(o.Filter, RegexOptions.IgnoreCase);
        var paths = p.Files.Values
            .Select(f => f.Path)
            .Where(x => re is null || re.IsMatch(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (o.Out is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);
            File.WriteAllLines(o.Out, paths);
            Console.WriteLine($"{paths.Count} paths -> {o.Out}");
        }
        else
        {
            foreach (var x in paths) Console.WriteLine(x);
        }
        return 0;
    }

    static string ExportJson(DefaultFileProvider p, string path)
    {
        var pkg = p.LoadPackage(path);
        return JsonConvert.SerializeObject(pkg.GetExports(), Formatting.Indented);
    }

    static int Export(Options o)
    {
        if (o.Positional.Count != 1) return Fail("export needs exactly one package path");
        using var p = Open(o);
        var json = ExportJson(p, o.Positional[0]);
        if (o.Out is null) Console.WriteLine(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);
            File.WriteAllText(o.Out, json);
            Console.WriteLine($"{o.Positional[0]} -> {o.Out} ({json.Length / 1024} KiB)");
        }
        return 0;
    }

    static int ExportMany(Options o)
    {
        if (o.Filter is null || o.Out is null) return Fail("export-many needs --filter and --out");
        using var p = Open(o);
        var re = new Regex(o.Filter, RegexOptions.IgnoreCase);
        var paths = p.Files.Values.Select(f => f.Path)
            .Where(x => re.IsMatch(x) && (x.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(o.Limit ?? int.MaxValue)
            .ToList();
        int ok = 0, bad = 0;
        foreach (var path in paths)
        {
            var dest = Path.Combine(o.Out, path.Replace('/', Path.DirectorySeparatorChar) + ".json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, ExportJson(p, path));
                ok++;
            }
            catch (Exception e)
            {
                bad++;
                Console.Error.WriteLine($"FAIL {path}: {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine($"exported {ok}, failed {bad} -> {o.Out}");
        return bad == 0 ? 0 : 3;
    }

    static int PatchF32(Options o)
    {
        if (o.Positional.Count != 1 || o.Out is null) return Fail("patch-f32 needs one input file and --out");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var expect = float.Parse(o.Named["expect"], inv);
        var value = float.Parse(o.Named["value"], inv);
        var pattern = new List<byte>(BitConverter.GetBytes(expect));
        foreach (var d in o.Named.GetValueOrDefault("context", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            pattern.AddRange(BitConverter.GetBytes(double.Parse(d, inv)));
        pattern.AddRange(Convert.FromHexString(o.Named.GetValueOrDefault("context-hex", "")));
        var data = File.ReadAllBytes(o.Positional[0]);
        var hits = new List<int>();
        for (int i = data.AsSpan().IndexOf(pattern.ToArray()); i >= 0;)
        {
            hits.Add(i);
            var next = data.AsSpan(i + 1).IndexOf(pattern.ToArray());
            i = next < 0 ? -1 : i + 1 + next;
        }
        if (hits.Count != 1) throw new InvalidOperationException($"pattern matched {hits.Count} times; refine --context");
        BitConverter.GetBytes(value).CopyTo(data, hits[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);
        File.WriteAllBytes(o.Out, data);
        Console.WriteLine($"patched f32 at offset {hits[0]}: {expect} -> {value} -> {o.Out}");
        return 0;
    }

    static int Raw(Options o)
    {
        if (o.Positional.Count != 1 || o.Out is null) return Fail("raw needs one package path and --out");
        using var p = Open(o);
        var stem = o.Positional[0];
        var baseNoExt = stem[..stem.LastIndexOf('.')];
        foreach (var ext in new[] { Path.GetExtension(stem), ".uexp", ".ubulk", ".uptnl" })
        {
            var candidate = baseNoExt + ext;
            if (!p.TrySaveAsset(candidate, out var data)) continue;
            var dest = Path.Combine(o.Out, candidate.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, data);
            Console.WriteLine($"{candidate} -> {dest} ({data.Length} bytes)");
        }
        return 0;
    }
}

internal sealed class Options
{
    public string Game = Environment.GetEnvironmentVariable("ACR_GAME_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally";
    public string Usmap = Environment.GetEnvironmentVariable("ACR_USMAP") ?? @"C:\Dumper-7\5.6.1-0+UE5-acr\Mappings\5.6.1-0+UE5-acr.usmap";
    public string? Filter;
    public string? Out;
    public int? Limit;
    public List<string> Positional = new();
    public Dictionary<string, string> Named = new();

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            switch (a[i])
            {
                case "--game": o.Game = Next(); break;
                case "--usmap": o.Usmap = Next(); break;
                case "--filter": o.Filter = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--limit": o.Limit = int.Parse(Next()); break;
                default:
                    if (a[i].StartsWith("--")) o.Named[a[i][2..]] = Next();
                    else o.Positional.Add(a[i]);
                    break;
            }
        }
        return o;
    }
}
