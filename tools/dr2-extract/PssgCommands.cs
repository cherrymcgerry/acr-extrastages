using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;

namespace Dr2Extract;

internal static class PssgCommands
{
    public static int Info(string[] args)
    {
        var pos = Program.Positional(args);
        var depth = int.Parse(Program.Option(args, "--depth") ?? "4");
        var pssg = Open(pos[0]);
        Console.WriteLine($"file type {pssg.FileType}");

        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        Count(pssg.RootElement, histogram);
        Console.WriteLine("element counts:");
        foreach (var (name, n) in histogram)
        {
            Console.WriteLine($"  {name,-36} {n}");
        }

        Console.WriteLine("tree:");
        if (Program.Option(args, "--library") is { } libraryType)
        {
            foreach (var library in pssg.RootElement.ChildElements.Where(e =>
                         e.Name == "LIBRARY" && e.Attributes.Any(a => a.Name == "type" && a.DisplayValue == libraryType)))
            {
                Print(library, 0, depth, int.Parse(Program.Option(args, "--siblings") ?? "3"));
            }

            return 0;
        }

        Print(pssg.RootElement, 0, depth, int.Parse(Program.Option(args, "--siblings") ?? "3"));
        return 0;
    }

    public static int ToGltf(string[] args) => PssgMeshExport.Run(args);

    /// <c>pssg-textures &lt;file.pssg&gt; &lt;out-dir&gt; [--filter &lt;regex on id&gt;]</c>: every TEXTURE as PNG (mip 0).
    public static int Textures(string[] args)
    {
        var pos = Program.Positional(args);
        var pssg = Open(pos[0]);
        var filter = new System.Text.RegularExpressions.Regex(Program.Option(args, "--filter") ?? ".");
        Directory.CreateDirectory(pos[1]);
        var ids = new List<string>();
        Collect(pssg.RootElement, ids);
        foreach (var id in ids.Where(i => filter.IsMatch(i)).Order())
        {
            var t = pssg.TryGetObject<PssgTexture>(id.AsMemory())!;
            var png = Path.Combine(pos[1], id + ".png");
            TextureCommands.DdsToPng(t.ToDdsFile(), png);
            Console.WriteLine($"{id}: {t.TexelFormat} {t.Width}x{t.Height}");
        }
        return 0;
    }

    private static void Collect(PssgElement element, List<string> ids)
    {
        if (element.Name == "TEXTURE" && element.Attributes.FirstOrDefault(a => a.Name == "id") is { } id) ids.Add(id.DisplayValue);
        foreach (var child in element.ChildElements) Collect(child, ids);
    }

    internal static PssgFile Open(string path)
    {
        using var stream = File.OpenRead(path);
        return PssgFile.Open(stream);
    }

    private static void Count(PssgElement element, IDictionary<string, int> histogram)
    {
        histogram[element.Name] = histogram.TryGetValue(element.Name, out var n) ? n + 1 : 1;
        foreach (var child in element.ChildElements)
        {
            Count(child, histogram);
        }
    }

    private static void Print(PssgElement element, int level, int maxDepth, int siblings)
    {
        var attrs = string.Join(" ", element.Attributes.Select(a => $"{a.Name}={Abbreviate(a.DisplayValue)}"));
        var value = element.IsDataElement && element.Value.Length > 0 ? $" [{element.Value.Length} bytes]" : "";
        Console.WriteLine($"{new string(' ', level * 2)}{element.Name} {attrs}{value}");
        if (level >= maxDepth)
        {
            if (element.ChildElements.Count > 0)
            {
                Console.WriteLine($"{new string(' ', level * 2 + 2)}... {element.ChildElements.Count} children");
            }

            return;
        }

        // Collapse long runs of same-named siblings.
        foreach (var group in element.ChildElements.GroupBy(c => c.Name))
        {
            var items = group.ToList();
            foreach (var child in items.Take(siblings))
            {
                Print(child, level + 1, maxDepth, siblings);
            }

            if (items.Count > siblings)
            {
                Console.WriteLine($"{new string(' ', level * 2 + 2)}... {items.Count - siblings} more {group.Key}");
            }
        }
    }

    private static string Abbreviate(string s) => s.Length > 80 ? s[..77] + "..." : s;
}
