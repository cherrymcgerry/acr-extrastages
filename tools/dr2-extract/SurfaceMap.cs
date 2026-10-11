using System.Text.RegularExpressions;

namespace Dr2Extract;

/// <summary>
/// <c>config/surface-map.toml</c>: DR2 4-char surface code -> ACR physical material. Only the subset of TOML used by
/// that file is accepted (a <c>[surfaces]</c> table of quoted string keys and values, plus comments).
/// </summary>
internal sealed partial class SurfaceMap
{
    public const string AssetPrefix = "/Game/Data/Gameplay/PhysicalMaterials/";

    public static readonly IReadOnlySet<string> AcrMaterials = new HashSet<string>
    {
        "PM_SurfaceCobblestones", "PM_SurfaceConcrete", "PM_SurfaceDeepSnow", "PM_SurfaceGrass", "PM_SurfaceGravelFine",
        "PM_SurfaceGravelHard", "PM_SurfaceKerb", "PM_SurfaceMetal", "PM_SurfaceRock", "PM_SurfaceSand", "PM_SurfaceSnow",
        "PM_SurfaceTarmacSmooth", "PM_WallBasic", "PM_WallRock", "PM_WallWood", "PM_WallConcrete", "PM_WallMetalGuardrail",
        "PM_WallBush", "PM_Fallback",
    };

    private readonly Dictionary<string, string> _map;

    public string Path { get; }

    private SurfaceMap(string path, Dictionary<string, string> map)
    {
        Path = path;
        _map = map;
    }

    [GeneratedRegex("""^\s*(?:"(?<key>[^"]+)"|(?<key>[A-Za-z0-9_\-]+))\s*=\s*"(?<value>[^"]*)"\s*(?:#.*)?$""")]
    private static partial Regex Entry();

    public static SurfaceMap Load(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? table = null;
        var lineNo = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                table = line.Split('#')[0].Trim().Trim('[', ']').Trim();
                continue;
            }

            var m = Entry().Match(line);
            if (!m.Success)
            {
                throw new InvalidDataException($"{path}:{lineNo}: cannot parse '{raw}'");
            }

            if (table != "surfaces")
            {
                continue;
            }

            var (key, value) = (m.Groups["key"].Value, m.Groups["value"].Value);
            if (!AcrMaterials.Contains(value))
            {
                throw new InvalidDataException($"{path}:{lineNo}: '{value}' is not a known ACR physical material");
            }

            if (!map.TryAdd(key, value))
            {
                throw new InvalidDataException($"{path}:{lineNo}: duplicate surface '{key}'");
            }
        }

        return new SurfaceMap(path, map);
    }

    public string? Get(string code) => _map.GetValueOrDefault(code);

    /// <summary>Maps every code or throws listing all unmapped ones.</summary>
    public Dictionary<string, string> Resolve(IEnumerable<string> codes)
    {
        var distinct = codes.Distinct().Order(StringComparer.Ordinal).ToList();
        var missing = distinct.Where(c => !_map.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"unmapped DR2 surface(s) {string.Join(", ", missing.Select(c => $"'{c}'"))}; add them to [surfaces] in {Path}");
        }

        return distinct.ToDictionary(c => c, c => _map[c]);
    }
}
