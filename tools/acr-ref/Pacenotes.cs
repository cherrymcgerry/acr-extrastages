using System.Globalization;
using System.Text;

namespace AcrRef;

/// ACR pacenote tables: UDataTable with RowStruct PacenoteSplineEntry
///   0 SplineDistanceM f32, 1 LengthM f32, 2 TokenList {0 Tokens: TArray&lt;FName&gt;}, 3 LinkToNext bool,
///   4 Description FString, 5 SavedLocation FVector (doubles).
/// Export data: DataTable properties, then int32 NumRows at a located offset, then rows (FName key + struct).
internal static class Pacenotes
{
    public sealed class Row
    {
        public string Key = "";
        public float SplineDistanceM, LengthM;
        public List<string> Tokens = new();
        public bool LinkToNext;
        public string Description = "";
        public double[] SavedLocation = [0, 0, 0];
        public override string ToString() => $"{SplineDistanceM,8:0.0} L{LengthM,6:0.0} {string.Join(' ', Tokens)}{(LinkToNext ? " ->" : "")}";
    }

    public sealed class Table
    {
        public byte[] Prefix = [];   // everything before NumRows
        public List<Row> Rows = new();
    }

    static string FStr(byte[] d, ref int p)
    {
        int len = BitConverter.ToInt32(d, p); p += 4;
        if (len == 0) return "";
        string s = len > 0 ? Encoding.Latin1.GetString(d, p, len - 1) : Encoding.Unicode.GetString(d, p, -len * 2 - 2);
        p += len > 0 ? len : -len * 2;
        return s;
    }

