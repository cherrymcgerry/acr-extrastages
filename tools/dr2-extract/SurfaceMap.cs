using System.Text.RegularExpressions;

namespace Dr2Extract;

/// <summary>
/// <c>config/surface-map.toml</c>: DR2 4-char surface code -> ACR physical material (<c>[surfaces]</c>), DR2 object
/// shape class (physparams.xml TEMPLATESHAPEPROPS id) -> ACR wall material (<c>[walls]</c>) and template-id prefix
/// overrides for walls (<c>[wall_templates]</c>). Only the subset of TOML used by that file is accepted (tables of
/// quoted string keys and values, plus comments).
/// </summary>
internal sealed partial class SurfaceMap
{
    public const string AssetPrefix = "/Game/Data/Gameplay/PhysicalMaterials/";

    /// <summary>Wall value meaning "not a collision object in ACR" (e.g. DR2 spawn markers).</summary>
    public const string NoCollision = "none";

    public static readonly IReadOnlySet<string> AcrMaterials = new HashSet<string>
    {
        "PM_SurfaceCobblestones", "PM_SurfaceConcrete", "PM_SurfaceDeepSnow", "PM_SurfaceGrass", "PM_SurfaceGravelFine",
        "PM_SurfaceGravelHard", "PM_SurfaceKerb", "PM_SurfaceMetal", "PM_SurfaceRock", "PM_SurfaceSand", "PM_SurfaceSnow",
        "PM_SurfaceTarmacSmooth", "PM_WallBasic", "PM_WallBush", "PM_WallConcrete", "PM_WallMetalGuardrail",
        "PM_WallMetalPole", "PM_WallRock", "PM_WallWood", "PM_WallWoodFence", "PM_WallWoodPole", "PM_WallWoodTreeTrunks",
        "PM_WallPlasticBarrier", "PM_WallHayBale", "PM_WallTire", "PM_Fallback",
    };

    private readonly Dictionary<string, string> _map;
    private readonly Dictionary<string, string> _walls;
    private readonly List<(string Prefix, string Material)> _wallTemplates;

    public string Path { get; }

    private SurfaceMap(string path, Dictionary<string, string> map, Dictionary<string, string> walls,
        List<(string, string)> wallTemplates)
    {
        Path = path;
        _map = map;
        _walls = walls;
        _wallTemplates = wallTemplates.OrderByDescending(t => t.Item1.Length).ToList();
    }

    [GeneratedRegex("""^\s*(?:"(?<key>[^"]+)"|(?<key>[A-Za-z0-9_\-]+))\s*=\s*"(?<value>[^"]*)"\s*(?:#.*)?$""")]
    private static partial Regex Entry();

    public static SurfaceMap Load(string path)
    {
        var tables = new Dictionary<string, Dictionary<string, string>>
        {
            ["surfaces"] = new(StringComparer.Ordinal),
            ["walls"] = new(StringComparer.Ordinal),
            ["wall_templates"] = new(StringComparer.Ordinal),
        };
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

            if (table is null || !tables.TryGetValue(table, out var target))
            {
                continue;
            }

            var (key, value) = (m.Groups["key"].Value, m.Groups["value"].Value);
            var allowed = table == "surfaces" ? AcrMaterials.Contains(value)
                : value == NoCollision || (AcrMaterials.Contains(value) && value.StartsWith("PM_Wall", StringComparison.Ordinal));
            if (!allowed)
            {
                throw new InvalidDataException($"{path}:{lineNo}: '{value}' is not a known ACR {(table == "surfaces" ? "physical" : "wall")} material");
            }

            if (!target.TryAdd(key, value))
            {
                throw new InvalidDataException($"{path}:{lineNo}: duplicate key '{key}' in [{table}]");
            }
        }

        return new SurfaceMap(path, tables["surfaces"], tables["walls"],
            tables["wall_templates"].Select(kv => (kv.Key, kv.Value)).ToList());
    }

    public string? Get(string code) => _map.GetValueOrDefault(code);

    /// <summary>
    /// Wall material for a shape: the shape class must be mapped (null otherwise); a template-prefix override then
    /// replaces it unless the class is <see cref="NoCollision"/>.
    /// </summary>
    public string? Wall(string template, string shapeProps)
    {
        if (_walls.GetValueOrDefault(shapeProps) is not { } byClass)
        {
            return null;
        }

        return byClass == NoCollision
            ? byClass
            : _wallTemplates.FirstOrDefault(t => template.StartsWith(t.Prefix, StringComparison.Ordinal)).Material ?? byClass;
    }

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
