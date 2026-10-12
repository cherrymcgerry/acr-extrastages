using System.Numerics;
using System.Text.Json.Nodes;

namespace AcrRef;

/// v1.11 ground: error-bounded simplification of the drawn render terrain and its split into DR2 ground-bake tiles.
internal static class GroundLod
{
    /// Distance (m, plan) from a UE cm point to the nearest centreline point; buckets of 50 m searched ring by ring.
    internal sealed class RouteDistance
    {
        const float Bucket = 5000;
        readonly Dictionary<(int, int), List<Vector2>> _b = new();
        readonly int _maxRing;

        public RouteDistance(IEnumerable<Vector2> centreUe, double capM = 1000)
        {
            _maxRing = (int)Math.Ceiling(capM * 100 / Bucket) + 1;
            foreach (var q in centreUe)
            {
                var k = ((int)MathF.Floor(q.X / Bucket), (int)MathF.Floor(q.Y / Bucket));
                if (!_b.TryGetValue(k, out var l)) _b[k] = l = new();
                l.Add(q);
            }
        }

        /// Beyond the searched rings (capM) the result is capM: callers only need distances up to their last band.
        public double Metres(Vector2 p)
        {
            int bx = (int)MathF.Floor(p.X / Bucket), by = (int)MathF.Floor(p.Y / Bucket);
            double best = double.MaxValue;
            void Cell(int i, int j)
            {
                if (_b.TryGetValue((bx + i, by + j), out var l)) foreach (var q in l) best = Math.Min(best, (p - q).LengthSquared());
            }
            for (int r = 0; r <= _maxRing; r++)
            {
                if (best < double.MaxValue && (r - 1) * Bucket > Math.Sqrt(best)) break;
                if (r == 0) { Cell(0, 0); continue; }
                for (int i = -r; i <= r; i++) { Cell(i, -r); Cell(i, r); }
                for (int j = -r + 1; j <= r - 1; j++) { Cell(-r, j); Cell(r, j); }
            }
            return best == double.MaxValue ? _maxRing * Bucket / 100.0 : Math.Min(Math.Sqrt(best) / 100, _maxRing * Bucket / 100.0);
        }
    }

    /// Tolerance (cm) at a distance (m) from the route: piecewise linear through [ground] simplify_tolerance_cm at
    /// simplify_distance_m (constant beyond the last point).
    internal static Func<double, double> Tolerance(Toml cfg)
    {
        var d = cfg.Arr("ground", "simplify_distance_m").Select(Convert.ToDouble).ToArray();
        var t = cfg.Arr("ground", "simplify_tolerance_cm").Select(Convert.ToDouble).ToArray();
        if (d.Length != t.Length || d.Length == 0) throw new ArgumentException("[ground] simplify_distance_m and simplify_tolerance_cm need the same length");
        return m =>
        {
            if (m <= d[0]) return t[0];
            for (int i = 1; i < d.Length; i++) if (m <= d[i]) return t[i - 1] + (t[i] - t[i - 1]) * (m - d[i - 1]) / (d[i] - d[i - 1]);
            return t[^1];
        };
    }

    /// Quadric edge collapse (Garland-Heckbert, vertices kept at one of the two edge ends) on a section whose triangles
    /// carry their intended winding. A collapse is taken only while the kept vertex's RMS distance to the original
    /// planes merged into it stays under tolerance(distance from the route) (a summed bound tightens with every merge
    /// and stops the simplification early). Border and non-manifold vertices (mesh edge, the cut along the road
    /// section) are locked; collapses that flip a triangle (> 60 deg), break the link condition or give a vertex more
    /// than MaxValence triangles are refused.
    /// Returns (positions, indices) of the result; vertices are welded at 1 mm first.
    const int MaxValence = 16;

