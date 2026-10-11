using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using static AcrRef.Splines;

namespace AcrRef;

/// Road corridor of the DR2 route in UE cm (XY), from the gates: centreline segments with the half widths of the
/// authored road edges. Clearance(q) is the horizontal distance from q to the nearest road edge, negative on the road.
internal sealed class RoadCorridor
{
    readonly V3[] _c;
    readonly double[] _lw, _rw;
    readonly int[] _leftSign;   // per segment: sign of cross(segment, offset) on the left edge's side
    public RoadCorridor(IReadOnlyList<V3> centre, IReadOnlyList<V3> left, IReadOnlyList<V3> right)
    {
        _c = centre.ToArray();
        _lw = Enumerable.Range(0, centre.Count).Select(i => Dist2(left[i], centre[i])).ToArray();
        _rw = Enumerable.Range(0, centre.Count).Select(i => Dist2(right[i], centre[i])).ToArray();
        _leftSign = new int[Math.Max(0, centre.Count - 1)];
        for (int i = 0; i + 1 < centre.Count; i++)
        {
            var a = _c[i]; var b = _c[i + 1];
            double cl = Cross2(b.X - a.X, b.Y - a.Y, left[i].X - a.X, left[i].Y - a.Y);
            double cr = Cross2(b.X - a.X, b.Y - a.Y, right[i].X - a.X, right[i].Y - a.Y);
            _leftSign[i] = cl != 0 ? Math.Sign(cl) : -Math.Sign(cr);
        }
    }
    static double Dist2(V3 a, V3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double Cross2(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    /// (clearance cm, centreline Z at the nearest point, nearest segment index); the road extends from the centre
    /// to the left edge on one side and to the right edge on the other.
    public (double clearance, double z, int seg) Query(double x, double y)
    {
        (double c, double z, int seg) best = (double.MaxValue, 0, -1);
        for (int i = 0; i + 1 < _c.Length; i++)
        {
            var a = _c[i]; var b = _c[i + 1];
            double ex = b.X - a.X, ey = b.Y - a.Y, l2 = ex * ex + ey * ey;
            double u = l2 < 1e-9 ? 0 : Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / l2, 0, 1);
            double px = a.X + u * ex, py = a.Y + u * ey;
            bool leftSide = Math.Sign(Cross2(ex, ey, x - px, y - py)) == _leftSign[i];
            double hw = leftSide ? _lw[i] + u * (_lw[i + 1] - _lw[i]) : _rw[i] + u * (_rw[i + 1] - _rw[i]);
            double d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)) - hw;
            if (d < best.c) best = (d, a.Z + u * (b.Z - a.Z), i);
        }
        return best;
    }
}

/// RespawnPlanes: 837 SplineMeshComponents (SM_RespawnWallCube, 20 x 0.2 x 10 m, query-only "Trigger" profile) whose
/// spline params are world positions (the components are not attached, so their transform is identity) and whose
/// collision is cooked per component (BodySetup CookedFormatData). Editing the spline params would not move that
/// collision, so walls are moved rigidly with RelativeLocation / RelativeRotation / RelativeScale3D, which are
/// inserted before the trailing bHiddenInGame value.
internal static class Respawn
{
    // slots present on every host wall: SplineParams, CachedMeshBodySetupGuid, BodySetup, StaticMesh,
    // bHasNoStreamableTextures, BodyInstance, bHiddenInGame
    static readonly int[] WallSlots = [0, 3, 5, 16, 130, 154, 189];
    const int SlotLocation = 175, SlotRotation = 176, SlotScale = 177;
    const int TailAfterHidden = 20;            // bytes after the bHiddenInGame value (identical on every wall)
    const double WallBottom = -100, WallTop = 900;   // cm, SM_RespawnWallCube Z range relative to the spline
    const double Below = 3000, Above = 1500;   // cm, wall span around the contour height after scaling
    const double MinClearance = 300;           // cm from any road edge; closer pieces are sunk
    const double Sink = -100000;

    sealed record Wall(int Export, V3 P0, V3 T0, V3 P1, V3 T1);

    static V3 Hermite(Wall w, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        return w.P0 * (2 * t3 - 3 * t2 + 1) + w.T0 * (t3 - 2 * t2 + t) + w.P1 * (-2 * t3 + 3 * t2) + w.T1 * (t3 - t2);
    }

    static V3 Vec(FStructFallback s, string name) => s.TryGetValue(out FVector v, name) ? new V3(v.X, v.Y, v.Z) : new V3(0, 0, 0);

    static List<Wall> Walls(LegacyPackage cell, List<UObject> objs)
    {
        var list = new List<Wall>();
        for (int e = 0; e < cell.ExportCount; e++)
        {
            if (cell.ExportClass(e) != "SplineMeshComponent") continue;
            if (!objs[e].TryGetValue(out FStructFallback sp, "SplineParams")) throw new InvalidDataException($"{cell.ExportName(e)} has no SplineParams");
            list.Add(new Wall(e, Vec(sp, "StartPos"), Vec(sp, "StartTangent"), Vec(sp, "EndPos"), Vec(sp, "EndTangent")));
        }
        return list;
    }

