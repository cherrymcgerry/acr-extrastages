using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Dr2Extract;

internal sealed record RouteMapFit(string Source, int Width, int Height, int RoutePixels, float ProperRotationDeg,
    float ProperCostPx, float ReflectedRotationDeg, float ReflectedCostPx)
{
    /// <summary>World (X, Z) matches the map image (x right, y down) by a proper rotation: right-handed, Y up.</summary>
    public bool RightHanded => ProperCostPx < ReflectedCostPx;

    public float Ratio => ReflectedCostPx / Math.Max(ProperCostPx, 1e-3f);
}

/// <summary>
/// Handedness check against the stage-select route map (<c>frontend/streamed_textures/splines/&lt;stage&gt;_sa.tpk</c>,
/// tpk v0x66 format 38 = BC7, route drawn in alpha). The gate polyline seen from above (+Y) is fitted to the route
/// pixels over all rotations, with and without a mirror; the frame is right-handed if the unmirrored fit is better.
/// </summary>
internal static class RouteMapCheck
{
    public static string MapPath(string stageId) => $"frontend/streamed_textures/splines/{stageId}_sa.tpk";

    /// <summary>Start-to-finish gate crossings (what the route map draws).</summary>
    public static List<Vector3> StagePolyline(ProgressTrack progress)
    {
        var (a, b) = (progress.Splits.First(s => s.Type == "start").Gate, progress.Splits.First(s => s.Type == "finish").Gate);
        return progress.Gates.Skip(Math.Min(a, b)).Take(Math.Abs(b - a) + 1).Select(g => g.Crossing).ToList();
    }

    /// <summary><c>route-map-check [--filter _rally_]</c>: handedness fit for every stage.</summary>
    public static async Task<int> Run(string[] args)
    {
        var install = await Dr2Install.OpenAsync(Program.Option(args, "--dr2") ?? Dr2Install.DefaultRoot,
            Program.Option(args, "--cache") ?? Path.Combine("work", "dr2", "cache"));
        var filter = new System.Text.RegularExpressions.Regex(Program.Option(args, "--filter") ?? "_rally_");
        var (right, left, missing) = (0, 0, 0);
        await foreach (var st in StageRef.Enumerate(install, filter))
        {
            if (!install.Exists(MapPath(st.Id)))
            {
                missing++;
                Console.WriteLine($"{st.Id,-36} no route map");
                continue;
            }

            var progress = ProgressTrack.Load(await install.FetchAsync($"{st.RouteDir}/progress_track.xml"));
            var fit = Fit(await install.FetchAsync(MapPath(st.Id)), StagePolyline(progress), MapPath(st.Id));
            _ = fit.RightHanded ? right++ : left++;
            Console.WriteLine($"{st.Id,-36} proper {fit.ProperCostPx,6:F2} px @ {fit.ProperRotationDeg,3}deg   " +
                              $"mirrored {fit.ReflectedCostPx,6:F2} px @ {fit.ReflectedRotationDeg,3}deg   ratio {fit.Ratio:F1}");
        }

        Console.WriteLine($"right-handed fits: {right}, mirrored fits: {left}, no map: {missing}");
        return 0;
    }

    public static byte[,] DecodeAlpha(string tpkPath, out int width, out int height)
    {
        var bytes = File.ReadAllBytes(tpkPath);
        if (BitConverter.ToInt32(bytes, 0) != 0x66 || BitConverter.ToInt32(bytes, 0x28) != 38)
        {
            throw new NotSupportedException($"{tpkPath}: expected tpk v0x66 format 38");
        }

        (width, height) = (BitConverter.ToInt32(bytes, 0x2C), BitConverter.ToInt32(bytes, 0x30));
        var length = BitConverter.ToInt32(bytes, 0x38);
        var pixels = new BcDecoder().DecodeRaw(bytes.AsSpan(bytes.Length - length, width * height).ToArray(), width, height,
            CompressionFormat.Bc7);
        var alpha = new byte[width, height];
        for (var i = 0; i < pixels.Length; i++)
        {
            alpha[i % width, i / width] = pixels[i].a;
        }

        return alpha;
    }