    static Row ReadRow(byte[] d, ref int p, LegacyPackage pkg)
    {
        var r = new Row { Key = pkg.NameAt(BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4)) };
        p += 8;
        foreach (var s in Unv.ReadHeader(d, ref p))
        {
            if (s.Zero) continue;
            switch (s.Index)
            {
                case 0: r.SplineDistanceM = BitConverter.ToSingle(d, p); p += 4; break;
                case 1: r.LengthM = BitConverter.ToSingle(d, p); p += 4; break;
                case 2:
                    foreach (var t in Unv.ReadHeader(d, ref p))
                    {
                        if (t.Zero) continue;
                        if (t.Index != 0) throw new InvalidDataException("PacenoteTokenList property");
                        int n = BitConverter.ToInt32(d, p); p += 4;
                        for (int i = 0; i < n; i++) { r.Tokens.Add(pkg.NameAt(BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4))); p += 8; }
                    }
                    break;
                case 3: r.LinkToNext = d[p++] != 0; break;
                case 4: r.Description = FStr(d, ref p); break;
                case 5: r.SavedLocation = [BitConverter.ToDouble(d, p), BitConverter.ToDouble(d, p + 8), BitConverter.ToDouble(d, p + 16)]; p += 24; break;
                default: throw new InvalidDataException($"PacenoteSplineEntry property {s.Index}");
            }
        }
        return r;
    }

    /// Finds NumRows as the first offset from which exactly that many rows parse to the end of the export.
    public static Table Read(LegacyPackage pkg)
    {
        var d = pkg.GetExportData(0);
        for (int at = 2; at + 4 <= d.Length; at++)
        {
            int n = BitConverter.ToInt32(d, at);
            if (n < 0 || n > 100000) continue;
            try
            {
                int p = at + 4;
                var rows = new List<Row>();
                for (int i = 0; i < n; i++) rows.Add(ReadRow(d, ref p, pkg));
                if (p == d.Length) return new Table { Prefix = d[..at], Rows = rows };
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
        }
        throw new InvalidDataException("could not parse the pacenote rows");
    }

    public static byte[] Write(Table t, LegacyPackage pkg)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(t.Prefix);
        w.Write(t.Rows.Count);
        foreach (var r in t.Rows)
        {
            w.Write(pkg.AddExportName(r.Key)); w.Write(0);
            bool loc = r.SavedLocation.Any(v => v != 0);
            var slots = new List<Unv.Slot>
            {
                new(0, r.SplineDistanceM == 0), new(1, r.LengthM == 0), new(2, r.Tokens.Count == 0),
                new(3, !r.LinkToNext), new(4, false), new(5, !loc),
            };
            Unv.WriteHeader(w, slots);
            if (r.SplineDistanceM != 0) w.Write(r.SplineDistanceM);
            if (r.LengthM != 0) w.Write(r.LengthM);
            if (r.Tokens.Count > 0)
            {
                Unv.WriteHeader(w, [new(0, false)]);
                w.Write(r.Tokens.Count);
                foreach (var tok in r.Tokens) { w.Write(pkg.AddExportName(tok)); w.Write(0); }
            }
            if (r.LinkToNext) w.Write((byte)1);
            if (r.Description.Length > 0) { w.Write(r.Description.Length + 1); w.Write(Encoding.ASCII.GetBytes(r.Description)); w.Write((byte)0); }
            else w.Write(0);   // the host stores empty descriptions as an explicit empty FString
            if (loc) foreach (var v in r.SavedLocation) w.Write(v);
        }
        return ms.ToArray();
    }

    /// `pacenote-survey --legacy <root>`: host rows against the host CenterSpline geometry.
    public static int Survey(Options o)
    {
        var root = o.Named["legacy"];
        if (o.Named.TryGetValue("compare", out var other))
        {
            var a = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Pacenote));
            var b = new LegacyPackage(HostPaths.Resolve(other, HostPaths.Pacenote));
            Console.WriteLine($"names {a.Names.Count} vs {b.Names.Count}");
            for (int i = 0; i < Math.Max(a.Names.Count, b.Names.Count); i++)
            {
                var na = i < a.Names.Count ? a.Names[i] : "-"; var nb = i < b.Names.Count ? b.Names[i] : "-";
                if (na != nb) Console.WriteLine($"  name[{i}] {na} | {nb}");
            }
            var da = a.GetExportData(0); var db = b.GetExportData(0);
            int diffs = 0;
            for (int i = 0; i < Math.Min(da.Length, db.Length) && diffs < 12; i++)
                if (da[i] != db[i]) { Console.WriteLine($"  data[{i}] {da[i]:X2} | {db[i]:X2}"); diffs++; }
            Console.WriteLine($"export data {da.Length} vs {db.Length}");
            return 0;
        }
        if (o.Named.TryGetValue("dump-export", out var exp))
        {
            var c = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Cell));
            foreach (var name in exp.Split(','))
            {
                int e = Enumerable.Range(0, c.ExportCount).Single(i => c.ExportName(i) == name && c.ExportClass(i) != "BodySetup");
                var d = c.GetExportData(e);
                int p = 0;
                var h = Unv.ReadHeader(d, ref p);
                Console.WriteLine($"{name} ({c.ExportClass(e)}): {d.Length} bytes, header {p} bytes, slots {string.Join(' ', h.Select(s => s.Zero ? $"{s.Index}z" : $"{s.Index}"))}");
                Console.WriteLine($"  head {Convert.ToHexString(d, p, Math.Min(96, d.Length - p))}");
                Console.WriteLine($"  tail {Convert.ToHexString(d, Math.Max(0, d.Length - 96), Math.Min(96, d.Length))}");
            }
            return 0;
        }
        var pkg = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Pacenote));
        var table = Read(pkg);
        var rt = Write(table, pkg);
        var orig = pkg.GetExportData(0);
        int diff = Enumerable.Range(0, Math.Min(rt.Length, orig.Length)).FirstOrDefault(i => rt[i] != orig[i], -1);
        Console.WriteLine($"{table.Rows.Count} rows; writer round trip {(rt.AsSpan().SequenceEqual(orig) ? "byte-identical" : $"DIFFERS at {diff} of {orig.Length} (written {rt.Length}): orig {Convert.ToHexString(orig, Math.Max(0, diff - 8), Math.Min(32, orig.Length - Math.Max(0, diff - 8)))} new {Convert.ToHexString(rt, Math.Max(0, diff - 8), Math.Min(32, rt.Length - Math.Max(0, diff - 8)))}")}");
        var cell = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Cell));
        var cs = Splines.SplitComponent(cell.GetExportData(cell.FindExport("CenterSpline", "DMSplineComponent"))).Curves;
        var curve = Curvature.FromPoints(cs.Position.Points.Select(p => (p.Out[0] / 100, p.Out[1] / 100)).ToList());
        Console.WriteLine($"host CenterSpline {curve.Length:0.#} m; start line (RaceSector0) at s = {curve.Project(-974.4664, 936.1725):0.#} m");
        var stats = new SortedDictionary<string, List<(double turn, double radius)>>();
        foreach (var r in table.Rows)
        {
            double turn = curve.Turn(r.SplineDistanceM, r.SplineDistanceM + r.LengthM);
            double minR = curve.MinRadius(r.SplineDistanceM, r.SplineDistanceM + r.LengthM);
            if (o.Named.ContainsKey("rows")) Console.WriteLine($"{r}   turn {turn,6:0} deg  minR {minR,6:0} m");
            foreach (var t in r.Tokens)
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(Left|Right)(\d|HP|AcuteHP|OpenHP|Square|Flat|Kink)$"))
                {
                    if (!stats.TryGetValue(t, out var l)) stats[t] = l = new();
                    l.Add((turn, minR));
                }
        }
        foreach (var (k, l) in stats)
            Console.WriteLine($"{k,-12} n={l.Count,3}  turn sign + {l.Count(x => x.turn > 0),3} / - {l.Count(x => x.turn < 0),3}   median |turn| {Median(l.Select(x => Math.Abs(x.turn))),5:0} deg   median min radius {Median(l.Select(x => x.radius)),6:0} m");
        return 0;
    }

    static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a[a.Length / 2]; }

    // ---------------- DR2 -> ACR generation ----------------

    public sealed record Dr2Token(int Call, int Subcall, double GateDistance, string Kind, string? Direction, string? Severity,
                                  double? DistanceCall, string[] Modifiers);

    public static List<Dr2Token> LoadTokens(string stageJson)
    {
        var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(stageJson))!["pacenotes"]!;
        return j["tokens"]!.AsArray().Select(t => new Dr2Token(
            t!["call"]!.GetValue<int>(), t["subcall"]!.GetValue<int>(), t["gate_distance"]!.GetValue<double>(),
            t["kind"]!.GetValue<string>(), t["direction"]?.GetValue<string>(), t["severity"]?.GetValue<string>(),
            t["distance_call_m"]?.GetValue<double>(),
            t["modifiers"]?.AsArray().Select(m => m!.GetValue<string>()).ToArray() ?? [])).ToList();
    }

    public sealed record Corner(double Start, double End, double Turn);

    /// Geometric corners along the spline (metres), sign + = right (heading increasing in UE X->Y, Z up).
    public static List<Corner> FindCorners(Curvature c, Toml m)
    {
        double w = m.Num("layout", "curvature_window_m"), kMin = 1 / m.Num("layout", "corner_radius_m");
        double merge = m.Num("layout", "merge_gap_m"), minTurn = m.Num("layout", "min_turn_deg");
        var runs = new List<(double a, double b, int sign)>();
        const double step = 2;
        for (double s = w / 2; s <= c.Length - w / 2; s += step)
        {
            double k = c.K(s, w);
            int sign = Math.Abs(k) < kMin ? 0 : Math.Sign(k);
            if (sign == 0) continue;
            if (runs.Count > 0 && runs[^1].sign == sign && s - runs[^1].b <= merge + step) runs[^1] = (runs[^1].a, s, sign);
            else runs.Add((s, s, sign));
        }
        return runs.Select(r => new Corner(r.a, Math.Max(r.b, r.a + step), c.Turn(r.a, Math.Max(r.b, r.a + step))))
                   .Where(k => Math.Abs(k.Turn) >= minTurn).ToList();
    }

    sealed class Placed
    {
        public double Start, End;
        public List<string> Tokens = new();
        public string? Conj;          // forced conjunction (from a DR2 distance call)
        public bool Localised;
        public int Call;              // DR2 call (one recorded phrase): rows of the same call are linked
    }

    /// Builds ACR rows from DR2 tokens. toSpline maps a DR2 gate distance to metres along the CenterSpline.
    public static List<Row> Generate(List<Dr2Token> tokens, Func<double, double> toSpline, Curvature curve,
                                     double startS, double finishS, double stopS, Toml m, System.Text.Json.Nodes.JsonObject report)
    {
        var corners = FindCorners(curve, m);
        var unmapped = new SortedDictionary<string, int>();
        void Unmapped(string what) => unmapped[what] = unmapped.GetValueOrDefault(what) + 1;
        string? Map(string section, string key) => m[section].TryGetValue(key, out var v) && v is string s ? s : null;
        double back = m.Num("layout", "search_back_m"), ahead = m.Num("layout", "search_ahead_m");
        double fbAhead = m.Num("layout", "fallback_ahead_m"), fbLen = m.Num("layout", "fallback_length_m");

        var placed = new List<Placed>();
        int lastCorner = -1, localised = 0, fallback = 0, dirAgree = 0, cornerTokens = 0, mappedTokens = 0, droppedAfterFinish = 0;
        string? pendingConj = null;
        double finishRead = double.NaN;
        foreach (var t in tokens)
        {
            double read = toSpline(t.GateDistance);
            switch (t.Kind)
            {
                case "corner":
                {
                    cornerTokens++;
                    var dir = t.Direction is null ? null : Map("direction", t.Direction);
                    var sev = t.Severity is null ? null : Map("severity", t.Severity);
                    if (dir is null || sev is null) { Unmapped($"corner {t.Direction} {t.Severity}"); break; }
                    var p = new Placed { Tokens = { sev.Replace("{Dir}", dir) }, Conj = pendingConj, Call = t.Call };
                    pendingConj = null;
                    foreach (var mod in t.Modifiers)
                        if (Map("modifiers", mod) is { } mt) p.Tokens.Add(mt); else Unmapped($"modifier {mod}");
                    int want = t.Direction == "right" ? 1 : -1;
                    int hit = -1;
                    for (int i = lastCorner + 1; i < corners.Count; i++)
                    {
                        if (corners[i].Start > read + ahead) break;
                        if (corners[i].Start >= read - back && Math.Sign(corners[i].Turn) == want) { hit = i; break; }
                    }
                    if (hit >= 0)
                    {
                        p.Start = corners[hit].Start; p.End = corners[hit].End; p.Localised = true;
                        lastCorner = hit; localised++; dirAgree++;
                    }
                    else
                    {
                        double s0 = Math.Max(read + fbAhead, placed.Count > 0 ? placed[^1].End : 0);
                        p.Start = s0; p.End = s0 + fbLen; fallback++;
                        if (Math.Sign(curve.Turn(p.Start, p.End)) == want) dirAgree++;
                    }
                    if (p.Start >= finishS) { droppedAfterFinish++; break; }
                    placed.Add(p);
                    mappedTokens++;
                    break;
                }
                case "distance":
                {
                    var key = t.DistanceCall is { } dc ? dc.ToString(CultureInfo.InvariantCulture) : "null";
                    if (Map("distance", key) is { } conj) { pendingConj = conj; mappedTokens++; }
                    else Unmapped($"distance {key}");
                    break;
                }
                case "finish":
                    if (Map("finish", "finish") is not null) { finishRead = read; mappedTokens++; } else Unmapped("finish");
                    break;
                default:
                    Unmapped($"kind {t.Kind}");
                    break;
            }
        }

        // tile: overlapping corners are trimmed; straights between corners become DistNN rows
        placed.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i + 1 < placed.Count; i++)
            if (placed[i].End > placed[i + 1].Start) placed[i].End = Math.Max(placed[i].Start + 5, placed[i + 1].Start);
        for (int i = 1; i < placed.Count; i++)
            if (placed[i].Start < placed[i - 1].End) placed[i].Start = placed[i - 1].End;

        double minStraight = m.Num("layout", "straight_min_m"), intoMax = m.Num("layout", "into_max_m");
        var dTok = m.Arr("layout", "straights").Cast<string>().ToArray();
        var dVal = m.Arr("layout", "straight_values_m").Cast<double>().ToArray();
        string longTok = m.Str("layout", "long_straight"); double longM = m.Num("layout", "long_straight_m");
        string StraightToken(double gap) => gap >= longM ? longTok : dTok[Array.FindLastIndex(dVal, v => v <= gap)];

        var rows = new List<Row>();
        void Add(double s, double e, IEnumerable<string> toks, bool link = false) =>
            rows.Add(new Row { SplineDistanceM = (float)s, LengthM = (float)Math.Max(1, e - s), Tokens = toks.ToList(), LinkToNext = link });

        // the finish call ends at the finish line; corners placed past its start are trimmed or dropped
        double finishStart = finishS - m.Num("layout", "finish_length_m");
        int beforeFinish = placed.Count;
        placed.RemoveAll(p => p.Start >= finishStart - 5);
        droppedAfterFinish += beforeFinish - placed.Count;
        if (placed.Count > 0 && placed[^1].End > finishStart) placed[^1].End = finishStart;

        double cursor = startS;
        int straights = 0, links = 0;
        for (int i = 0; i < placed.Count; i++)
        {
            var p = placed[i];
            double gap = p.Start - cursor;
            bool linked = i > 0 && placed[i - 1].Call == p.Call;
            if (linked) { rows[^1].LinkToNext = true; links++; }
            else if (gap >= minStraight) { Add(cursor, p.Start, [StraightToken(gap)]); straights++; }
            var toks = new List<string>();
            if (linked) toks.Add(p.Conj ?? (gap < intoMax ? m.Str("layout", "into") : m.Str("layout", "and")));
            toks.AddRange(p.Tokens);
            Add(p.Start, p.End, toks);
            cursor = p.End;
        }
        if (finishStart - cursor >= minStraight) { Add(cursor, finishStart, [StraightToken(finishStart - cursor)]); straights++; }
        Add(Math.Max(cursor, finishStart), finishS, [Map("finish", "finish") ?? "Finish"]);
        var after = m.Arr("layout", "after_finish").Cast<string>().ToArray();
        double stopAt = Math.Max(finishS + 1, stopS - m.Num("layout", "stop_ahead_m"));
        if (after.Length > 0) Add(stopAt, stopS, after);
        for (int i = 0; i < rows.Count; i++) rows[i].Key = $"R{i:0000}";

        report["rows"] = rows.Count;
        report["dr2_tokens"] = tokens.Count;
        report["dr2_tokens_mapped"] = mappedTokens;
        report["corner_tokens"] = cornerTokens;
        report["geometric_corners"] = corners.Count;
        report["corners_localised_on_geometry"] = localised;
        report["corners_fallback_placed"] = fallback;
        report["corners_direction_agrees_with_geometry"] = dirAgree;
        report["corners_dropped_after_finish"] = droppedAfterFinish;
        report["straight_rows"] = straights;
        report["linked_rows"] = links;
        report["dr2_finish_call_read_m"] = double.IsNaN(finishRead) ? null : Math.Round(finishRead, 1);
        report["unmapped"] = new System.Text.Json.Nodes.JsonObject(unmapped.Select(kv => KeyValuePair.Create(kv.Key, (System.Text.Json.Nodes.JsonNode?)kv.Value)));
        report["rows_preview"] = new System.Text.Json.Nodes.JsonArray(rows.Select(r => (System.Text.Json.Nodes.JsonNode?)$"{r.Key} {r}").ToArray());
        return rows;
    }
}