    /// long.GetHashCode xors the two halves: edge keys (u << 32 | v) of nearby vertex ids all collide.
    sealed class MixedLong : IEqualityComparer<long>
    {
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long x)
        {
            ulong z = (ulong)x + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (int)(z ^ (z >> 31));
        }
    }

    internal static (List<Vector3> pos, List<uint> idx, JsonObject report) Simplify(ProcMesh.Section s, RouteDistance route, Func<double, double> tolCm)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // weld
        var weld = new Dictionary<(long, long, long), int>();
        var P = new List<Vector3>();
        var remap = new int[s.Pos.Count];
        for (int i = 0; i < s.Pos.Count; i++)
        {
            var p = s.Pos[i];
            var k = ((long)Math.Round(p.X * 10), (long)Math.Round(p.Y * 10), (long)Math.Round(p.Z * 10));
            if (!weld.TryGetValue(k, out var w)) { weld[k] = w = P.Count; P.Add(p); }
            remap[i] = w;
        }
        int nT = s.Idx.Count / 3, nV = P.Count;
        Console.WriteLine($"visuals: ground simplify welded {s.Pos.Count} -> {nV} vertices, {clock.Elapsed.TotalSeconds:0.0} s");
        var T = new int[nT * 3];
        var alive = new bool[nT];
        var vt = new List<int>[nV];
        for (int v = 0; v < nV; v++) vt[v] = new List<int>(6);
        int degenerate = 0;
        for (int t = 0; t < nT; t++)
        {
            int a = remap[s.Idx[t * 3]], b = remap[s.Idx[t * 3 + 1]], c = remap[s.Idx[t * 3 + 2]];
            T[t * 3] = a; T[t * 3 + 1] = b; T[t * 3 + 2] = c;
            if (a == b || b == c || a == c) { degenerate++; continue; }
            alive[t] = true;
            vt[a].Add(t); vt[b].Add(t); vt[c].Add(t);
        }

        // locks: border / non-manifold edges
        var edgeUse = new Dictionary<long, int>(nT * 2, new MixedLong());
        long EK(int u, int v) => u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
        for (int t = 0; t < nT; t++)
            if (alive[t]) for (int k = 0; k < 3; k++) { var e = EK(T[t * 3 + k], T[t * 3 + (k + 1) % 3]); edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1; }
        Console.WriteLine($"visuals: ground simplify {edgeUse.Count} edges, {clock.Elapsed.TotalSeconds:0.0} s");
        var locked = new bool[nV];
        foreach (var (e, n) in edgeUse) if (n != 2) { locked[(int)(e >> 32)] = true; locked[(int)(e & 0xFFFFFFFF)] = true; }

        // quadrics (symmetric 4x4: a2 ab ac ad b2 bc bd c2 cd d2), in metres
        var Q = new double[nV * 10]; var W = new double[nV];
        Vector3 Nrm(int a, int b, int c) => Vector3.Cross(P[b] - P[a], P[c] - P[a]);
        for (int t = 0; t < nT; t++)
        {
            if (!alive[t]) continue;
            var n = Nrm(T[t * 3], T[t * 3 + 1], T[t * 3 + 2]);
            double len = n.Length();
            if (len == 0) continue;
            double a = n.X / len, b = n.Y / len, c = n.Z / len;
            var p0 = P[T[t * 3]];
            double d = -(a * p0.X + b * p0.Y + c * p0.Z) / 100;
            Span<double> q = [a * a, a * b, a * c, a * d, b * b, b * c, b * d, c * c, c * d, d * d];
            for (int k = 0; k < 3; k++) { int v = T[t * 3 + k]; W[v]++; for (int j = 0; j < 10; j++) Q[v * 10 + j] += q[j]; }
        }
        double Cost(int u, int v, Vector3 at)
        {
            double x = at.X / 100.0, y = at.Y / 100.0, z = at.Z / 100.0;
            double C(int j) => Q[u * 10 + j] + Q[v * 10 + j];
            return C(0) * x * x + 2 * C(1) * x * y + 2 * C(2) * x * z + 2 * C(3) * x + C(4) * y * y + 2 * C(5) * y * z + 2 * C(6) * y
                   + C(7) * z * z + 2 * C(8) * z + C(9);
        }
        var tolM = new double[nV];
        for (int v = 0; v < nV; v++) tolM[v] = tolCm(route.Metres(new Vector2(P[v].X, P[v].Y))) / 100;

        Console.WriteLine($"visuals: ground simplify quadrics and route distances, {clock.Elapsed.TotalSeconds:0.0} s");
        // candidate: (remove r, keep k); cost against min tolerance of both ends
        var heap = new PriorityQueue<(int r, int k), double>();
        bool Best(int u, int v, out int r, out int k, out double cost)
        {
            r = k = -1; cost = double.MaxValue;
            double lim = Math.Min(tolM[u], tolM[v]); lim *= lim * (W[u] + W[v]);
            if (!locked[u]) { double c = Cost(u, v, P[v]); if (c < cost) { cost = c; r = u; k = v; } }
            if (!locked[v]) { double c = Cost(u, v, P[u]); if (c < cost) { cost = c; r = v; k = u; } }
            return r >= 0 && cost <= lim;
        }
        void PushAround(int v)
        {
            foreach (var t in vt[v])
                for (int j = 0; j < 3; j++)
                {
                    int o = T[t * 3 + j];
                    if (o == v) continue;
                    if (Best(v, o, out var r, out var k, out var c)) heap.Enqueue((r, k), c);
                }
        }
        for (int v = 0; v < nV; v++) if (!locked[v]) PushAround(v);
        Console.WriteLine($"visuals: ground simplify set up {nT} triangles, {nV} welded vertices, {heap.Count} candidates in {clock.Elapsed.TotalSeconds:0.0} s");

        var gone = new bool[nV];
        int collapses = 0, refused = 0;
        var mark = new int[nV]; int stamp = 0;
        while (heap.TryDequeue(out var cand, out var pc))
        {
            var (r, k) = cand;
            if (gone[r] || gone[k] || locked[r]) continue;
            double now = Cost(r, k, P[k]), lim = Math.Min(tolM[r], tolM[k]);
            if (now > lim * lim * (W[r] + W[k])) continue;
            if (now > pc + 1e-12) { heap.Enqueue((r, k), now); continue; }
            // still an edge?
            bool edge = false;
            foreach (var t in vt[r]) if (T[t * 3] == k || T[t * 3 + 1] == k || T[t * 3 + 2] == k) { edge = true; break; }
            if (!edge) continue;
            // link condition: common neighbours == opposite vertices of the shared triangles
            stamp++;
            foreach (var t in vt[k]) for (int j = 0; j < 3; j++) mark[T[t * 3 + j]] = stamp;
            int common = 0, shared = 0;
            var seen = new HashSet<int>();
            foreach (var t in vt[r])
            {
                bool hasK = T[t * 3] == k || T[t * 3 + 1] == k || T[t * 3 + 2] == k;
                if (hasK) shared++;
                for (int j = 0; j < 3; j++) { int o = T[t * 3 + j]; if (o != r && o != k && mark[o] == stamp && seen.Add(o)) common++; }
            }
            if (common != shared) { refused++; continue; }
            // valence cap: keeping one edge end lets a vertex swallow its neighbours one by one into an ever larger fan,
            // which every later collapse around it rescans (quadratic)
            if (vt[k].Count + vt[r].Count - 2 * shared > MaxValence) { refused++; continue; }
            // flips
            bool ok = true;
            foreach (var t in vt[r])
            {
                int a = T[t * 3], b = T[t * 3 + 1], c = T[t * 3 + 2];
                if (a == k || b == k || c == k) continue;
                var n0 = Nrm(a, b, c);
                int a2 = a == r ? k : a, b2 = b == r ? k : b, c2 = c == r ? k : c;
                var n1 = Nrm(a2, b2, c2);
                float l0 = n0.Length(), l1 = n1.Length();
                if (l1 < 1e-3f * l0 || Vector3.Dot(n0, n1) < 0.5f * l0 * l1) { ok = false; break; }
            }
            if (!ok) { refused++; continue; }
            // collapse r -> k
            foreach (var t in vt[r].ToList())
            {
                bool hasK = T[t * 3] == k || T[t * 3 + 1] == k || T[t * 3 + 2] == k;
                if (hasK)
                {
                    alive[t] = false;
                    for (int j = 0; j < 3; j++) { int o = T[t * 3 + j]; if (o != r) vt[o].Remove(t); }
                }
                else
                {
                    for (int j = 0; j < 3; j++) if (T[t * 3 + j] == r) T[t * 3 + j] = k;
                    vt[k].Add(t);
                }
            }
            vt[r].Clear(); gone[r] = true;
            for (int j = 0; j < 10; j++) Q[k * 10 + j] += Q[r * 10 + j];
            W[k] += W[r];
            tolM[k] = Math.Min(tolM[k], tolM[r]);
            collapses++;
            PushAround(k);
            if (collapses % 100000 == 0)
                Console.WriteLine($"visuals: ground simplify {collapses} collapses, {heap.Count} queued, {clock.Elapsed.TotalSeconds:0.0} s");
        }

        var outPos = new List<Vector3>(); var outIdx = new List<uint>();
        var newId = new int[nV]; Array.Fill(newId, -1);
        int kept = 0;
        for (int t = 0; t < nT; t++)
        {
            if (!alive[t]) continue;
            kept++;
            for (int j = 0; j < 3; j++)
            {
                int v = T[t * 3 + j];
                if (newId[v] < 0) { newId[v] = outPos.Count; outPos.Add(P[v]); }
                outIdx.Add((uint)newId[v]);
            }
        }
        var rep = new JsonObject
        {
            ["triangles"] = $"{nT} -> {kept}",
            ["vertices_welded"] = $"{s.Pos.Count} -> {nV} -> {outPos.Count}",
            ["locked_vertices"] = locked.Count(x => x),
            ["collapses"] = collapses,
            ["refused_link_or_flip"] = refused,
            ["degenerate_dropped"] = degenerate,
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
        };
        Console.WriteLine($"visuals: ground simplified {nT} -> {kept} triangles ({collapses} collapses, {locked.Count(x => x)} locked border vertices, " +
                          $"{refused} refused) in {clock.Elapsed.TotalSeconds:0.0} s");
        return (outPos, outIdx, rep);
    }

    /// DR2 ground-bake tiles (render/ground_bake/manifest.json, schema ground_bake/1): IR plan bounds per tile.
    internal sealed record Tile(string Name, string Tier, string Dds, double MinX, double MinZ, double MaxX, double MaxZ,
                                double AMinX, double AMinZ, double AMaxX, double AMaxZ, double Size);

    internal static List<Tile> LoadTiles(string manifest)
    {
        var j = JsonNode.Parse(File.ReadAllText(manifest))!;
        if (j["schema"]?.GetValue<string>() != "ground_bake/1") throw new InvalidDataException($"{manifest}: schema {j["schema"]} (expected ground_bake/1)");
        double D(JsonNode n, string k) => n[k]!.GetValue<double>();
        var tiles = j["tiles"]!.AsArray().Select(t => new Tile(t!["name"]!.GetValue<string>(), t["tier"]!.GetValue<string>(), t["dds"]!.GetValue<string>(),
            D(t["bounds"]!, "min_x"), D(t["bounds"]!, "min_z"), D(t["bounds"]!, "max_x"), D(t["bounds"]!, "max_z"),
            D(t["assign_bounds"]!, "min_x"), D(t["assign_bounds"]!, "min_z"), D(t["assign_bounds"]!, "max_x"), D(t["assign_bounds"]!, "max_z"),
            D(t, "size_m"))).ToList();
        var dir = Path.GetDirectoryName(manifest)!;
        var missing = tiles.Where(t => !File.Exists(Path.Combine(dir, t.Dds))).Select(t => t.Dds).ToList();
        if (missing.Count > 0) throw new FileNotFoundException($"{manifest}: {missing.Count} tile DDS missing ({string.Join(", ", missing.Take(3))})");
        return tiles;
    }

    /// IR (DR2 world) plan metres of a UE cm point: the inverse of StageBuild.Placement.ToPhysics in x, z.
    internal static (double x, double z) ToIr(StageBuild.Placement pl, Vector3 ue)
    {
        double r = pl.ThetaDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        double xp = ue.X / 100.0 - pl.Tx, zp = ue.Y / 100.0 - pl.Tz;
        return (c * xp - s * zp + pl.Cx, s * xp + c * zp + pl.Cz);
    }

    /// Splits the terrain into one section per tile: a triangle goes to the first high tile whose assign bounds hold its
    /// IR centroid, else the first such low tile; UV0 = (x - min_x, z - min_z) / size_m (v = 0 is the DDS top row).
    /// A triangle reaching past its tile's bounds (simplified, long) goes to the first tile holding all three vertices;
    /// triangles over no tile, or held by none, stay in `rest` (box UVs on the grass slice, as before).
    internal static (List<(Tile tile, ProcMesh.Section sec)> tiles, ProcMesh.Section rest, JsonObject report) Split(
        ProcMesh.Section terrain, StageBuild.Placement pl, List<Tile> tiles)
    {
        var order = tiles.Where(t => t.Tier == "high").Concat(tiles.Where(t => t.Tier != "high")).ToList();
        var secs = order.Select(_ => new ProcMesh.Section()).ToArray();
        var maps = order.Select(_ => new Dictionary<uint, uint>()).ToArray();
        var rest = new ProcMesh.Section(); var restMap = new Dictionary<uint, uint>();
        var ir = terrain.Pos.Select(p => ToIr(pl, p)).ToArray();
        int outside = 0, reassigned = 0, spill = 0; double uvMin = double.MaxValue, uvMax = double.MinValue;
        for (int t = 0; t + 2 < terrain.Idx.Count; t += 3)
        {
            double cx = 0, cz = 0;
            for (int j = 0; j < 3; j++) { cx += ir[terrain.Idx[t + j]].x / 3; cz += ir[terrain.Idx[t + j]].z / 3; }
            int ti = order.FindIndex(x => cx >= x.AMinX && cx <= x.AMaxX && cz >= x.AMinZ && cz <= x.AMaxZ);
            // simplified triangles can reach past the tile's margin: then the first tile holding all three vertices
            bool Holds(Tile x) => Enumerable.Range(0, 3).All(j => { var q = ir[terrain.Idx[t + j]]; return q.x >= x.MinX && q.x <= x.MaxX && q.z >= x.MinZ && q.z <= x.MaxZ; });
            if (ti >= 0 && !Holds(order[ti]))
            {
                int alt = order.FindIndex(Holds);
                if (alt >= 0) { ti = alt; reassigned++; } else { spill++; ti = -1; }
            }
            var s = ti < 0 ? rest : secs[ti]; var map = ti < 0 ? restMap : maps[ti];
            if (ti < 0) outside++;   // includes the spills: they keep box UVs on the grass slice
            for (int j = 0; j < 3; j++)
            {
                uint v = terrain.Idx[t + j];
                if (!map.TryGetValue(v, out var nv))
                {
                    map[v] = nv = (uint)s.Pos.Count;
                    s.Pos.Add(terrain.Pos[(int)v]); s.Nrm.Add(terrain.Nrm[(int)v]);
                    if (ti >= 0)
                    {
                        var tl = order[ti];
                        var uv = new Vector2((float)((ir[v].x - tl.MinX) / tl.Size), (float)((ir[v].z - tl.MinZ) / tl.Size));
                        uvMin = Math.Min(uvMin, Math.Min(uv.X, uv.Y)); uvMax = Math.Max(uvMax, Math.Max(uv.X, uv.Y));
                        s.Uv.Add(uv);
                    }
                    else s.Uv.Add(terrain.Uv[(int)v]);
                }
                s.Idx.Add(nv);
            }
        }
        var used = order.Select((t, i) => (t, secs[i])).Where(x => x.Item2.Idx.Count > 0).ToList();
        var rep = new JsonObject
        {
            ["tiles_in_manifest"] = tiles.Count,
            ["tiles_used"] = used.Count,
            ["triangles_outside_tiles"] = outside,
            ["triangles_moved_to_a_tile_holding_all_vertices"] = reassigned,
            ["triangles_reaching_past_every_tile"] = spill,
            ["uv_range"] = $"{uvMin:0.0000}..{uvMax:0.0000}",
            ["per_tile"] = new JsonObject(used.Select(x => KeyValuePair.Create(x.t.Name, (JsonNode?)(x.Item2.Idx.Count / 3)))),
        };
        Console.WriteLine($"visuals: ground bake {used.Count} of {tiles.Count} tiles used, {outside} triangles outside every tile, {reassigned} moved to a tile " +
                          $"holding all their vertices, {spill} reaching past every tile, UV range {rep["uv_range"]}");
        return (used, rest, rep);
    }
}
