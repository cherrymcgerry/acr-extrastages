using EgoEngineLibrary.Graphics.Pssg;

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