/// Horizontal heading/curvature of a polyline (x, y in metres, any frame; turn sign: + = heading increases).
internal sealed class Curvature
{
    readonly List<(double x, double y)> _p;
    readonly double[] _s;
    Curvature(List<(double x, double y)> p, double[]? s)
    {
        _p = p; _s = s ?? new double[p.Count];
        if (s is null)
            for (int i = 1; i < p.Count; i++) _s[i] = _s[i - 1] + Math.Sqrt(Math.Pow(p[i].x - p[i - 1].x, 2) + Math.Pow(p[i].y - p[i - 1].y, 2));
    }
    public static Curvature FromPoints(List<(double x, double y)> p) => new(p, null);
    /// Points with their own distance scale (e.g. spline arc length at each spline point).
    public static Curvature FromPoints(List<(double x, double y)> p, double[] s) => new(p, s);
    public double Length => _s[^1];

    int Seg(double s) { int i = Array.BinarySearch(_s, s); if (i < 0) i = ~i - 1; return Math.Clamp(i, 0, _p.Count - 2); }
    public double Heading(double s) { int i = Seg(s); return Math.Atan2(_p[i + 1].y - _p[i].y, _p[i + 1].x - _p[i].x); }
    static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

    /// Signed heading change in degrees from s0 to s1, accumulated per metre.
    public double Turn(double s0, double s1)
    {
        double acc = 0, prev = Heading(s0);
        for (double s = s0 + 1; s <= s1; s += 1) { double h = Heading(s); acc += Wrap(h - prev); prev = h; }
        return acc * 180 / Math.PI;
    }

