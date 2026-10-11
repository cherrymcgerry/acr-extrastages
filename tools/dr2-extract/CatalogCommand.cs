using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dr2Extract;

/// <summary>
/// <c>catalog</c>: every rally stage in the install (name, length, gates, pacenotes) plus the physics-ground surface codes
/// within the route corridor, checked against config/surface-map.toml. Writes JSON and Markdown.
/// </summary>
internal static class CatalogCommand
{
    public static async Task<int> Run(string[] args)
    {
        var root = Program.Option(args, "--dr2") ?? Dr2Install.DefaultRoot;
        var cache = Program.Option(args, "--cache") ?? Path.Combine("work", "dr2", "cache");
        var outPrefix = Program.Option(args, "--out") ?? Path.Combine("work", "dr2", "catalog");
        var corridor = float.Parse(Program.Option(args, "--corridor") ?? "250", System.Globalization.CultureInfo.InvariantCulture);
        var filter = new Regex(Program.Option(args, "--filter") ?? "_rally_", RegexOptions.IgnoreCase);
        var surfaceMap = SurfaceMap.Load(Program.Option(args, "--surface-map") ?? Path.Combine("config", "surface-map.toml"));

        var install = await Dr2Install.OpenAsync(root, cache);
        var lng = LngCommands.Load(await install.FetchAsync("language/language_eng.lng"));
        var surfaces = SurfaceMaterials.Load(await install.FetchAsync("surface_materials.xml"));
        var stages = new List<Dictionary<string, object?>>();
        var surfaceTotals = new SortedDictionary<string, long>(StringComparer.Ordinal);

        foreach (var archive in install.LocationArchives().Where(a => filter.IsMatch(Path.GetFileName(a))))
        {
            await install.OpenLocationAsync(archive);
            var (country, location) = (Path.GetFileNameWithoutExtension(archive).Split("__")[0], Path.GetFileNameWithoutExtension(archive).Split("__")[1]);
            var locDir = $"tracks/locations/{country}/{location}";
            var routes = install.List($"{locDir}/route_").Select(p => p.Split('/')[4]).Distinct()
                .Select(r => int.Parse(r["route_".Length..])).Order().ToList();
            var groundCache = new Dictionary<string, List<GroundTriangle>>();
            foreach (var route in routes)
            {
                var routeDir = $"{locDir}/route_{route}";
                if (!install.Exists($"{routeDir}/progress_track.xml"))
                {
                    continue;
                }

                try
                {
                    var progress = ProgressTrack.Load(await install.FetchAsync($"{routeDir}/progress_track.xml"));
                    var codriverPath = $"{routeDir}/{location}_codriver_1_pro_{route}_data.xml";
                    var codriver = install.Exists(codriverPath) ? Codriver.Load(await install.FetchAsync(codriverPath)) : null;
                    var jpk = await install.FetchAsync($"{routeDir}/track.jpk", temporary: true);
                    string key;
                    List<GroundTriangle>? triangles;
                    try
                    {
                        key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(jpk)));
                        if (!groundCache.TryGetValue(key, out triangles))
                        {
                            groundCache[key] = triangles = Dr2Ground.ReadJpk(jpk).Cells.SelectMany(c => c.Triangles).ToList();
                        }
                    }
                    finally
                    {
                        File.Delete(jpk);
                    }

                    var inCorridor = new RouteCorridor(progress.Gates.Select(g => g.Crossing).ToList(), corridor);
                    var codes = triangles.Where(t => inCorridor.Contains(t.Centroid)).GroupBy(t => t.Material)
                        .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
                    foreach (var (code, count) in codes)
                    {
                        surfaceTotals[code] = surfaceTotals.GetValueOrDefault(code) + count;
                    }

                    var start = progress.GateOf("start");
                    var finish = progress.GateOf("finish");
                    var stage = new Dictionary<string, object?>
                    {
                        ["id"] = $"{location}_route_{route}",
                        ["name"] = StageNames.Stage(lng, location, route),
                        ["location"] = StageNames.Location(lng, location, country),
                        ["archive"] = Path.GetFileName(archive),
                        ["length_m"] = MathF.Round(finish.Distance - start.Distance, 1),
                        ["total_distance_m"] = progress.TotalDistance,
                        ["gates"] = progress.Gates.Count,
                        ["splits"] = string.Join(",", progress.Splits.Select(s => s.Type)),
                        ["pacenotes"] = codriver?.Calls.Count,
                        ["codriver_source"] = codriver is null ? null : install.Sources[codriverPath],
                        ["ground"] = key[..Math.Min(key.Length, 20)],
                        ["surfaces"] = codes,
                    };
                    stages.Add(stage);
                    Console.WriteLine($"{stage["id"],-36} {stage["length_m"],8} m  {stage["name"]}  [{string.Join(" ", codes.Keys)}]");
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
                {
                    Console.WriteLine($"FAIL {location}_route_{route}: {ex.Message}");
                    stages.Add(new Dictionary<string, object?>
                    {
                        ["id"] = $"{location}_route_{route}",
                        ["error"] = ex.Message,
                        ["surfaces"] = new Dictionary<string, int>(),
                    });
                }
            }
        }

        var unmapped = surfaceTotals.Keys.Where(k => surfaceMap.Get(k) is null).ToList();
        if (unmapped.Count > 0)
        {
            Console.WriteLine($"unmapped surface codes: {string.Join(" ", unmapped)}");
        }

        var surfaceRows = surfaceTotals.Select(kv =>
        {
            surfaces.TryGetValue(kv.Key, out var s);
            return new { code = kv.Key, triangles = kv.Value, acr_physical_material = surfaceMap.Get(kv.Key), dr2 = s };
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPrefix))!);
        File.WriteAllText(outPrefix + ".json", JsonSerializer.Serialize(new { stages, surfaces = surfaceRows }, Json.Options));

        var md = new StringBuilder();
        md.AppendLine("| id | name | location | start->finish (m) | gates | pacenotes | surfaces |");
        md.AppendLine("|---|---|---|---:|---:|---:|---|");
        foreach (var s in stages)
        {
            md.AppendLine($"| `{s["id"]}` | {s["name"]} | {s["location"]} | {s["length_m"]:F1} | {s["gates"]} | {s["pacenotes"]} | " +
                          $"{string.Join(" ", ((Dictionary<string, int>)s["surfaces"]!).Keys)} |");
        }

        md.AppendLine();
        md.AppendLine("| code | triangles (in corridors) | base chain | mechanics | particle | ACR material |");
        md.AppendLine("|---|---:|---|---|---|---|");
        foreach (var r in surfaceRows)
        {
            md.AppendLine($"| `{r.code}` | {r.triangles} | {string.Join(" > ", r.dr2?.BaseChain ?? [])} | {r.dr2?.Mechanics} | {r.dr2?.Particle} | {r.acr_physical_material ?? "**unmapped**"} |");
        }

        File.WriteAllText(outPrefix + ".md", md.ToString());
        Console.WriteLine($"{stages.Count} stages, {surfaceTotals.Count} surface codes; wrote {outPrefix}.json/.md");
        return 0;
    }
}
