using System.IO.Abstractions;
using System.Text.RegularExpressions;
using VictorBush.Ego.NefsLib;
using VictorBush.Ego.NefsLib.IO;
using VictorBush.Ego.NefsLib.Item;
using VictorBush.Ego.NefsLib.Progress;

namespace Dr2Extract;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "scan" => await Scan(args[1..]),
                "list" => await List(args[1..]),
                "extract" => await Extract(args[1..]),
                "xml" => XmlCommands.Convert(args[1..]),
                "lng" => LngCommands.Dump(args[1..]),
                "tpk-to-png" => TextureCommands.TpkToPng(args[1..]),
                "track-to-gltf" => TrackCommands.ToGltf(args[1..]),
                "track-info" => TrackCommands.Info(args[1..]),
                "ground-height" => TrackCommands.Height(args[1..]),
                "pssg-info" => PssgCommands.Info(args[1..]),
                "pssg-to-gltf" => PssgCommands.ToGltf(args[1..]),
                "pssg-textures" => PssgCommands.Textures(args[1..]),
                "terrain-info" => TerrainInfo.Run(args[1..]),
                "stage-ir" => await StageIr.Run(args[1..]),
                "stage-textures" => await StageTextures.Run(args[1..]),
                "catalog" => await CatalogCommand.Run(args[1..]),
                "pacenote-survey" => await PacenoteSurvey.Run(args[1..]),
                "route-map-check" => await RouteMapCheck.Run(args[1..]),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Environment.GetEnvironmentVariable("DR2_DEBUG") is null ? $"error: {ex.Message}" : $"error: {ex}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            dr2-extract <command> ...
              scan <dr2-root>                            read every location archive header, report readability
              list <archive.nefs> [--filter <regex>]     list files (path<TAB>size)
              extract <archive.nefs> <out-dir> [--filter <regex>]
              xml <file.xml|dir> [out.xml]               binary XML to text (dir: writes *.text.xml alongside)
              lng <language_xxx.lng> [--filter <regex>]  dump localisation key/value pairs
              track-to-gltf <track.jpk|.vcqtc|.cqtc> <out.glb>   physics ground -> glTF (one primitive per surface)
              track-info <track.jpk> [--max N] [--dump <dir>]  list vcqtc cells / dump raw cells
              ground-height <track.jpk> <x> <z> [<x> <z> ...]  physics ground height probe
              pssg-info <file.pssg> [--depth N] [--siblings N] [--library <type>]  element tree summary
              pssg-to-gltf <file.pssg> <out.glb> [--bounds minx,minz,maxx,maxz] [--node <id-substring>]
                           [--textures <dir> [--texture-pssg <a.pssg;b.pssg>]]   render meshes (world space) + DDS
              stage-ir <stage_id> [--render] [--corridor 250] [--out work/ir] [--dr2 <dir>] [--cache <dir>]
                       [--surface-map config/surface-map.toml]   DR2 stage -> work/ir/<stage_id>/ (docs/stage-ir.md)
              catalog [--filter _rally_] [--out work/dr2/catalog]   all stages + surface codes vs surface map
              pacenote-survey [--filter _rally_] [--out work/dr2/pacenote-survey.md]   codriver field stats + geometry checks
            DR2 install: --dr2, else %DR2_DIR%, else C:\Games\DiRT Rally 2.0. Set DR2_DEBUG=1 for stack traces,
            DR2_PSSG_NODES=1 to list render nodes during pssg export.
            """);
        return 2;
    }

    internal static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static string[] Positional(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                i++;
                continue;
            }

            result.Add(args[i]);
        }

        return result.ToArray();
    }

    internal static async Task<NefsArchive> Open(string path)
    {
        var fs = new FileSystem();
        var reader = new NefsReader(fs);
        var progress = new NefsProgress(CancellationToken.None);
        if (!path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        {
            return await reader.ReadArchiveAsync(path, progress);
        }

        // game\*.dat archives are headless: their NeFS headers live in the game executable.
        var full = Path.GetFullPath(path);
        var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(full)!)!, "dirtrally2.exe");
        var sources = await new NefsExeHeaderFinder(fs).FindHeadersAsync(exe, Path.GetDirectoryName(full)!, false, progress);
        var source = sources.FirstOrDefault(s => string.Equals(Path.GetFullPath(s.FilePath), full, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"no header for {Path.GetFileName(full)} in {exe}; found: {string.Join(", ", sources.Select(s => Path.GetFileName(s.FilePath)))}");
        return await reader.ReadArchiveAsync(source, progress);
    }

    internal static IEnumerable<(NefsItem Item, string Path)> Files(NefsArchive archive, string? filter)
    {
        var regex = filter is null ? null : new Regex(filter, RegexOptions.IgnoreCase);
        foreach (var item in archive.Items.EnumerateDepthFirstByName())
        {
            if (item.Type != NefsItemType.File)
            {
                continue;
            }

            var path = archive.Items.GetItemFilePath(item.Id).Replace('\\', '/');
            if (regex is null || regex.IsMatch(path))
            {
                yield return (item, path);
            }
        }
    }

    private static async Task<int> Scan(string[] args)
    {
        var root = Positional(args)[0];
        var failures = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "locations"), "*.nefs").Order())
        {
            try
            {
                var archive = await Open(file);
                var files = Files(archive, null).ToList();
                var tracks = files.Count(f => f.Path.EndsWith(".track.jpk", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"ok\t{Path.GetFileName(file)}\tfiles={files.Count}\ttrack.jpk={tracks}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL\t{Path.GetFileName(file)}\t{ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> List(string[] args)
    {
        var archive = await Open(Positional(args)[0]);
        foreach (var (item, path) in Files(archive, Option(args, "--filter")))
        {
            Console.WriteLine($"{path}\t{item.ExtractedSize}");
        }

        return 0;
    }

    private static async Task<int> Extract(string[] args)
    {
        var pos = Positional(args);
        var archive = await Open(pos[0]);
        var outDir = pos[1];
        var transformer = new NefsTransformer(new FileSystem());
        var progress = new NefsProgress(CancellationToken.None);
        var count = 0;
        foreach (var (item, path) in Files(archive, Option(args, "--filter")))
        {
            var target = Path.Combine(outDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await transformer.DetransformAsync(item.DataSource, target, 0, progress);
            count++;
        }

        Console.WriteLine($"extracted {count} files to {outDir}");
        return 0;
    }
}
