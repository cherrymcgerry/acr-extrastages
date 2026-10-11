using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcrRef;

/// Streams an `export` JSON (array of exports), prints a Type histogram and writes the exports
/// whose Type or Name matches --filter to --out.
internal static class ExportFilter
{
    public static int Run(Options o)
    {
        if (o.Positional.Count != 1) throw new ArgumentException("exports needs the JSON produced by `export`");
        var re = o.Filter is null ? null : new Regex(o.Filter, RegexOptions.IgnoreCase);
        var hist = new SortedDictionary<string, int>();
        var picked = new JArray();
        using var reader = new JsonTextReader(File.OpenText(o.Positional[0])) { FloatParseHandling = FloatParseHandling.Double };
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonToken.StartObject)
        {
            var e = JObject.Load(reader);
            var type = (string?)e["Type"] ?? "?";
            var name = (string?)e["Name"] ?? "?";
            hist[type] = hist.GetValueOrDefault(type) + 1;
            if (re is not null && (re.IsMatch(type) || re.IsMatch(name))) picked.Add(e);
        }

        foreach (var (k, v) in hist.OrderByDescending(kv => kv.Value)) Console.WriteLine($"{v,7} {k}");
        Console.WriteLine($"{hist.Values.Sum(),7} total exports");
        if (re is not null && o.Out is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);
            File.WriteAllText(o.Out, picked.ToString(Formatting.Indented));
            Console.WriteLine($"{picked.Count} matching exports -> {o.Out}");
        }
        return 0;
    }
}