    /// Smallest radius over [s0, s1] from heading change across a 10 m window.
    public double MinRadius(double s0, double s1, double win = 10)
    {
        double best = double.MaxValue;
        for (double s = s0; s <= s1; s += 2)
        {
            double dh = Math.Abs(Wrap(Heading(s + win / 2) - Heading(s - win / 2)));
            if (dh > 1e-6) best = Math.Min(best, win / dh);
        }
        return best == double.MaxValue ? 9999 : best;
    }

    /// Signed curvature (1/m) at s over a window.
    public double K(double s, double win = 10) => Wrap(Heading(s + win / 2) - Heading(s - win / 2)) / win;

    public double Project(double x, double y)
    {
        double best = double.MaxValue, bs = 0;
        for (int i = 0; i + 1 < _p.Count; i++)
        {
            double ex = _p[i + 1].x - _p[i].x, ey = _p[i + 1].y - _p[i].y, l2 = ex * ex + ey * ey;
            double u = Math.Clamp(((x - _p[i].x) * ex + (y - _p[i].y) * ey) / l2, 0, 1);
            double dx = _p[i].x + u * ex - x, dy = _p[i].y + u * ey - y, d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; bs = _s[i] + u * (_s[i + 1] - _s[i]); }
        }
        return bs;
    }
}