    public static RouteMapFit Fit(string tpkPath, IReadOnlyList<Vector3> polyline, string source)
    {
        var alpha = DecodeAlpha(tpkPath, out var w, out var h);
        var route = new List<Vector2>();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (alpha[x, y] >= 128)
                {
                    route.Add(new Vector2(x, y));
                }
            }
        }

        if (route.Count < 20)
        {
            throw new InvalidDataException($"{tpkPath}: only {route.Count} route pixels");
        }

        var dist = DistanceMap(alpha, w, h);
        var step = Math.Max(1, polyline.Count / 400);
        var pts = polyline.Where((_, i) => i % step == 0).Select(p => new Vector2(p.X, p.Z)).ToList();
        var rMin = route.Aggregate(Vector2.Min);
        var rMax = route.Aggregate(Vector2.Max);

        (float Deg, float Cost) Best(bool mirror)
        {
            var best = (Deg: 0f, Cost: float.MaxValue);
            for (var deg = 0; deg < 360; deg++)
            {
                var a = deg * MathF.PI / 180;
                var (c, s) = (MathF.Cos(a), MathF.Sin(a));
                var q = pts.Select(p =>
                {
                    var v = mirror ? new Vector2(p.X, -p.Y) : p;
                    return new Vector2(c * v.X - s * v.Y, s * v.X + c * v.Y);
                }).ToList();
                var qMin = q.Aggregate(Vector2.Min);
                var qMax = q.Aggregate(Vector2.Max);
                // Uniform scale from the bounding boxes (the map may pad one axis), centred.
                var scale = MathF.Min((rMax.X - rMin.X) / MathF.Max(qMax.X - qMin.X, 1e-3f),
                    (rMax.Y - rMin.Y) / MathF.Max(qMax.Y - qMin.Y, 1e-3f));
                var offset = (rMin + rMax) / 2 - scale * (qMin + qMax) / 2;
                var cost = 0f;
                foreach (var p in q)
                {
                    var m = scale * p + offset;
                    var (x, y) = ((int)MathF.Round(m.X), (int)MathF.Round(m.Y));
                    cost += x < 0 || y < 0 || x >= w || y >= h ? w : dist[x, y];
                }

                cost /= q.Count;
                if (cost < best.Cost)
                {
                    best = (deg, cost);
                }
            }

            return best;
        }

        var proper = Best(false);
        var mirrored = Best(true);
        return new RouteMapFit(source, w, h, route.Count, proper.Deg, MathF.Round(proper.Cost, 2), mirrored.Deg,
            MathF.Round(mirrored.Cost, 2));
    }

    /// <summary>Two-pass chamfer distance (pixels) to the nearest route pixel (alpha >= 128).</summary>
    private static float[,] DistanceMap(byte[,] alpha, int w, int h)
    {
        var d = new float[w, h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                d[x, y] = alpha[x, y] >= 128 ? 0 : float.MaxValue / 4;
            }
        }

        const float o = 1, dg = 1.4142f;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x > 0) d[x, y] = MathF.Min(d[x, y], d[x - 1, y] + o);
                if (y > 0) d[x, y] = MathF.Min(d[x, y], d[x, y - 1] + o);
                if (x > 0 && y > 0) d[x, y] = MathF.Min(d[x, y], d[x - 1, y - 1] + dg);
                if (x + 1 < w && y > 0) d[x, y] = MathF.Min(d[x, y], d[x + 1, y - 1] + dg);
            }
        }

        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = w - 1; x >= 0; x--)
            {
                if (x + 1 < w) d[x, y] = MathF.Min(d[x, y], d[x + 1, y] + o);
                if (y + 1 < h) d[x, y] = MathF.Min(d[x, y], d[x, y + 1] + o);
                if (x + 1 < w && y + 1 < h) d[x, y] = MathF.Min(d[x, y], d[x + 1, y + 1] + dg);
                if (x > 0 && y + 1 < h) d[x, y] = MathF.Min(d[x, y], d[x - 1, y + 1] + dg);
            }
        }

        return d;
    }
}
