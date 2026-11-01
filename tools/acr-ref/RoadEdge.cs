using System.Numerics;
using System.Text.Json.Nodes;

namespace AcrRef;

/// v1.16 organic road edge ([road] smooth_edge, edge_line in config/visuals.toml): the road section is whole DR2 collision triangles
/// with road surface codes, so its outline is a row of triangular teeth against the grass. Each side is cut to a smooth
/// line instead, along the tooth tips (edge_line "tips", default: a running median of the outermost boundary vertex per
/// metre, the notches filled with the collision ground there) or along the notches between the teeth (boundary vertices
/// nearest the centreline; rims of holes inside the road skipped), a running minimum over half of edge_window_m, averaged over edge_window_m, pulled in by
/// edge_margin_m and a slow wander of up to edge_wander_m. Beyond the line the DR2 ground (its verge in the ground bake)
/// shows. Triangles with a vertex further than [textured] road_follow_max_m from the centreline (side roads, junctions)
/// are kept whole. Collision is unaffected (the car drives on BakedData).
internal static class RoadEdge
{
    const float Bin = 100;   // cm per envelope sample along the centreline

    /// Returns the report and the drawn half width (cm) per side (0: across > 0, 1: across < 0) at a distance along the
    /// centreline (cm), for RoadUv's edge-to-edge UV1.
    public static (JsonObject report, Func<int, double, double> halfWidth) Apply(ProcMesh.Section road, IReadOnlyList<Vector2> cl, Toml cfg,
                                                                                ProcMesh.Section? fill = null)
    {
        double window = cfg.Num("road", "edge_window_m") * 100, margin = cfg.Num("road", "edge_margin_m") * 100;
        double wander = cfg.Num("road", "edge_wander_m") * 100, minHalf = cfg.Num("road", "edge_min_half_m") * 100;
        double maxOff = cfg.Num("textured", "road_follow_max_m") * 100;
        // edge_line "tips": the line runs along the tooth tips (the road's full width) and the notches between them are
        // filled with the collision ground triangles there (they share the road's edges, so the surface is seamless);
        // "notches": along the notches (narrower; the ground shows where the teeth were)
        bool atTips = !(cfg["road"].TryGetValue("edge_line", out var el) && el is string els && els == "notches");

        var s = new double[cl.Count];
        for (int k = 1; k < cl.Count; k++) s[k] = s[k - 1] + (cl[k] - cl[k - 1]).Length();
        // segments bucketed on a 20 m grid; a vertex looks in its cell and the 8 around it
        const float Cell = 2000;
        var cells = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k + 1 < cl.Count; k++)
        {
            var (a, b) = (cl[k], cl[k + 1]);
            for (int x = (int)MathF.Floor(MathF.Min(a.X, b.X) / Cell); x <= (int)MathF.Floor(MathF.Max(a.X, b.X) / Cell); x++)
                for (int y = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / Cell); y <= (int)MathF.Floor(MathF.Max(a.Y, b.Y) / Cell); y++)
                {
                    if (!cells.TryGetValue((x, y), out var l)) cells[(x, y)] = l = new();
                    l.Add(k);
                }
        }
        (double along, double across, double dist, Vector2 dir) Project(Vector2 v)
        {
            var best = (along: 0.0, across: 0.0, dist: double.MaxValue, dir: Vector2.Zero);
            int cx = (int)MathF.Floor(v.X / Cell), cy = (int)MathF.Floor(v.Y / Cell);
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int y = cy - 1; y <= cy + 1; y++)
                {
                    if (!cells.TryGetValue((x, y), out var l)) continue;
                    foreach (int k in l)
                    {
                        var seg = cl[k + 1] - cl[k]; float len = seg.Length();
                        if (len < 1e-3f) continue;
                        var t = seg / len; var rel = v - cl[k];
                        double f = Math.Clamp(Vector2.Dot(rel, t), 0, len);
                        var foot = cl[k] + t * (float)f;
                        double dist = (v - foot).Length();
                        if (dist < best.dist) best = (s[k] + f, t.X * rel.Y - t.Y * rel.X, dist, dist > 1 ? (v - foot) / (float)dist : Vector2.Zero);
                    }
                }
            return best;
        }

        int nv = road.Pos.Count;
        var along = new double[nv]; var across = new double[nv]; var far = new bool[nv]; var outward = new Vector2[nv];
        for (int i = 0; i < nv; i++)
        {
            var (al, ac, d, dir) = Project(new Vector2(road.Pos[i].X, road.Pos[i].Y));
            along[i] = al; across[i] = ac; far[i] = d > maxOff; outward[i] = dir;
        }
        // a boundary vertex with road just beyond it (outward from the centreline) is on the rim of a hole or patch inside
        // the road, not on its edge
        var top = new Visuals.TopSurface();
        for (int t = 0; t + 2 < road.Idx.Count; t += 3) top.Add(road.Pos[(int)road.Idx[t]], road.Pos[(int)road.Idx[t + 1]], road.Pos[(int)road.Idx[t + 2]]);
        int inner = 0;
        bool Near(int t) => !far[road.Idx[t]] && !far[road.Idx[t + 1]] && !far[road.Idx[t + 2]];

        // boundary edges of the near triangles; their vertices give the notch depth per side and metre
        var edgeUse = new Dictionary<(uint, uint), int>();
        for (int t = 0; t + 2 < road.Idx.Count; t += 3)
            if (Near(t))
                for (int k = 0; k < 3; k++)
                {
                    uint a = road.Idx[t + k], b = road.Idx[t + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                }
        int bins = (int)Math.Ceiling(s[^1] / Bin) + 1;
        var env = new[] { new double[bins], new double[bins] };   // [0] across > 0 side, [1] across < 0 side
        foreach (var e in env) Array.Fill(e, double.NaN);
        var tips = new[] { new double[bins], new double[bins] };  // the outermost boundary vertex (tooth tips)
        foreach (var e in tips) Array.Fill(e, double.NaN);
        foreach (var ((a, b), n) in edgeUse)
        {
            if (n != 1) continue;
            foreach (var v in new[] { a, b })
            {
                double w = Math.Abs(across[v]);
                if (w < minHalf) continue;                          // the cross cuts at the stage ends
                var p = new Vector2(road.Pos[(int)v].X, road.Pos[(int)v].Y) + outward[v] * 30;
                if (top.At(p) is not null) { inner++; continue; }
                int side = across[v] > 0 ? 0 : 1, bi = (int)Math.Clamp(along[v] / Bin, 0, bins - 1);
                if (double.IsNaN(env[side][bi]) || w < env[side][bi]) env[side][bi] = w;
                if (w <= maxOff && (double.IsNaN(tips[side][bi]) || w > tips[side][bi])) tips[side][bi] = w;
            }
        }
        var rawNotch = env.Select(e => (double[])e.Clone()).ToArray();
        var rawTips = tips.Select(e => (double[])e.Clone()).ToArray();
        if (atTips) env = tips;
        int half = Math.Max(1, (int)Math.Round(window / Bin / 2)), minHalfBins = Math.Max(1, half / 2);
        var edge = new double[2][];
        int filled = 0;
        for (int side = 0; side < 2; side++)
        {
            var raw = env[side];
            // fill bins without a boundary vertex from their neighbours (linear)
            int prev = -1;
            for (int i = 0; i < bins; i++)
            {
                if (double.IsNaN(raw[i])) continue;
                if (prev < 0) for (int j = 0; j < i; j++) raw[j] = raw[i];
                else for (int j = prev + 1; j < i; j++) { raw[j] = raw[prev] + (raw[i] - raw[prev]) * (j - prev) / (i - prev); filled++; }
                prev = i;
            }
            if (prev < 0) { edge[side] = Enumerable.Repeat(double.MaxValue, bins).ToArray(); continue; }
            for (int j = prev + 1; j < bins; j++) raw[j] = raw[prev];
            // notches: running minimum (stay inside every notch); tips: running median (the typical tip, not a junction's)
            var mn = new double[bins];
            for (int i = 0; i < bins; i++)
            {
                var vals = new List<double>();
                for (int j = Math.Max(0, i - (atTips ? half : minHalfBins)); j <= Math.Min(bins - 1, i + (atTips ? half : minHalfBins)); j++) vals.Add(raw[j]);
                vals.Sort();
                mn[i] = atTips ? vals[vals.Count / 2] : vals[0];
            }
            var e = new double[bins];
            double ph1 = side * 2.1, ph2 = side * 4.7 + 1.3;
            for (int i = 0; i < bins; i++)
            {
                double sum = 0; int c = 0;
                for (int j = Math.Max(0, i - half); j <= Math.Min(bins - 1, i + half); j++) { sum += mn[j]; c++; }
                double sm = i * Bin;
                double noise = 0.6 * Math.Sin(2 * Math.PI * sm / 730 + ph1) + 0.4 * Math.Sin(2 * Math.PI * sm / 290 + ph2);   // -1..1
                e[i] = Math.Max(minHalf, sum / c - margin - wander * (0.5 + 0.5 * noise));
            }
            edge[side] = e;
        }
        double EdgeAt(int side, double al)
        {
            double x = Math.Clamp(al / Bin, 0, bins - 1); int i = (int)Math.Floor(x); int j = Math.Min(bins - 1, i + 1);
            return edge[side][i] + (edge[side][j] - edge[side][i]) * (x - i);
        }

        // clip each near triangle by both edge lines (linear in along/across over the triangle), fan-triangulate
        var outS = new ProcMesh.Section { Color = road.Color };
        var dedup = new Dictionary<(long, long, long), uint>();
        bool tan = road.Tan.Count == nv;
        uint Emit(V v)
        {
            var key = ((long)Math.Round(v.P.X * 100), (long)Math.Round(v.P.Y * 100), (long)Math.Round(v.P.Z * 100));
            if (dedup.TryGetValue(key, out var i)) return i;
            dedup[key] = i = (uint)outS.Pos.Count;
            outS.Pos.Add(v.P); outS.Nrm.Add(v.N); outS.Uv.Add(v.Uv); if (tan) outS.Tan.Add(v.T);
            return i;
        }
        V Orig(uint i) => new(road.Pos[(int)i], road.Nrm[(int)i], road.Uv[(int)i], tan ? road.Tan[(int)i] : default, along[i], across[i]);
        int kept = 0, cut = 0, removed = 0;
        double areaBefore = 0, areaAfter = 0;
        for (int t = 0; t + 2 < road.Idx.Count; t += 3)
        {
            var poly = new List<V> { Orig(road.Idx[t]), Orig(road.Idx[t + 1]), Orig(road.Idx[t + 2]) };
            double a0 = Area(poly);
            areaBefore += a0;
            if (Near(t))
            {
                poly = Clip(poly, v => EdgeAt(0, v.Al) - v.Ac);
                poly = Clip(poly, v => EdgeAt(1, v.Al) + v.Ac);
            }
            if (poly.Count < 3) { removed++; continue; }
            double a1 = Area(poly);
            areaAfter += a1;
            if (Math.Abs(a1 - a0) > 1e-3 * Math.Max(1, a0)) cut++; else kept++;
            var ids = poly.Select(Emit).ToList();
            for (int k = 1; k + 1 < ids.Count; k++)
                if (ids[0] != ids[k] && ids[k] != ids[k + 1] && ids[0] != ids[k + 1])
                { outS.Idx.Add(ids[0]); outS.Idx.Add(ids[k]); outS.Idx.Add(ids[k + 1]); }
        }
        // tips: collision ground triangles inside the line (the notches between the teeth, patches inside the road) join
        // the road, clipped to the line; only where the road runs (foot of the perpendicular on the centreline, not
        // beyond its ends)
        int filledTris = 0; double fillArea = 0;
        if (atTips && fill is not null)
        {
            int fn = fill.Pos.Count;
            var fal = new double[fn]; var fac = new double[fn]; var fok = new bool[fn];
            for (int i = 0; i < fn; i++)
            {
                var (al, ac, d, _) = Project(new Vector2(fill.Pos[i].X, fill.Pos[i].Y));
                fal[i] = al; fac[i] = ac;
                fok[i] = d <= maxOff && Math.Abs(d - Math.Abs(ac)) < 10 && al > Bin && al < s[^1] - Bin;
            }
            V FOrig(uint i) => new(fill.Pos[(int)i], fill.Nrm[(int)i], fill.Uv[(int)i], default, fal[i], fac[i]);
            for (int t = 0; t + 2 < fill.Idx.Count; t += 3)
            {
                uint a = fill.Idx[t], b = fill.Idx[t + 1], c = fill.Idx[t + 2];
                if (!fok[a] || !fok[b] || !fok[c]) continue;
                if (new[] { a, b, c }.All(v => Math.Abs(fac[v]) > EdgeAt(fac[v] > 0 ? 0 : 1, fal[v]))) continue;   // wholly outside (quick)
                var poly = Clip(Clip(new List<V> { FOrig(a), FOrig(b), FOrig(c) }, v => EdgeAt(0, v.Al) - v.Ac), v => EdgeAt(1, v.Al) + v.Ac);
                if (poly.Count < 3) continue;
                filledTris++; fillArea += Area(poly);
                var ids = poly.Select(Emit).ToList();
                for (int k = 1; k + 1 < ids.Count; k++)
                    if (ids[0] != ids[k] && ids[k] != ids[k + 1] && ids[0] != ids[k + 1])
                    { outS.Idx.Add(ids[0]); outS.Idx.Add(ids[k]); outS.Idx.Add(ids[k + 1]); }
            }
        }
        road.Pos = outS.Pos; road.Nrm = outS.Nrm; road.Uv = outS.Uv; road.Tan = outS.Tan; road.Idx = outS.Idx;

        string Pct(IEnumerable<double> xs) { var l = xs.Where(x => !double.IsNaN(x) && x > 0 && x < 1e6).Select(x => x / 100).OrderBy(x => x).ToList();
            return l.Count == 0 ? "none" : $"median {l[l.Count / 2]:0.00}, 5% {l[l.Count / 20]:0.00}, 95% {l[l.Count * 19 / 20]:0.00}"; }
        var notchRep = $"notches {Pct(rawNotch[0].Concat(rawNotch[1]))} m, tooth tips {Pct(rawTips[0].Concat(rawTips[1]))} m, cut line ({(atTips ? "tips" : "notches")}) {Pct(edge[0].Concat(edge[1]))} m from the centreline";
        var widths = Enumerable.Range(0, bins).Select(i => (Math.Min(edge[0][i], 1e6) + Math.Min(edge[1][i], 1e6)) / 100).Where(w => w < 1e3).OrderBy(w => w).ToList();
        var r = new JsonObject
        {
            ["triangles_kept_whole"] = kept,
            ["triangles_cut"] = cut,
            ["triangles_removed"] = removed,
            ["area_removed_m2"] = Math.Round((areaBefore - areaAfter) / 1e4, 1),
            ["area_removed_fraction"] = Math.Round(areaBefore > 0 ? 1 - areaAfter / areaBefore : 0, 4),
            ["drawn_width_m"] = widths.Count == 0 ? "none" : $"median {widths[widths.Count / 2]:0.0}, 5% {widths[widths.Count / 20]:0.0}, 95% {widths[widths.Count * 19 / 20]:0.0}",
            ["envelope_bins_interpolated"] = filled,
            ["ground_triangles_filled_in"] = filledTris,
            ["ground_area_filled_in_m2"] = Math.Round(fillArea / 1e4, 1),
            ["inner_boundary_vertices_skipped"] = inner,
            ["half_widths"] = notchRep,
            ["settings"] = $"window {window / 100} m, margin {margin / 100} m, wander {wander / 100} m, min half width {minHalf / 100} m",
        };
        Console.WriteLine($"visuals: organic road edge: {cut} road triangles cut, {removed} removed, {kept} whole; " +
                          $"{r["area_removed_m2"]} m2 ({(double)r["area_removed_fraction"]!:P1}) of road left to the ground, {fillArea / 1e4:0} m2 of notches filled; drawn width {r["drawn_width_m"]}; {notchRep}");
        return (r, EdgeAt);
    }

    readonly record struct V(Vector3 P, Vector3 N, Vector2 Uv, Vector3 T, double Al, double Ac)
    {
        public static V Lerp(V a, V b, float t) => new(Vector3.Lerp(a.P, b.P, t), Vector3.Lerp(a.N, b.N, t), Vector2.Lerp(a.Uv, b.Uv, t),
                                                       Vector3.Lerp(a.T, b.T, t), a.Al + (b.Al - a.Al) * t, a.Ac + (b.Ac - a.Ac) * t);
    }

    /// Sutherland-Hodgman against f(v) >= 0, f taken per vertex and linear along each edge.
    static List<V> Clip(List<V> poly, Func<V, double> f)
    {
        if (poly.Count < 3) return poly;
        var d = poly.Select(f).ToArray();
        if (d.All(x => x >= 0)) return poly;
        var res = new List<V>();
        for (int i = 0; i < poly.Count; i++)
        {
            int j = (i + 1) % poly.Count;
            if (d[i] >= 0) res.Add(poly[i]);
            if (d[i] >= 0 != d[j] >= 0) res.Add(V.Lerp(poly[i], poly[j], (float)(d[i] / (d[i] - d[j]))));
        }
        return res;
    }

    static double Area(List<V> poly)
    {
        double a = 0;
        for (int k = 1; k + 1 < poly.Count; k++) a += Vector3.Cross(poly[k].P - poly[0].P, poly[k + 1].P - poly[0].P).Length() / 2;
        return a;
    }
}
