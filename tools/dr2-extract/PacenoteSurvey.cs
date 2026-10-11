using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Dr2Extract;

internal sealed record StageRef(string Archive, string Country, string Location, int Route)
{
    public string Id => $"{Location}_route_{Route}";

    public string LocDir => $"tracks/locations/{Country}/{Location}";

    public string RouteDir => $"{LocDir}/route_{Route}";

    public string CodriverPath => $"{RouteDir}/{Location}_codriver_1_pro_{Route}_data.xml";

    /// <summary>Every route with a progress_track.xml, location archive by location archive (each opened in turn).</summary>
    public static async IAsyncEnumerable<StageRef> Enumerate(Dr2Install install, Regex filter)
    {
        foreach (var archive in install.LocationArchives().Where(a => filter.IsMatch(Path.GetFileName(a))))
        {
            await install.OpenLocationAsync(archive);
            var parts = Path.GetFileNameWithoutExtension(archive).Split("__");
            var locDir = $"tracks/locations/{parts[0]}/{parts[1]}";
            var routes = install.List($"{locDir}/route_").Select(p => p.Split('/')[4]).Distinct()
                .Select(r => int.Parse(r["route_".Length..], CultureInfo.InvariantCulture)).Order().ToList();
            foreach (var route in routes.Where(r => install.Exists($"{locDir}/route_{r}/progress_track.xml")))
            {
                yield return new StageRef(archive, parts[0], parts[1], route);
            }
        }
    }
}

/// <summary>
/// <c>pacenote-survey</c>: field statistics of every codriver file plus geometric checks (turn direction of corner calls,
/// distance origin) used to decode the format. Writes work/dr2/pacenote-survey.md.
/// </summary>
internal static class PacenoteSurvey
{
    public static async Task<int> Run(string[] args)
    {
        var install = await Dr2Install.OpenAsync(Program.Option(args, "--dr2") ?? Dr2Install.DefaultRoot,
            Program.Option(args, "--cache") ?? Path.Combine("work", "dr2", "cache"));
        var filter = new Regex(Program.Option(args, "--filter") ?? "_rally_", RegexOptions.IgnoreCase);
        var outPath = Program.Option(args, "--out") ?? Path.Combine("work", "dr2", "pacenote-survey.md");

        var fields = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string key) => fields[key] = fields.GetValueOrDefault(key) + 1;
        var turns = new SortedDictionary<string, int[]>(StringComparer.Ordinal); // key -> [right, left] (RH frame)
        var curvatureByAngle = new SortedDictionary<int, List<float>>();
        var stageRows = new List<string>();
        var gaps = new SortedDictionary<string, List<float>>(StringComparer.Ordinal);

        await foreach (var st in StageRef.Enumerate(install, filter))
        {
            if (!install.Exists(st.CodriverPath))
            {
                stageRows.Add($"| `{st.Id}` | no codriver file |||||||");
                continue;
            }

            var progress = ProgressTrack.Load(await install.FetchAsync($"{st.RouteDir}/progress_track.xml"));
            var codriver = Codriver.Load(await install.FetchAsync(st.CodriverPath));
            var line = new RouteLine(progress);
            var s0 = progress.GateOf("start").Distance;
            var finish = progress.GateOf("finish").Distance;
            var leadsA = new List<float>();
            var leadsB = new List<float>();
            float? finishCall = null;
            foreach (var call in codriver.Calls)
            {
                foreach (var (sub, i) in call.Subcalls.Select((s, i) => (s, i)))
                {
                    if (sub.Type == 0 && sub.Angle == 0 && sub.DistanceLink == 0)
                    {
                        continue;
                    }

                    Count($"type={sub.Type}");
                    Count($"type={sub.Type} angle={sub.Angle}");
                    Count($"type={sub.Type} modifierA={sub.ModifierA}");
                    Count($"type={sub.Type} modifierB={sub.ModifierB}");
                    Count($"type={sub.Type} distanceLink={sub.DistanceLink}");
                    Count($"type={sub.Type} canConcat={sub.CanConcatModA}/{sub.CanConcatModB}");
                    Count($"subcall#{i} delay={(sub.Delay == 0 ? "0" : ">0")}");
                    if (sub.Type == 6)
                    {
                        finishCall ??= call.Distance;
                    }
                }

                var callIndex = codriver.Calls.IndexOf(call);
                var last = call.Subcalls.LastOrDefault(s => s.Type != 0 || s.Angle != 0 || s.DistanceLink != 0);
                if (last is not null && callIndex + 1 < codriver.Calls.Count && last.DistanceLink != 0)
                {
                    var key = last.Type == 3 ? $"type 3 code {last.DistanceLink}" : $"corner link {last.DistanceLink}";
                    gaps.TryAdd(key, []);
                    gaps[key].Add(codriver.Calls[callIndex + 1].Distance - call.Distance);
                }

                var corner = call.Subcalls.FirstOrDefault(s => s.Type is 1 or 2);
                if (corner is null || call.Subcalls[0] != corner)
                {
                    continue;
                }

                if (corner.Angle >= 65)
                {
                    foreach (var (from, to) in Windows)
                    {
                        var turn = HeadingChange(line, s0 + call.Distance + from, s0 + call.Distance + to);
                        if (MathF.Abs(turn) < 0.5f)
                        {
                            continue;
                        }

                        var key = $"net heading [{from},{to}] type={corner.Type}";
                        turns.TryAdd(key, new int[2]);
                        turns[key][turn > 0 ? 0 : 1]++;
                    }
                }

                foreach (var (metric, offset, leads) in new[] { ("A", 0f, leadsA), ("B", s0, leadsB) })
                {
                    var from = call.Distance + offset;
                    var (s, k) = line.PeakCurvature(from, from + 120);
                    if (MathF.Abs(k) < 0.01f)
                    {
                        continue;
                    }

                    var key = $"{metric} type={corner.Type}";
                    turns.TryAdd(key, new int[2]);
                    turns[key][k > 0 ? 0 : 1]++;
                    if (corner.Angle >= 100)
                    {
                        leads.Add(s - from);
                    }

                    if (metric == "A")
                    {
                        curvatureByAngle.TryAdd(corner.Angle, []);
                        curvatureByAngle[corner.Angle].Add(MathF.Abs(k));
                    }
                }
            }

            var shake = progress.Splits.FirstOrDefault(s => s.Type == "shake_finish");
            stageRows.Add(string.Create(CultureInfo.InvariantCulture,
                $"| `{st.Id}` | {s0:F1} | {finish:F1} | {codriver.Calls.FirstOrDefault()?.Distance:F1} | {codriver.Calls.LastOrDefault()?.Distance:F1} | " +
                $"{finishCall:F1} | {codriver.ShakedownCutoff:F0} / {codriver.FinishCallDistance:F0} | " +
                $"{(shake is null ? "" : progress.Gates[shake.Gate].Distance.ToString("F1", CultureInfo.InvariantCulture))} | " +
                $"{Median(leadsA):F1} | {Median(leadsB):F1} | {leadsA.Count} |"));
            Console.WriteLine(stageRows[^1]);
        }