    /// Footprint samples of a wall after the rigid transform (yaw about Z, Z scale k, translation t).
    static IEnumerable<V3> Samples(Wall w, double yaw, double k, V3 t)
    {
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        for (int i = 0; i <= 8; i++)
        {
            var p = Hermite(w, i / 8.0);
            yield return new V3(c * p.X - s * p.Y + t.X, s * p.X + c * p.Y + t.Y, k * p.Z + t.Z);
        }
    }

    /// v1 audit: host walls left in place that the DR2 road passes through (footprint on the road, height overlap).
    public static JsonObject AuditInPlace(LegacyPackage cell, List<UObject> objs, RoadCorridor road)
    {
        int onRoad = 0, nearRoad = 0, heightOverlap = 0;
        foreach (var w in Walls(cell, objs))
        {
            double minC = double.MaxValue; bool overlap = false;
            foreach (var q in Samples(w, 0, 1, new V3(0, 0, 0)))
            {
                var (c, z, _) = road.Query(q.X, q.Y);
                minC = Math.Min(minC, c);
                if (c < 0 && z > q.Z + WallBottom - 200 && z < q.Z + WallTop) overlap = true;
            }
            if (minC < 0) onRoad++;
            if (minC < MinClearance) nearRoad++;
            if (overlap) heightOverlap++;
        }
        return new JsonObject
        {
            ["walls_footprint_on_dr2_road"] = onRoad, ["walls_within_3m_of_dr2_road"] = nearRoad,
            ["walls_on_dr2_road_at_road_height"] = heightOverlap,
        };
    }

    /// Re-lays the host walls end to end along the closed contour (UE cm); returns a report.
    public static JsonObject Relay(LegacyPackage cell, List<UObject> objs, IReadOnlyList<V3> contour, RoadCorridor road, bool sinkAll)
    {
        var walls = Walls(cell, objs);
        var loop = contour.Append(contour[0]).ToList();
        var s = new double[loop.Count];
        for (int i = 1; i < loop.Count; i++) s[i] = s[i - 1] + Dist(loop[i], loop[i - 1]);
        int seg = 0; double u = 0;          // current point = loop[seg] + u * (loop[seg+1] - loop[seg])
        V3 Cur() => loop[seg] + (loop[seg + 1] - loop[seg]) * u;

        int placed = 0, sunkNear = 0, sunkSpare = 0;
        double covered = 0, minClear = double.MaxValue;
        byte[]? tail = null;
        foreach (var w in walls)
        {
            double yaw = 0, k = 1; V3 t = new(0, 0, Sink);
            bool place = !sinkAll && seg < loop.Count - 1;
            if (place)
            {
                var p = Cur();
                double chord = Dist(w.P1, w.P0);
                if (!Advance(loop, ref seg, ref u, p, chord)) { place = false; sunkSpare++; }
                else
                {
                    var q = Cur();
                    yaw = Math.Atan2(q.Y - p.Y, q.X - p.X) - Math.Atan2(w.P1.Y - w.P0.Y, w.P1.X - w.P0.X);
                    k = (Below + Above) / (WallTop - WallBottom);
                    double zEdge = (p.Z + q.Z) / 2;
                    double c = Math.Cos(yaw), sn = Math.Sin(yaw);
                    t = new V3(p.X - (c * w.P0.X - sn * w.P0.Y), p.Y - (sn * w.P0.X + c * w.P0.Y), zEdge - Below - k * (w.P0.Z + WallBottom));
                    double clear = Samples(w, yaw, k, t).Min(x => road.Query(x.X, x.Y).clearance);
                    if (clear < MinClearance) { place = false; sunkNear++; covered += chord; }
                    else { placed++; covered += chord; minClear = Math.Min(minClear, clear); }
                }
            }
            else if (!sinkAll) sunkSpare++;
            if (!place) { yaw = 0; k = 1; t = new V3(0, 0, Sink); }

            var d = cell.GetExportData(w.Export);
            int p0 = 0;
            var slots = Unv.ReadHeader(d, ref p0);
            if (!slots.Select(x => x.Index).SequenceEqual(WallSlots) || slots.Any(x => x.Zero))
                throw new InvalidDataException($"{cell.ExportName(w.Export)}: unexpected property slots");
            int hiddenAt = d.Length - TailAfterHidden - 1;
            if (d[hiddenAt] != 1) throw new InvalidDataException($"{cell.ExportName(w.Export)}: bHiddenInGame not where expected");
            var thisTail = d[(hiddenAt + 1)..];
            if (tail is null) tail = thisTail;
            else if (!thisTail.AsSpan().SequenceEqual(tail)) throw new InvalidDataException($"{cell.ExportName(w.Export)}: wall tail differs");

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            var newSlots = WallSlots.Concat([SlotLocation, SlotRotation, SlotScale]).OrderBy(x => x).Select(x => new Unv.Slot(x, false)).ToList();
            Unv.WriteHeader(bw, newSlots);
            bw.Write(d, p0, hiddenAt - p0);
            foreach (var v in new[] { t.X, t.Y, t.Z, 0, yaw * 180 / Math.PI, 0, 1, 1, k }) bw.Write(v);
            bw.Write(d, hiddenAt, d.Length - hiddenAt);
            cell.SetExportData(w.Export, ms.ToArray());
        }
        return new JsonObject
        {
            ["walls"] = walls.Count, ["placed_along_dr2_contour"] = placed,
            ["sunk_too_close_to_road"] = sunkNear, ["sunk_spare"] = sunkSpare,
            ["contour_length_m"] = Math.Round(s[^1] / 100, 1),
            ["contour_covered_m"] = Math.Round(covered / 100, 1),
            ["min_clearance_to_road_m"] = placed > 0 ? Math.Round(minClear / 100, 2) : null,
            ["wall_span"] = $"contour height -{Below / 100} m .. +{Above / 100} m",
        };
    }

