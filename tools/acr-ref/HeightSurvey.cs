using System.Globalization;

namespace AcrRef;

/// `height-survey --ir <dir> --legacy <vanilla legacy root>`: host ground height (unmodified BakedData, which follows the
/// visible host terrain) under each placed DR2 gate vs the DR2 road height there. Physics metres, y up.
internal static class HeightSurvey
{
    const double Cell = 4;

    /// `ground-at --legacy <built mod root> --points x,z[;x,z...]`: highest BakedData mesh surface at physics x,z.
    public static int GroundAt(Options o)
    {
        var root = o.Named.GetValueOrDefault("legacy") ?? throw new ArgumentException("--legacy is required");
        var data = File.ReadAllBytes(Path.ChangeExtension(HostPaths.Resolve(root, HostPaths.Baked), ".uexp"));
        var (cols, _, _) = BakedEdit.ParseColliders(data);
        foreach (var pt in (o.Named.GetValueOrDefault("points") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = pt.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var h = BakedEdit.GroundHeight(cols, new System.Numerics.Vector2(v[0], v[1]));
            Console.WriteLine(FormattableString.Invariant($"{v[0]:0.00},{v[1]:0.00}: ground {(h is null ? "none" : h.Value.ToString("0.000", CultureInfo.InvariantCulture))}"));
        }
        return 0;
    }

    public static int Run(Options o)
    {
        var ir = StageBuild.Ir.Load(o.Named.GetValueOrDefault("ir") ?? throw new ArgumentException("--ir is required"));
        var legacy = o.Named.GetValueOrDefault("legacy") ?? throw new ArgumentException("--legacy is required");
        var pl = StageBuild.Solve(ir, double.Parse(o.Named.GetValueOrDefault("ground-offset", "0"), CultureInfo.InvariantCulture));
        var data = File.ReadAllBytes(Path.ChangeExtension(HostPaths.Resolve(legacy, HostPaths.Baked), ".uexp"));
        var (cols, _, _) = BakedEdit.ParseColliders(data);

        // up-facing host triangles rasterised by centroid: highest ground per 4 m cell
        var grid = new Dictionary<(int, int), float>();
        foreach (var c in cols)
        {
            var p = c.Positions;
            for (int t = 0; t + 2 < c.Indices.Length; t += 3)
            {
                uint a = c.Indices[t], b = c.Indices[t + 1], e = c.Indices[t + 2];
                float ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                float vx = p[e * 3] - p[a * 3], vy = p[e * 3 + 1] - p[a * 3 + 1], vz = p[e * 3 + 2] - p[a * 3 + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                if (Math.Abs(ny) < 0.7f * MathF.Sqrt(nx * nx + ny * ny + nz * nz)) continue;
                float cx = (p[a * 3] + p[b * 3] + p[e * 3]) / 3, cy = (p[a * 3 + 1] + p[b * 3 + 1] + p[e * 3 + 1]) / 3, cz = (p[a * 3 + 2] + p[b * 3 + 2] + p[e * 3 + 2]) / 3;
                var k = ((int)Math.Floor(cx / Cell), (int)Math.Floor(cz / Cell));
                if (!grid.TryGetValue(k, out var y) || cy > y) grid[k] = cy;
            }
        }
        float? HostAt(double x, double z)
        {
            int gx = (int)Math.Floor(x / Cell), gz = (int)Math.Floor(z / Cell);
            for (int r = 0; r <= 3; r++)
            {
                float? best = null;
                for (int i = -r; i <= r; i++) for (int j = -r; j <= r; j++)
                    if (grid.TryGetValue((gx + i, gz + j), out var y) && (best is null || y > best)) best = y;
                if (best is not null) return best;
            }
            return null;
        }

        var diffs = new List<double>(); var hosts = new List<double>(); var roads = new List<double>();
        var profile = new System.Text.StringBuilder();
        for (int gi = 0; gi < ir.Gates.Count; gi++)
        {
            var g = ir.Gates[gi];
            var q = pl.ToPhysics(g.crossing);
            var h = HostAt(q.X, q.Z);
            if (gi % 20 == 0)
                profile.AppendLine(FormattableString.Invariant($"  gate {gi,3} s {g.distance,7:0} m: road {q.Y,6:0.0}  host {(h is null ? "  none" : h.Value.ToString("0.0", CultureInfo.InvariantCulture)),6}  at ({q.X:0}, {q.Z:0})"));
            if (h is null) continue;
            diffs.Add(q.Y - h.Value); hosts.Add(h.Value); roads.Add(q.Y);
        }
        Console.Write(profile);
        static string S(List<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            string F(double d) => d.ToString("0.0", CultureInfo.InvariantCulture);
            return $"min {F(s[0])}  p10 {F(s[s.Count / 10])}  median {F(s[s.Count / 2])}  p90 {F(s[s.Count * 9 / 10])}  max {F(s[^1])}  mean {F(s.Average())}";
        }
        Console.WriteLine($"placement dy {pl.Dy:0.###} (DR2 collision min y {ir.CollisionMin[1]:0.#} -> physics 380)");
        Console.WriteLine($"gates with host ground below: {diffs.Count}/{ir.Gates.Count}");
        Console.WriteLine($"DR2 road y   : {S(roads)}");
        Console.WriteLine($"host ground y: {S(hosts)}");
        Console.WriteLine($"road - host  : {S(diffs)}");
        Console.WriteLine($"gates where the DR2 road is below the host ground: {diffs.Count(d => d < 0)}");
        return 0;
    }
}