        var md = new StringBuilder();
        md.AppendLine("# Pacenote survey (generated by `dr2-extract pacenote-survey`)");
        md.AppendLine();
        md.AppendLine("Metric A: distanceAlongTrack is a progress-track distance (gates[].distance). Metric B: it is measured from the start gate.");
        md.AppendLine("Lead = distance from the call to the curvature peak within 120 m (hairpin calls, angle >= 100), median per stage.");
        md.AppendLine();
        md.AppendLine("| stage | start gate | finish gate | first call | last call | finish call (type 6) | shakedown cutoff / finish call | shake_finish gate | median lead A | median lead B | hairpins |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|");
        stageRows.ForEach(r => md.AppendLine(r));
        md.AppendLine();
        md.AppendLine("## Turn direction of the first corner subcall (curvature peak within 120 m, |k| >= 0.01)");
        md.AppendLine();
        md.AppendLine("| metric / type | towards cross(f,+Y) | towards cross(+Y,f) |");
        md.AppendLine("|---|---:|---:|");
        foreach (var (k, v) in turns)
        {
            md.AppendLine($"| {k} | {v[0]} | {v[1]} |");
        }

        md.AppendLine();
        md.AppendLine("## Gap to the next call when a call ends with a distanceLink");
        md.AppendLine();
        md.AppendLine("| last subcall | calls | median gap (m) | p25 | p75 |");
        md.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (k, v) in gaps.OrderBy(kv => Median(kv.Value)))
        {
            var o = v.Order().ToList();
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {k} | {v.Count} | {Median(v):F0} | {o[o.Count / 4]:F0} | {o[o.Count * 3 / 4]:F0} |"));
        }

        md.AppendLine();
        md.AppendLine("## Curvature vs angle (metric A, first corner subcall)");
        md.AppendLine();
        md.AppendLine("| angle | calls | median peak radius (m) |");
        md.AppendLine("|---:|---:|---:|");
        foreach (var (a, ks) in curvatureByAngle)
        {
            md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {a} | {ks.Count} | {1 / Median(ks):F1} |"));
        }

        md.AppendLine();
        md.AppendLine("## Field histograms (non-empty subcalls)");
        md.AppendLine();
        md.AppendLine("| field | count |");
        md.AppendLine("|---|---:|");
        foreach (var (k, v) in fields)
        {
            md.AppendLine($"| {k} | {v} |");
        }

        File.WriteAllText(outPath, md.ToString());
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private static readonly (float From, float To)[] Windows =
        Enumerable.Range(0, 16).Select(i => (i * 25f, i * 25f + 75f)).ToArray();

    /// <summary>Signed heading change (rad, positive towards cross(f,+Y)) between two distances.</summary>
    private static float HeadingChange(RouteLine line, float from, float to)
    {
        var total = 0f;
        var prev = line.Heading(from);
        for (var s = from + 4; s <= to; s += 4)
        {
            var h = line.Heading(s);
            total += MathF.Atan2(-Vector3.Cross(prev, h).Y, Vector3.Dot(prev, h));
            prev = h;
        }

        return total;
    }

    private static float Median(List<float> v)
    {
        if (v.Count == 0)
        {
            return float.NaN;
        }

        var s = v.Order().ToList();
        return s[s.Count / 2];
    }
}
