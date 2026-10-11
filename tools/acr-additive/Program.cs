using AcrRef;

namespace AcrAdditive;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        var o = Opts.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "pkg-info" => PkgInfo(o),
                "who-imports" => WhoImports(o),
                "dt-selftest" => Registry.SelfTest(o),
                "registry" => Registry.Run(o),
                "relocate" => Relocate(o),
                "verify" => Verify.Run(o),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 2;
        }
    }

    const string Usage = """
        acr-additive — additive stage registration (docs/additive-plan.md)

          acr-additive pkg-info <legacy .uasset|.umap> [--names <regex>]   summary, imports and matching names
          acr-additive who-imports <package name>... [--game <dir>]       packages that hard-import them (container headers)
          acr-additive dt-selftest --legacy <root>                         DB table decode/encode round trip
          acr-additive relocate --legacy <root> --out <mod> --map /Game/a=/Game/b... [--object A=B...]
          acr-additive registry --legacy <root> --out <mod> <stage spec dir|json>...
          acr-additive verify --vanilla <dir> --game <dir with mods> --spec <stage spec dir> [--out <report>]
        """;

    static int Fail(string msg) { Console.Error.WriteLine(msg); Console.WriteLine(Usage); return 1; }

    /// relocate --legacy <root> --out <mod root> --map /Game/old=/Game/new [--map ...] [--object Old=New ...]
    static int Relocate(Opts o)
    {
        var rel = new Relocation();
        foreach (var m in o.Multi("map")) { var kv = m.Split('=', 2); rel.Packages.Add((kv[0], kv[1])); }
        foreach (var m in o.Multi("object")) { var kv = m.Split('=', 2); rel.Objects[kv[0]] = kv[1]; }
        var legacy = Path.GetFullPath(o.Named["legacy"]);
        var outRoot = o.Named["out"];
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(legacy, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Game.PackageName(Path.GetRelativePath(legacy, f));
            if (rel.MapPackage(name) is null) continue;
            rel.RelocateFile(f, outRoot, n < 3 || o.Named.ContainsKey("verbose") ? Console.Out : TextWriter.Null);
            n++;
        }
        Console.WriteLine($"relocated {n} packages -> {outRoot}");
        return n > 0 ? 0 : 3;
    }

    static int WhoImports(Opts o)
    {
        using var p = Game.Open(o);
        var index = Game.PackageIndex(p);
        Console.WriteLine($"{index.Count} packages in {p.MountedVfs.Count} containers");
        foreach (var target in o.Positional)
        {
            var id = CUE4Parse.UE4.IO.Objects.FPackageId.FromName(target).id;
            var users = index.Values.SelectMany(l => l).Where(k => k.Imports.Any(x => x.id == id)).Select(k => k.Name).Distinct().OrderBy(x => x).ToList();
            Console.WriteLine($"{target} ({id:X16}, {(index.ContainsKey(id) ? "present" : "absent")}): imported by {users.Count}");
            foreach (var u in users) Console.WriteLine($"    {u}");
        }
        return 0;
    }

    static int PkgInfo(Opts o)
    {
        var pkg = new LegacyPackage(o.Positional[0]);
        Console.WriteLine($"package {pkg.PackageName}: {pkg.Names.Count} names, {pkg.ImportCount} imports, {pkg.ExportCount} exports");
        for (int i = 0; i < pkg.ImportCount; i++)
            if (pkg.ImportOuter(i) == 0) Console.WriteLine($"  import package #{i} {pkg.ImportName(i)}");
        if (o.Named.TryGetValue("names", out var re))
        {
            var r = new System.Text.RegularExpressions.Regex(re);
            for (int n = 0; n < pkg.Names.Count; n++) if (r.IsMatch(pkg.Names[n])) Console.WriteLine($"  name #{n} {pkg.Names[n]}");
        }
        if (o.Named.TryGetValue("raw", out var raw))
        {
            // raw string occurrences in export data (FString payloads are not covered by name renames)
            var pats = new[] { System.Text.Encoding.ASCII.GetBytes(raw), System.Text.Encoding.Unicode.GetBytes(raw) };
            for (int e = 0; e < pkg.ExportCount; e++)
            {
                var d = pkg.GetExportData(e);
                foreach (var pat in pats)
                    for (int i = d.AsSpan().IndexOf(pat); i >= 0;)
                    {
                        int end = Math.Min(d.Length, i + 120);
                        Console.WriteLine($"  raw #{e} {pkg.ExportName(e)} ({pkg.ExportClass(e)}) @{i}: {System.Text.Encoding.Latin1.GetString(d, Math.Max(0, i - 8), end - Math.Max(0, i - 8)).Replace('\0', '.')}");
                        int next = d.AsSpan(i + 1).IndexOf(pat);
                        i = next < 0 ? -1 : i + 1 + next;
                    }
            }
        }
        return 0;
    }
}

internal sealed class Opts
{
    public string Game = Environment.GetEnvironmentVariable("ACR_GAME_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally";
    public string Usmap = Environment.GetEnvironmentVariable("ACR_USMAP") ?? @"C:\Dumper-7\5.6.1-0+UE5-acr\Mappings\5.6.1-0+UE5-acr.usmap";
    public List<string> Positional = new();
    public Dictionary<string, string> Named = new();
    readonly List<(string key, string value)> _all = new();

    public IEnumerable<string> Multi(string key) => _all.Where(x => x.key == key).Select(x => x.value);

    public static Opts Parse(string[] a)
    {
        var o = new Opts();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) { o.Positional.Add(a[i]); continue; }
            var k = a[i];
            var v = i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{k} needs a value");
            switch (k)
            {
                case "--game": o.Game = v; break;
                case "--usmap": o.Usmap = v; break;
                default: o.Named[k[2..]] = v; break;
            }
            o._all.Add((k[2..], v));
        }
        return o;
    }
}
