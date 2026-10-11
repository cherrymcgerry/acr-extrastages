namespace AcrRef;

/// uv-fit <file.glb>: per material, least-squares fit of each TEXCOORD set against the vertex plan position
/// (u = a x + b z + c, glb space, y up) with the RMS residual and UV range: shows which UV set is a planar
/// colour-map projection and its metres per UV unit.
internal static class UvFit
{
    public static int Run(Options o)
    {
        var glb = new Glb(o.Positional[0]);
        var all = glb.Primitives().SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(v => (x: p.Positions[v * 3], z: p.Positions[v * 3 + 2]))).ToList();
        Console.WriteLine($"plan bounds x {all.Min(q => q.x):0}..{all.Max(q => q.x):0} z {all.Min(q => q.z):0}..{all.Max(q => q.z):0}");
        foreach (var g in glb.Primitives(withUv: true).GroupBy(p => p.Material))
        {
            if (g.All(p => p.Color is not null))
                for (int c = 0; c < 4; c++)
                {
                    var hist = g.SelectMany(p => Enumerable.Range(0, p.Color!.Length / 4).Select(v => (int)Math.Round(p.Color![v * 4 + c] * 255)))
                        .GroupBy(x => x).OrderByDescending(x => x.Count()).Take(10);
                    Console.WriteLine($"{g.Key} COLOR_0.{"RGBA"[c]}: {string.Join(" ", hist.Select(x => $"{x.Key}x{x.Count()}"))}");
                }
            for (int k = 0; k < 2; k++)
            {
                if (g.Any(p => p.Uv[k] is null)) { Console.WriteLine($"{g.Key}: no TEXCOORD_{k}"); continue; }
                var pts = g.SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3)
                    .Select(v => (x: (double)p.Positions[v * 3], z: (double)p.Positions[v * 3 + 2], u: (double)p.Uv[k]![v * 2], w: (double)p.Uv[k]![v * 2 + 1]))).ToList();
                var fu = Fit(pts.Select(q => (q.x, q.z, q.u)).ToList());
                var fv = Fit(pts.Select(q => (q.x, q.z, q.w)).ToList());
                Console.WriteLine($"{g.Key} uv{k}: {pts.Count} v, u {pts.Min(q => q.u):0.###}..{pts.Max(q => q.u):0.###} v {pts.Min(q => q.w):0.###}..{pts.Max(q => q.w):0.###}; " +
                                  $"u = {fu.a:E3} x + {fu.b:E3} z + {fu.c:0.####} (rms {fu.rms:0.#####}); v = {fv.a:E3} x + {fv.b:E3} z + {fv.c:0.####} (rms {fv.rms:0.#####}); " +
                                  $"m/unit {1 / Math.Sqrt(fu.a * fu.a + fu.b * fu.b):0.#} x {1 / Math.Sqrt(fv.a * fv.a + fv.b * fv.b):0.#}");
            }
        }
        return 0;
    }

    public static (double a, double b, double c, double rms) Fit(List<(double x, double z, double t)> p)
    {
        double mx = p.Average(q => q.x), mz = p.Average(q => q.z), mt = p.Average(q => q.t);
        double sxx = 0, sxz = 0, szz = 0, sxt = 0, szt = 0;
        foreach (var q in p)
        {
            double x = q.x - mx, z = q.z - mz, t = q.t - mt;
            sxx += x * x; sxz += x * z; szz += z * z; sxt += x * t; szt += z * t;
        }
        double det = sxx * szz - sxz * sxz;
        double a = (sxt * szz - szt * sxz) / det, b = (szt * sxx - sxt * sxz) / det, c = mt - a * mx - b * mz;
        double rms = Math.Sqrt(p.Average(q => Math.Pow(a * q.x + b * q.z + c - q.t, 2)));
        return (a, b, c, rms);
    }
}
