using System.Numerics;
using System.Text.Json.Nodes;

namespace AcrRef;

/// Road-following UVs for the visuals road section ([textured] in config/visuals.toml), in the layout of Hafren's own
/// road meshes (SM_WalesS4HafrenSouthTrack*_Road_*, read with `acr-ref mesh-uv`):
///   UV0 tiling: U = across / road_uv0_across_m, V = along / road_uv0_along_m
///   UV1: U 0..1 from one road edge to the other (DR2 gate width), V = along / road_uv1_along_m
///   UV2 / UV3: the trajectory coordinate, U = 0.5 -/+ across / road_traj_across_m, V = along / road_traj_along_m
///     (Hafren carries the mirrored one in UV4; a ProceduralMeshComponent has UV0..UV3 only)
/// "along" is metres along the DR2 centreline, "across" the signed plan offset from it. Each vertex is projected onto
/// the centreline segments near its first triangle, so the two legs of a hairpin do not mix.
internal static class RoadUv
{
    public static JsonObject Apply(StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, ProcMesh.Section road)
    {
        double a0 = cfg.Num("textured", "road_uv0_across_m") * 100, l0 = cfg.Num("textured", "road_uv0_along_m") * 100;
        double l1 = cfg.Num("textured", "road_uv1_along_m") * 100;
        double ta = cfg.Num("textured", "road_traj_across_m") * 100, tl = cfg.Num("textured", "road_traj_along_m") * 100;

        var cl = ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)).ToArray();
        var s = new double[cl.Length];
        for (int k = 1; k < cl.Length; k++) s[k] = s[k - 1] + (cl[k] - cl[k - 1]).Length();
        var gates = ir.Gates.Select(g => { var l = pl.ToUe(g.left); var r = pl.ToUe(g.right);
            return (mid: new Vector2((float)(l.X + r.X) / 2, (float)(l.Y + r.Y) / 2), w: Math.Sqrt(Math.Pow(l.X - r.X, 2) + Math.Pow(l.Y - r.Y, 2))); }).ToArray();
        var width = cl.Select(q => gates.MinBy(g => (g.mid - q).LengthSquared()).w).ToArray();

        (double along, double across, double w, double dist) Project(Vector2 v, int lo, int hi)
        {
            var best = (along: 0.0, across: 0.0, w: 0.0, dist: double.MaxValue);
            for (int k = Math.Max(0, lo); k < Math.Min(cl.Length - 1, hi); k++)
            {
                var seg = cl[k + 1] - cl[k]; float len = seg.Length();
                if (len < 1e-3f) continue;
                var t = seg / len; var rel = v - cl[k];
                double f = Math.Clamp(Vector2.Dot(rel, t), 0, len);
                var foot = cl[k] + t * (float)f;
                double dist = (v - foot).Length();
                if (dist < best.dist)
                    best = (s[k] + f, t.X * rel.Y - t.Y * rel.X, width[k] + (width[k + 1] - width[k]) * f / len, dist);
            }
            return best;
        }

        int nv = road.Pos.Count;
        var done = new bool[nv];
        var off = new double[nv];
        road.Uv1.Clear(); road.Uv2.Clear(); road.Uv3.Clear();
        for (int i = 0; i < nv; i++) { road.Uv1.Add(Vector2.Zero); road.Uv2.Add(Vector2.Zero); road.Uv3.Add(Vector2.Zero); }
        double far = 0;
        for (int t = 0; t + 2 < road.Idx.Count; t += 3)
        {
            var g = Enumerable.Range(0, 3).Select(k => road.Pos[(int)road.Idx[t + k]]).Aggregate(Vector3.Zero, (a, b) => a + b) / 3;
            // nearest segment of the centroid (global), then each vertex against its neighbourhood
            int j = 0; double bestD = double.MaxValue;
            for (int k = 0; k + 1 < cl.Length; k++)
            {
                double d = (Project(new Vector2(g.X, g.Y), k, k + 1)).dist;
                if (d < bestD) { bestD = d; j = k; }
            }
            for (int k = 0; k < 3; k++)
            {
                int v = (int)road.Idx[t + k];
                if (done[v]) continue;
                done[v] = true;
                var (along, across, w, dist) = Project(new Vector2(road.Pos[v].X, road.Pos[v].Y), j - 6, j + 7);
                far = Math.Max(far, dist);
                off[v] = dist;
                road.Uv[v] = new Vector2((float)(across / a0), (float)(along / l0));
                road.Uv1[v] = new Vector2((float)Math.Clamp(0.5 + across / w, 0, 1), (float)(along / l1));
                road.Uv2[v] = new Vector2((float)(0.5 - across / ta), (float)(along / tl));
                road.Uv3[v] = new Vector2((float)(0.5 + across / ta), (float)(along / tl));
            }
        }
        // other DR2 roads (junctions, driveways) carry the same surface codes; a triangle with a vertex further than
        // road_follow_max_m from the centreline gets its own vertices with world-planar UVs at the same scale
        double maxOff = cfg.Num("textured", "road_follow_max_m") * 100;
        int planar = 0;
        bool tan = road.Tan.Count == nv;
        for (int t = 0; t + 2 < road.Idx.Count; t += 3)
        {
            if (!Enumerable.Range(0, 3).Any(k => off[road.Idx[t + k]] > maxOff)) continue;
            planar++;
            for (int k = 0; k < 3; k++)
            {
                int v = (int)road.Idx[t + k];
                var q = road.Pos[v];
                road.Idx[t + k] = (uint)road.Pos.Count;
                road.Pos.Add(q); road.Nrm.Add(road.Nrm[v]); if (tan) road.Tan.Add(road.Tan[v]);
                road.Uv.Add(new Vector2((float)(q.X / a0), (float)(q.Y / l0)));
                road.Uv1.Add(new Vector2(0.5f, (float)(q.Y / l1)));
                road.Uv2.Add(new Vector2(0.5f, (float)(q.Y / tl)));
                road.Uv3.Add(new Vector2(0.5f, (float)(q.Y / tl)));
            }
        }
        road.Color = Bgra(cfg, "road_color");
        var (inside, mu, mv) = Stretch(road, a0 / 100, l0 / 100);
        var r = new JsonObject
        {
            ["road_vertices"] = nv,
            ["planar_triangles"] = planar,
            ["max_offset_from_centreline_m"] = Math.Round(far / 100, 1),
            ["uv0_median_m_per_unit"] = $"{mu:0.###} across, {mv:0.###} along",
            ["uv0_within_tolerance"] = Math.Round(inside, 4),
            ["road_color_bgra"] = $"{road.Color:X8}",
        };
        Console.WriteLine($"visuals: road UVs along the DR2 centreline, {nv} vertices (up to {far / 100:0.0} m off it; {planar} triangles beyond " +
                          $"{maxOff / 100:0} m world-planar); UV0 median {mu:0.##} m across, {mv:0.##} m along per unit; " +
                          $"{inside:P2} of triangles within the stretch tolerance");
        return r;
    }

    public static uint Bgra(Toml cfg, string key)
    {
        var c = cfg.Arr("textured", key).Select(x => Convert.ToInt32(x)).ToArray();
        return (uint)(c[3] << 24 | c[0] << 16 | c[1] << 8 | c[2]);
    }

    /// UV0 scale per triangle (world metres per UV unit along U and V): fraction of triangles with both within
    /// [1/1.5, 1.5] x nominal, and the medians.
    public static (double inside, double medU, double medV) Stretch(ProcMesh.Section s, double nomU, double nomV)
    {
        var mu = new List<double>(); var mv = new List<double>(); int ok = 0, n = 0;
        for (int t = 0; t + 2 < s.Idx.Count; t += 3)
        {
            int a = (int)s.Idx[t], b = (int)s.Idx[t + 1], c = (int)s.Idx[t + 2];
            double du1 = s.Uv[b].X - s.Uv[a].X, dv1 = s.Uv[b].Y - s.Uv[a].Y, du2 = s.Uv[c].X - s.Uv[a].X, dv2 = s.Uv[c].Y - s.Uv[a].Y;
            double det = du1 * dv2 - du2 * dv1;
            n++;
            if (Math.Abs(det) < 1e-12) continue;
            var e1 = s.Pos[b] - s.Pos[a]; var e2 = s.Pos[c] - s.Pos[a];
            double u = ((e1 * (float)dv2 - e2 * (float)dv1) / (float)det).Length() / 100;
            double v = ((e2 * (float)du1 - e1 * (float)du2) / (float)det).Length() / 100;
            mu.Add(u); mv.Add(v);
            if (u >= nomU / 1.5 && u <= nomU * 1.5 && v >= nomV / 1.5 && v <= nomV * 1.5) ok++;
        }
        mu.Sort(); mv.Sort();
        return (n == 0 ? 0 : (double)ok / n, mu.Count > 0 ? mu[mu.Count / 2] : double.NaN, mv.Count > 0 ? mv[mv.Count / 2] : double.NaN);
    }
}