    static double Dist(V3 a, V3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// Moves (seg, u) forward along the polyline to the first point at horizontal distance `chord` from p.
    static bool Advance(List<V3> loop, ref int seg, ref double u, V3 p, double chord)
    {
        if (chord < 1) return true;
        for (int i = seg; i + 1 < loop.Count; i++)
        {
            var a = loop[i]; var b = loop[i + 1];
            double ex = b.X - a.X, ey = b.Y - a.Y, fx = a.X - p.X, fy = a.Y - p.Y;
            double A = ex * ex + ey * ey, B = 2 * (fx * ex + fy * ey), C = fx * fx + fy * fy - chord * chord;
            if (A < 1e-9) continue;
            double disc = B * B - 4 * A * C;
            if (disc < 0) continue;
            double r = (-B + Math.Sqrt(disc)) / (2 * A);
            double lo = i == seg ? u : 0;
            if (r >= lo - 1e-9 && r <= 1)
            {
                seg = i; u = r;
                if (u >= 1 - 1e-12 && seg + 2 < loop.Count) { seg++; u = 0; }
                return true;
            }
        }
        seg = loop.Count - 1; u = 0;
        return false;
    }

    /// Offline check of written walls: re-parses the inserted transforms and measures clearance to the road.
    public static (int placed, int sunk, double minClearM, int badLayout) Check(LegacyPackage cell, RoadCorridor road)
    {
        int placed = 0, sunk = 0, bad = 0; double minC = double.MaxValue;
        for (int e = 0; e < cell.ExportCount; e++)
        {
            if (cell.ExportClass(e) != "SplineMeshComponent") continue;
            var d = cell.GetExportData(e);
            int p = 0;
            var slots = Unv.ReadHeader(d, ref p);
            if (!slots.Select(x => x.Index).SequenceEqual(WallSlots.Concat([SlotLocation, SlotRotation, SlotScale]).OrderBy(x => x))) { bad++; continue; }
            int at = d.Length - TailAfterHidden - 1 - 72;
            double[] v = Enumerable.Range(0, 9).Select(i => BitConverter.ToDouble(d, at + i * 8)).ToArray();
            if (d[d.Length - TailAfterHidden - 1] != 1) { bad++; continue; }
            if (v[8] == 1 && v[2] <= Sink + 1) { sunk++; continue; }   // placed walls carry the Z scale
            // SplineParams StartPos/EndPos are the first two... read via the struct header
            int q = p;
            var sp = Unv.ReadHeader(d, ref q);
            var vals = new Dictionary<int, V3>();
            foreach (var sl in sp)
            {
                if (sl.Zero) continue;
                int size = sl.Index switch { 0 or 1 or 6 or 8 => 24, 2 or 5 or 7 or 9 => 16, _ => 4 };
                if (size == 24) vals[sl.Index] = new V3(BitConverter.ToDouble(d, q), BitConverter.ToDouble(d, q + 8), BitConverter.ToDouble(d, q + 16));
                q += size;
            }
            var w = new Wall(e, vals.GetValueOrDefault(0), vals.GetValueOrDefault(1), vals.GetValueOrDefault(6), vals.GetValueOrDefault(8));
            double c = Samples(w, v[4] * Math.PI / 180, v[8], new V3(v[0], v[1], v[2])).Min(x => road.Query(x.X, x.Y).clearance);
            minC = Math.Min(minC, c);
            placed++;
        }
        return (placed, sunk, minC / 100, bad);
    }
}
