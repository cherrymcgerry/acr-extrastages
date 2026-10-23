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
        if (o.Named.ContainsKey("fields"))
        {
            // every field of every row: the comparison with a generated table (distance-call shape check)
            Console.WriteLine($"prefix {table.Prefix.Length} bytes: {Convert.ToHexString(table.Prefix)}");
            foreach (var r in table.Rows)
                Console.WriteLine($"{r.Key,-8} s={r.SplineDistanceM,9:0.###} L={r.LengthM,8:0.###} link={(r.LinkToNext ? 1 : 0)} desc='{r.Description}' loc=({string.Join(',', r.SavedLocation.Select(v => v.ToString("0.#")))}) tokens=[{string.Join(' ', r.Tokens)}]");
            return 0;
        }
        if (o.Named.ContainsKey("locations"))
        {
            // each row's SavedLocation projected onto each spline: UE arc length (reparam table) vs SplineDistanceM
            var lines = new[] { "CenterSpline", "IdealSpline" }.ToDictionary(n => n, n =>
                Splines.SplitComponent(cell.GetExportData(cell.FindExport(n, "DMSplineComponent"))).Curves);
            foreach (var (n, c) in lines)
                Console.WriteLine($"{n}: {c.Position.Points.Count} points, UE length {c.Reparam.Points[^1].InVal / 100:0.0} m, looped {c.Position.Looped}");
            Console.WriteLine("row key      SplineDistanceM  LengthM  | Center s  lat   diff | Ideal s  lat   diff | tokens");
            foreach (var r in table.Rows)
            {
                var q = r.SavedLocation;
                var cols = lines.Values.Select(c => { var (s, lat) = ArcLength(c, q[0], q[1]); return $"{s,8:0.0} {lat,5:0.0} {r.SplineDistanceM - s,6:0.0}"; });
                Console.WriteLine($"{r.Key,-10} {r.SplineDistanceM,10:0.0} {r.LengthM,8:0.0}  | {string.Join(" | ", cols)} | {string.Join(' ', r.Tokens)}{(r.LinkToNext ? " ->" : "")}");
            }
            return 0;
        }
        if (o.Named.ContainsKey("shift"))
        {
            // signed agreement of the host's tight corner rows (severity 1-4, HP, square) with each spline's geometry, with
            // the rows shifted by d metres (or scaled about the spline start): the peak shows which spline, which origin
            // and which scale SplineDistanceM is on
            var tight = new System.Text.RegularExpressions.Regex(@"^(Left|Right)([1-4]|HP|Square)$");
            var rows = table.Rows.Select(r => (r, t: r.Tokens.FirstOrDefault(tight.IsMatch))).Where(x => x.t is not null).ToList();
            foreach (var n in new[] { "CenterSpline", "IdealSpline" })
            {
                var c = Splines.SplitComponent(cell.GetExportData(cell.FindExport(n, "DMSplineComponent"))).Curves;
                var pts = c.Position.Points.Select(p => (p.Out[0] / 100, p.Out[1] / 100)).ToList();
                var s = Enumerable.Range(0, pts.Count).Select(i => c.Reparam.Points.MinBy(p => Math.Abs(p.Out[0] - i))!.InVal / 100.0).ToArray();
                var cv = Curvature.FromPoints(pts, s);
                double Score(Func<double, double> f) => rows.Sum(x =>
                {
                    double t = cv.Turn(f(x.r.SplineDistanceM), f(x.r.SplineDistanceM + x.r.LengthM));
                    return (x.t!.StartsWith("Right") ? 1 : -1) * Math.Clamp(t, -90, 90);
                }) / rows.Count;
                var shifts = Enumerable.Range(-30, 61).Select(k => (d: k * 10.0, v: Score(x => x + k * 10.0))).ToList();
                var scales = Enumerable.Range(-20, 41).Select(k => (k: 1 + k * 0.0025, v: Score(x => x * (1 + k * 0.0025)))).ToList();
                var bs = shifts.MaxBy(x => x.v); var bk = scales.MaxBy(x => x.v);
                Console.WriteLine($"{n}: {rows.Count} tight rows; mean signed turn (deg, clamped 90) at shift 0: {Score(x => x):0.0}; best shift {bs.d:+0;-0} m ({bs.v:0.0}); " +
                                  $"best scale {bk.k:0.0000} ({bk.v:0.0}); at shift -242.5 (start-line origin): {Score(x => x - 242.5):0.0}");
                Console.WriteLine("  shift: " + string.Join(" ", shifts.Where(x => x.d % 50 == 0).Select(x => $"{x.d:+0;-0}:{x.v:0}")));
            }
            return 0;
        }
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

    /// UE arc length (m) and lateral distance (m) of world point (x, y) cm on a spline: projected onto the control
    /// polygon for the key parameter, then mapped through the reparam table (distance -> key).
    public static (double s, double lateral) ArcLength(Splines.SplineCurves c, double x, double y)
    {
        var p = c.Position.Points;
        double best = double.MaxValue, key = 0;
        for (int i = 0; i + 1 < p.Count; i++)
        {
            double ax = p[i].Out[0], ay = p[i].Out[1], ex = p[i + 1].Out[0] - ax, ey = p[i + 1].Out[1] - ay, l2 = ex * ex + ey * ey;
            double u = l2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * ex + (y - ay) * ey) / l2, 0, 1);
            double dx = ax + u * ex - x, dy = ay + u * ey - y, d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; key = p[i].InVal + u * (p[i + 1].InVal - p[i].InVal); }
        }
        var rp = c.Reparam.Points;
        for (int i = 0; i + 1 < rp.Count; i++)
            if (key <= rp[i + 1].Out[0])
            {
                double k0 = rp[i].Out[0], k1 = rp[i + 1].Out[0], u = k1 > k0 ? Math.Clamp((key - k0) / (k1 - k0), 0, 1) : 0;
                return ((rp[i].InVal + u * (rp[i + 1].InVal - rp[i].InVal)) / 100, Math.Sqrt(best) / 100);
            }
        return (rp[^1].InVal / 100, Math.Sqrt(best) / 100);
    }

    static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a[a.Length / 2]; }

    // ---------------- DR2 -> ACR generation ----------------

    public sealed record Dr2Token(int Call, int Subcall, double GateDistance, string Kind, string? Direction, string? Severity,
                                  double? DistanceCall, string[] Modifiers, double DistanceM = 0, int Angle = 0, int DelayMs = 0);

    public static List<Dr2Token> LoadTokens(string stageJson)
    {
        var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(stageJson))!["pacenotes"]!;
        return j["tokens"]!.AsArray().Select(t => new Dr2Token(
            t!["call"]!.GetValue<int>(), t["subcall"]!.GetValue<int>(), t["gate_distance"]!.GetValue<double>(),
            t["kind"]!.GetValue<string>(), t["direction"]?.GetValue<string>(), t["severity"]?.GetValue<string>(),
            t["distance_call_m"]?.GetValue<double>(),
            t["modifiers"]?.AsArray().Select(m => m!.GetValue<string>()).ToArray() ?? [],
            t["distance_m"]?.GetValue<double>() ?? 0, t["angle"]?.GetValue<int>() ?? 0, t["delay_ms"]?.GetValue<int>() ?? 0)).ToList();
    }

    /// Median min radius (m) per severity on the host stage (pacenote-survey on Hafren South): the yardstick for the
    /// report's severity hint.
    static readonly Dictionary<string, double> HostRadius = new()
    {
        ["AcuteHP"] = 6, ["HP"] = 7, ["OpenHP"] = 10, ["Square"] = 8, ["1"] = 14, ["2"] = 20, ["3"] = 26, ["4"] = 45, ["5"] = 65, ["6"] = 100,
    };
    static readonly HashSet<string> Tight = ["AcuteHP", "HP", "OpenHP", "Square", "1", "2", "3", "4"];

    /// Markdown report: every generated row with the DR2 centreline geometry at its position. A corner call is checked
    /// against the geometric corners (FindCorners) overlapping [s - 20, s + L + 20] m:
    ///   DIRECTION: no corner of the called direction there, but one of the other direction of at least 20 deg
    ///   NO CORNER: a tight call (severity 1-4, hairpin, square) with no geometric corner there and < 10 deg over the row
    /// The severity hint (min radius vs the host's median for that severity, outside 0.4x..2.5x) is not a mismatch.
    public sealed record Check(double S, string Call, string Measured, string Flag, string Hint, string Line);

    static (List<Check> rows, int mismatches, int hints, int corners) CheckRows(List<Row> rows, List<Corner> corners, Curvature curve, double startS)
    {
        var re = new System.Text.RegularExpressions.Regex(@"^(Left|Right)(\d|HP|AcuteHP|OpenHP|Square|Flat|Kink)$");
        var lines = new List<Check>();
        int mismatches = 0, hints = 0, cornerRows = 0;
        foreach (var r in rows)
        {
            double s = r.SplineDistanceM, e = s + r.LengthM;
            var call = string.Join(' ', r.Tokens) + (r.LinkToNext ? " →" : "");
            var tok = r.Tokens.Select(t => re.Match(t)).FirstOrDefault(x => x.Success);
            if (tok is null)
            {
                lines.Add(new(s, call, "", "", "", $"| {s - startS,7:0} | {r.LengthM,4:0} | {call} | | | | |"));
                continue;
            }
            cornerRows++;
            int sign = tok.Groups[1].Value == "Right" ? 1 : -1;
            string sev = tok.Groups[2].Value;
            double spanTurn = curve.Turn(s, e);
            var near = corners.Where(k => k.End >= s - 20 && k.Start <= e + 20).ToList();
            var same = near.Where(k => Math.Sign(k.Turn) == sign).MaxBy(k => Math.Abs(k.Turn));
            var opp = near.Where(k => Math.Sign(k.Turn) != sign).MaxBy(k => Math.Abs(k.Turn));
            var geo = same ?? opp;
            double minR = geo is not null ? curve.MinRadius(geo.Start, geo.End) : curve.MinRadius(s, Math.Max(e, s + 10));
            string measured = geo is not null
                ? $"{(geo.Turn > 0 ? "right" : "left")} {Math.Abs(geo.Turn):0}° (at {geo.Start - startS:0}–{geo.End - startS:0} m), min R {minR:0} m"
                : $"straight-ish: {(spanTurn > 0 ? "right" : "left")} {Math.Abs(spanTurn):0}° over the row, min R {minR:0} m";
            string flag = "";
            if (same is null && opp is not null && Math.Abs(opp.Turn) >= 20) flag = "**DIRECTION**";
            else if (same is null && Tight.Contains(sev) && Math.Abs(spanTurn) < 10) flag = "**NO CORNER**";
            if (flag.Length > 0) mismatches++;
            string hint = "";
            if (HostRadius.TryGetValue(sev, out var hr) && (minR < hr * 0.4 || minR > hr * 2.5))
            {
                hint = minR < hr * 0.4 ? $"tighter than a typical {sev} (~{hr:0} m)" : $"wider than a typical {sev} (~{hr:0} m)";
                hints++;
            }
            lines.Add(new(s, call, measured, flag, hint, $"| {s - startS,7:0} | {r.LengthM,4:0} | {call} | {(sign > 0 ? "right" : "left")} {sev} | {measured} | {flag} | {hint} |"));
        }
        return (lines, mismatches, hints, cornerRows);
    }

    /// Markdown report for the generated rows (and, if given, the same calls placed by the v1.4 greedy assignment):
    /// per DR2 corner call its read point, where each version puts the row and the lead between them, the drift per
    /// 500 m, v1.4's flagged rows re-checked, and every row with the geometry there.
    public static (int mismatches, int hints, int corners) Report(List<Row> rows, Curvature curve, double startS, Toml m, string stage, string path,
                                                                  List<Placement>? now = null, List<Row>? oldRows = null, List<Placement>? old = null)
    {
        var corners = FindCorners(curve, m);
        var sb = new System.Text.StringBuilder();
        var (lines, mismatches, hints, cornerRows) = CheckRows(rows, corners, curve, startS);
        sb.AppendLine($"# Pacenotes vs DR2 centreline: {stage}");
        sb.AppendLine();
        sb.AppendLine($"Generated by stage-build. {rows.Count} rows, {cornerRows} corner calls, **{mismatches} mismatches**, {hints} severity hints.");
        sb.AppendLine();
        sb.AppendLine("Distances are metres from the start line along the rebuilt CenterSpline: the scale the game uses for `SplineDistanceM` " +
                      "(verified on the host table: its corner rows fit its CenterSpline best at shift 0 m and scale 1.000; acr-ref pacenote-survey --shift). " +
                      "The game calls a row about LookAheadTime (Normal 5.75 s) before the car reaches it, at a reference speed from the spline's curvature (DA_PacenoteConfig).");
        sb.AppendLine();
        if (now is not null && old is not null && oldRows is not null)
        {
            var (_, oldMis, oldHints, _) = CheckRows(oldRows, corners, curve, startS);
            string Key(Placement p) => $"{p.Call}.{p.Subcall}";
            var o = old.ToDictionary(Key);
            var pairs = now.Where(p => o.ContainsKey(Key(p))).Select(p => (n: p, o: o[Key(p)])).OrderBy(x => x.n.Call).ThenBy(x => x.n.Subcall).ToList();
            sb.AppendLine("## Drift: v1.4 (greedy) vs v1.5 (aligned)");
            sb.AppendLine();
            sb.AppendLine($"- v1.4: {oldMis} mismatches, {oldHints} severity hints; v1.5: {mismatches} mismatches, {hints} severity hints.");
            sb.AppendLine($"- Corner calls on geometry: v1.4 {old.Count(p => p.Localised)} of {old.Count}, v1.5 {now.Count(p => p.Localised)} of {now.Count}.");
            sb.AppendLine($"- Rows that moved by more than 30 m: {pairs.Count(x => Math.Abs(x.n.Start - x.o.Start) > 30)}.");
            sb.AppendLine("- *Lead* = row start − DR2 read point. DR2 reads a note 50–150 m before its corner, so the first subcall of a call should sit there; " +
                          "a lead well above that means the row is on a later corner than the one DR2 called.");
            sb.AppendLine();
            sb.AppendLine("| DR2 read point (m) | first-subcall calls | v1.4 mean lead (m) | v1.4 max | v1.5 mean lead (m) | v1.5 max | v1.4 − v1.5 mean shift (m) |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (var g in pairs.Where(x => x.n.Subcall == 0).GroupBy(x => (int)((x.n.Read - startS) / 500)).OrderBy(g => g.Key))
                sb.AppendLine($"| {g.Key * 500}–{g.Key * 500 + 500} | {g.Count()} | {g.Average(x => x.o.Start - x.o.Read):0} | {g.Max(x => x.o.Start - x.o.Read):0} | " +
                              $"{g.Average(x => x.n.Start - x.n.Read):0} | {g.Max(x => x.n.Start - x.n.Read):0} | {g.Average(x => x.o.Start - x.n.Start):+0;-0;0} |");
            sb.AppendLine();
            sb.AppendLine("## Every corner call");
            sb.AppendLine();
            sb.AppendLine("| DR2 call | Called | DR2 angle | DR2 read (m) | v1.4 row (m) | v1.4 lead | v1.5 row (m) | v1.5 lead | v1.4 − v1.5 (m) | Corner at the v1.5 row |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (var (n, ol) in pairs)
            {
                var c = corners.Where(k => k.End >= n.Start - 5 && k.Start <= n.End + 5 && Math.Sign(k.Turn) == n.Want).MaxBy(k => Math.Abs(k.Turn));
                string geo = c is null ? (n.Localised ? "" : "fallback (no corner of this direction matched)")
                    : $"{(c.Turn > 0 ? "right" : "left")} {Math.Abs(c.Turn):0}°, min R {curve.MinRadius(c.Start, c.End):0} m{(n.Localised ? "" : " (fallback)")}";
                double d = ol.Start - n.Start;
                sb.AppendLine($"| {n.Call}.{n.Subcall} | {n.Token} | {n.Angle} | {n.Read - startS:0} | {ol.Start - startS:0} | {ol.Start - ol.Read:0} | " +
                              $"{n.Start - startS:0} | {n.Start - n.Read:0} | {(Math.Abs(d) > 30 ? $"**{d:+0;-0}**" : $"{d:+0;-0;0}")} | {geo} |");
            }
            sb.AppendLine();
            sb.AppendLine("## v1.4's flagged rows, re-checked");
            sb.AppendLine();
            sb.AppendLine("| v1.4 row (m) | Call | v1.4 flag | DR2 call | v1.5 row (m) | v1.5 measured | v1.5 flag |");
            sb.AppendLine("|---:|---|---|---|---:|---|---|");
            var (oldLines, _, _, _) = CheckRows(oldRows, corners, curve, startS);
            foreach (var ol in oldLines.Where(x => x.Flag.Length > 0 || x.Hint.Length > 0))
            {
                var op = old.Where(p => Math.Abs(p.Start - ol.S) < 6).MinBy(p => Math.Abs(p.Start - ol.S));
                var np = op is null ? null : now.FirstOrDefault(p => Key(p) == Key(op));
                var nl = np is null ? null : lines.Where(x => x.Measured.Length > 0).MinBy(x => Math.Abs(x.S - np.Start));
                sb.AppendLine($"| {ol.S - startS:0} | {ol.Call} | {ol.Flag}{(ol.Flag.Length > 0 && ol.Hint.Length > 0 ? "; " : "")}{ol.Hint} | {(op is null ? "?" : Key(op))} | " +
                              $"{(np is null ? "" : $"{np.Start - startS:0}")} | {nl?.Measured} | {(nl is null ? "" : (nl.Flag + (nl.Flag.Length > 0 && nl.Hint.Length > 0 ? "; " : "") + nl.Hint) is { Length: > 0 } f ? f : "ok")} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Rows (v1.5)");
        sb.AppendLine();
        sb.AppendLine("- *Distance*: metres from the start line along the rebuilt CenterSpline (the in-game stage distance), where the row starts; *L*: row length.");
        sb.AppendLine("- *Measured*: the strongest geometric corner on the DR2 centreline overlapping the row ±20 m (heading change, its extent, smallest radius over a 10 m window); right = clockwise seen from above.");
        sb.AppendLine("- **DIRECTION**: no corner of the called direction there but one the other way (≥ 20°). **NO CORNER**: a severity 1–4 / hairpin / square call with no corner there (< 10° over the row).");
        sb.AppendLine("- Severity hint (not counted): the smallest radius is outside 0.4×–2.5× the host stage's median for that severity (HP 7, 2 20, 3 26, 4 45, 5 65, 6 100 m). DR2 and ACR grade severity differently, so treat it as a pointer only.");
        sb.AppendLine();
        sb.AppendLine("| Distance (m) | L (m) | Call | Called | Measured | Mismatch | Severity hint |");
        sb.AppendLine("|---:|---:|---|---|---|---|---|");
        foreach (var l in lines) sb.AppendLine(l.Line);
        File.WriteAllText(path, sb.ToString());
        return (mismatches, hints, cornerRows);
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
        public int Subcall, Angle, Want;
        public double Read;           // DR2 read point on the spline
        public Placed? PrevInSeq;     // previous corner in DR2 order (null: first corner of the stage)
        public double? WishBefore;    // DR2 spoken distance (m, >= min_dr2_m) between PrevInSeq and this corner
    }

    /// v1.4: each corner call takes the first not yet used geometric corner of its direction starting within
    /// [read - back, read + ahead]. One early mismatch (two subcalls in one merged corner) shifts every later call
    /// by a corner until the window forces a reset.
    static int[] AssignGreedy(List<Placed> calls, List<Corner> corners, double back, double ahead)
    {
        var hit = Enumerable.Repeat(-1, calls.Count).ToArray();
        int last = -1;
        for (int j = 0; j < calls.Count; j++)
            for (int i = last + 1; i < corners.Count; i++)
            {
                if (corners[i].Start > calls[j].Read + ahead) break;
                if (corners[i].Start >= calls[j].Read - back && Math.Sign(corners[i].Turn) == calls[j].Want) { hit[j] = i; last = i; break; }
            }
        return hit;
    }

    /// Order-preserving alignment of the DR2 corner calls (in spoken order) to the geometric corners, minimising a
    /// cost: the lead (corner start - DR2 read point) outside DR2's lead window (first subcall lead_first_m, later
    /// subcalls lead_later_m; docs/dr2-formats.md: notes are read 50-150 m before their corner), a severity term, and
    /// unplaced_cost per call left to the fallback. Directions must match. Consecutive subcalls of one call with the
    /// same direction may share one geometric corner (a merged "right 2 into right 1").
    static int[] AssignAligned(List<Placed> calls, List<Corner> corners, Curvature curve, Toml m)
    {
        var first = m.Arr("layout", "lead_first_m").Cast<double>().ToArray();
        var later = m.Arr("layout", "lead_later_m").Cast<double>().ToArray();
        double scale = m.Num("layout", "lead_cost_scale_m"), unplaced = m.Num("layout", "unplaced_cost"), share = m.Num("layout", "share_cost");
        double maxLead = m.Num("layout", "lead_max_m"), back = m.Num("layout", "search_back_m");
        var minR = corners.Select(c => curve.MinRadius(c.Start, c.End)).ToArray();
        double Cost(int j, int i)
        {
            var (p, c) = (calls[j], corners[i]);
            if (Math.Sign(c.Turn) != p.Want) return double.PositiveInfinity;
            double lead = c.Start - p.Read;
            if (lead < -back || lead > maxLead) return double.PositiveInfinity;
            var w = p.Subcall == 0 ? first : later;
            double cost = lead < w[0] ? (w[0] - lead) / scale : lead > w[1] ? (lead - w[1]) / scale : 0;
            // severity: tight calls (DR2 angle >= 65: 2, 1, square, hairpin) on open corners, fast calls (<= 25) on hairpins
            if (p.Angle >= 65 && (minR[i] > 80 || Math.Abs(c.Turn) < 20)) cost += 1;
            if (p.Angle <= 25 && Math.Abs(c.Turn) > 120) cost += 1;
            return cost;
        }
        int n = calls.Count, k = corners.Count;
        // dp[j, c + 1]: best cost after j calls with corner c the last one used (-1: none)
        var dp = new double[n + 1, k + 1]; var from = new (int c, int hit)[n + 1, k + 1];
        for (int j = 0; j <= n; j++) for (int c = 0; c <= k; c++) dp[j, c] = double.PositiveInfinity;
        dp[0, 0] = 0;
        for (int j = 0; j < n; j++)
            for (int c = -1; c < k; c++)
            {
                double cur = dp[j, c + 1];
                if (double.IsPositiveInfinity(cur)) continue;
                if (cur + unplaced < dp[j + 1, c + 1]) { dp[j + 1, c + 1] = cur + unplaced; from[j + 1, c + 1] = (c, -1); }
                bool canShare = c >= 0 && j > 0 && calls[j - 1].Call == calls[j].Call && calls[j - 1].Want == calls[j].Want;
                for (int i = canShare ? c : c + 1; i < k; i++)
                {
                    if (corners[i].Start > calls[j].Read + maxLead) break;
                    double v = cur + Cost(j, i) + (i == c ? share : 0);
                    if (v < dp[j + 1, i + 1]) { dp[j + 1, i + 1] = v; from[j + 1, i + 1] = (c, i); }
                }
            }
        int bc = Enumerable.Range(0, k + 1).MinBy(c => dp[n, c]);
        var hit = new int[n];
        for (int j = n; j > 0; j--) { var (pc, h) = from[j, bc]; hit[j - 1] = h; bc = pc + 1; }
        return hit;
    }

    /// Builds ACR rows from DR2 tokens. toSpline maps a DR2 gate distance to metres along the CenterSpline.
    public sealed record Placement(int Call, int Subcall, int Want, int Angle, string Token, double Read, double Start, double End, bool Localised);

    public static List<Row> Generate(List<Dr2Token> tokens, Func<double, double> toSpline, Curvature curve,
                                     double startS, double finishS, double stopS, Toml m, System.Text.Json.Nodes.JsonObject report,
                                     List<Placement>? placements = null, string? assignment = null)
    {
        var corners = FindCorners(curve, m);
        var unmapped = new SortedDictionary<string, int>();
        void Unmapped(string what) => unmapped[what] = unmapped.GetValueOrDefault(what) + 1;
        string? Map(string section, string key) => m[section].TryGetValue(key, out var v) && v is string s ? s : null;
        double back = m.Num("layout", "search_back_m"), ahead = m.Num("layout", "search_ahead_m");
        double fbAhead = m.Num("layout", "fallback_ahead_m"), fbLen = m.Num("layout", "fallback_length_m");

        var placed = new List<Placed>();
        int localised = 0, fallback = 0, dirAgree = 0, cornerTokens = 0, mappedTokens = 0, droppedAfterFinish = 0;
        string? pendingConj = null;
        double finishRead = double.NaN;

        // DR2 spoken distances (>= min_dr2_m) become "wishes" on the gap between the corner before and the corner after them
        // in DR2 order; the straight row for that gap is then called with the snapped DR2 value (see the tiling below)
        bool dr2Dist = m["distance_calls"].TryGetValue("source", out var srcv) && srcv is "dr2";
        double minDr2 = m["distance_calls"].TryGetValue("min_dr2_m", out var mdv) && mdv is double mdd ? mdd : 40;
        double? pendingGap = null, trailingWish = null;
        Placed? lastSeq = null;
        int dr2DistCalls = 0, dr2DistSuperseded = 0, distNull = 0;
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
                    var p = new Placed { Tokens = { sev.Replace("{Dir}", dir) }, Conj = pendingConj, Call = t.Call, Subcall = t.Subcall,
                                         Angle = t.Angle, Read = read, Want = t.Direction == "right" ? 1 : -1,
                                         PrevInSeq = lastSeq, WishBefore = pendingGap };
                    pendingConj = null; pendingGap = null; lastSeq = p;
                    foreach (var mod in t.Modifiers)
                        if (Map("modifiers", mod) is { } mt) p.Tokens.Add(mt); else Unmapped($"modifier {mod}");
                    placed.Add(p);
                    mappedTokens++;
                    break;
                }
                case "distance":
                {
                    var key = t.DistanceCall is { } dc ? dc.ToString(CultureInfo.InvariantCulture) : "null";
                    if (Map("distance", key) is { } conj) { pendingConj = conj; mappedTokens++; }
                    else if (dr2Dist && t.DistanceCall is { } dc2 && dc2 >= minDr2)
                    {
                        dr2DistCalls++;
                        if (pendingGap is not null) dr2DistSuperseded++;
                        pendingGap = dc2; mappedTokens++;
                    }
                    else if (dr2Dist && t.DistanceCall is null)
                    {
                        // distance call of unknown value (unverified distanceLink code): the straight's length decides
                        distNull++; mappedTokens++;
                    }
                    else Unmapped($"distance {key}");
                    break;
                }
                case "finish":
                    if (Map("finish", "finish") is not null) { finishRead = read; mappedTokens++; trailingWish = pendingGap; pendingGap = null; } else Unmapped("finish");
                    break;
                default:
                    Unmapped($"kind {t.Kind}");
                    break;
            }
        }

        // corner localisation on the geometry
        var mode = assignment ?? (m["layout"].TryGetValue("assignment", out var am) && am is string ams ? ams : "aligned");
        var hits = mode switch
        {
            "greedy" => AssignGreedy(placed, corners, back, ahead),
            "aligned" => AssignAligned(placed, corners, curve, m),
            _ => throw new ArgumentException($"[layout] assignment must be greedy or aligned, not {mode}"),
        };
        foreach (var grp in Enumerable.Range(0, placed.Count).Where(i => hits[i] >= 0).GroupBy(i => hits[i]))
        {
            // subcalls sharing one merged geometric corner split it at its straightest points
            var c = corners[grp.Key]; var idx = grp.ToList();
            var cuts = new List<double> { c.Start };
            for (int k = 1; k < idx.Count; k++)
            {
                double a = c.Start + (c.End - c.Start) * (k - 0.5) / idx.Count, b = c.Start + (c.End - c.Start) * (k + 0.5) / idx.Count;
                double best = a, bk = double.MaxValue;
                for (double s = a; s <= b; s += 2) { double kk = Math.Abs(curve.K(s, 10)); if (kk < bk) { bk = kk; best = s; } }
                cuts.Add(best);
            }
            cuts.Add(c.End);
            for (int k = 0; k < idx.Count; k++) { placed[idx[k]].Start = cuts[k]; placed[idx[k]].End = cuts[k + 1]; placed[idx[k]].Localised = true; }
        }
        for (int i = 0; i < placed.Count; i++)
        {
            var p = placed[i];
            if (p.Localised) { localised++; dirAgree++; continue; }
            double prevEnd = placed.Take(i).Where(q => q.Localised || q.End > 0).Select(q => q.End).DefaultIfEmpty(0).Max();
            double s0 = Math.Max(p.Read + fbAhead, prevEnd);
            p.Start = s0; p.End = s0 + fbLen; fallback++;
            if (Math.Sign(curve.Turn(p.Start, p.End)) == p.Want) dirAgree++;
        }
        droppedAfterFinish += placed.RemoveAll(p => p.Start >= finishS);
        report["assignment"] = mode;
        placements?.AddRange(placed.Select(p => new Placement(p.Call, p.Subcall, p.Want, p.Angle, p.Tokens[0], p.Read, p.Start, p.End, p.Localised)));

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
        // straight_snap: "nearest" (the host's DistNN rows are the spoken distance rounded to the nearest call: its 58 m gap is
        // Dist60, 54 m Dist50, 66 m Dist70) or "floor" (largest token not above the gap, v1.5 behaviour)
        bool snapNearest = !(m["layout"].TryGetValue("straight_snap", out var snv) && snv is "floor");
        int SnapIndex(double metres)
        {
            if (!snapNearest) return Math.Max(0, Array.FindLastIndex(dVal, v => v <= metres));
            int best = 0;
            for (int k = 1; k < dVal.Length; k++) if (Math.Abs(dVal[k] - metres) < Math.Abs(dVal[best] - metres)) best = k;
            return best;
        }
        string StraightToken(double gap) => gap >= longM ? longTok : dTok[SnapIndex(gap)];

        // DR2 distance calls: the straight between two corners is called with the snapped DR2 value when the geometric gap agrees
        // (within agree_ratio); gaps of short_min_m..straight_min_m are called too when DR2 spoke a short distance
        // (<= short_max_wish_m), like the host's 25-40 m DistNN rows; otherwise the geometry decides
        double agree = m["distance_calls"].TryGetValue("agree_ratio", out var arv) && arv is double ard ? ard : 1.5;
        double shortMin = m["distance_calls"].TryGetValue("short_min_m", out var smv) && smv is double smd ? smd : 20;
        double shortMaxWish = m["distance_calls"].TryGetValue("short_max_wish_m", out var swv) && swv is double swd ? swd : 60;
        bool chainDist = !(m["distance_calls"].TryGetValue("link_to_previous", out var ltp) && ltp is false);
        int dr2Emitted = 0, dr2Disagree = 0, dr2Short = 0, dr2ShortSkipped = 0, dr2Unplaced = 0, geoRows = 0;
        var distDetail = new List<string>();
        (string? tok, string how) DistanceToken(double gap, double? wish)
        {
            string? geo = gap >= minStraight ? StraightToken(gap) : null;
            if (wish is not { } w) return (geo, "geometry");
            string snapped = w >= longM ? longTok : dTok[SnapIndex(w)];
            if (gap >= minStraight) return gap >= w / agree && gap <= w * agree ? (snapped, "dr2") : (geo, "geometry; dr2 disagrees");
            if (gap >= shortMin && w <= shortMaxWish) return (snapped, "dr2 short");
            return (null, "dr2 call dropped: gap too short");
        }

        var rows = new List<Row>();
        void Add(double s, double e, IEnumerable<string> toks, bool link = false) =>
            rows.Add(new Row { SplineDistanceM = (float)s, LengthM = (float)Math.Max(1, e - s), Tokens = toks.ToList(), LinkToNext = link });

        // the finish call ends at the finish line; corners placed past its start are trimmed or dropped
        double finishStart = finishS - m.Num("layout", "finish_length_m");
        int beforeFinish = placed.Count;
        placed.RemoveAll(p => p.Start >= finishStart - 5);
        droppedAfterFinish += beforeFinish - placed.Count;
        if (placed.Count > 0 && placed[^1].End > finishStart) placed[^1].End = finishStart;

        if (pendingGap is not null && trailingWish is null) trailingWish = pendingGap;
        double cursor = startS;
        int straights = 0, links = 0;
        void Straight(double from, double to, double? wish, bool link, string where)
        {
            var (tok, how) = DistanceToken(to - from, wish);
            if (wish is not null)
            {
                if (how == "dr2") dr2Emitted++;
                else if (how == "dr2 short") { dr2Emitted++; dr2Short++; }
                else if (how.StartsWith("geometry")) dr2Disagree++;
                else dr2ShortSkipped++;
            }
            else if (tok is not null) geoRows++;
            if (wish is not null || tok is not null)
                distDetail.Add($"{where} {from - startS:0}-{to - startS:0} m (gap {to - from:0}): DR2 {(wish is { } wv ? wv.ToString("0") : "-")} -> {tok ?? "no row"} ({how})");
            if (tok is null) return;
            // The host's encoding (45 vanilla tables, 1360 DistNN rows): a distance is the tail of the corner row before it, i.e.
            // that row has LinkToNext = true (1246 of 1360; the other 114 are unlinked leftovers) and the DistNN row itself never
            // links onward. An unlinked DistNN row after a corner is neither shown nor spoken in game (the v1.5-v1.11 tables).
            if (rows.Count > 0 && chainDist) rows[^1].LinkToNext = true;
            Add(from, to, [tok], false); straights++;
        }
        for (int i = 0; i < placed.Count; i++)
        {
            var p = placed[i];
            double gap = p.Start - cursor;
            bool linked = i > 0 && placed[i - 1].Call == p.Call;
            double? wish = i == 0 ? (p.PrevInSeq is null ? p.WishBefore : null) : (ReferenceEquals(p.PrevInSeq, placed[i - 1]) ? p.WishBefore : null);
            bool spoken = false;   // a DR2 distance call inside one phrase: "Left 3 ... 100 Right 2"
            if (linked)
            {
                if (wish is { } lw && DistanceToken(gap, lw).tok is not null)
                {
                    rows[^1].LinkToNext = true; links++;
                    Straight(cursor, p.Start, lw, false, $"call {p.Call}");
                    spoken = true;
                }
                else { rows[^1].LinkToNext = true; links++; if (wish is not null) Straight(cursor, p.Start, wish, false, $"call {p.Call}"); }
            }
            else Straight(cursor, p.Start, wish, false, $"call {p.Call}");
            var toks = new List<string>();
            if (linked && !spoken) toks.Add(p.Conj ?? (gap < intoMax ? m.Str("layout", "into") : m.Str("layout", "and")));
            toks.AddRange(p.Tokens);
            Add(p.Start, p.End, toks);
            cursor = p.End;
        }
        Straight(cursor, finishStart, placed.Count > 0 && ReferenceEquals(lastSeq, placed[^1]) ? trailingWish : (placed.Count == 0 ? trailingWish : null), false, "finish");
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
        dr2Unplaced = dr2DistCalls - dr2DistSuperseded - dr2Emitted - dr2Disagree - dr2ShortSkipped;   // corner dropped / not adjacent after sorting
        report["distance_calls"] = new System.Text.Json.Nodes.JsonObject
        {
            ["agree_ratio"] = agree, ["short_min_m"] = shortMin, ["short_max_wish_m"] = shortMaxWish, ["straight_min_m"] = minStraight,
            ["source"] = dr2Dist ? "dr2" : "geometry",
            ["dr2_calls"] = dr2DistCalls,                       // DR2 distance tokens >= min_dr2_m
            ["dr2_superseded"] = dr2DistSuperseded,             // a second distance token before the same corner: the later one wins
            ["dr2_null_value"] = distNull,                      // distance tokens with an unverified value code: geometry decides
            ["rows_called_with_dr2_value"] = dr2Emitted,
            ["rows_short_gap_with_dr2_value"] = dr2Short,
            ["dr2_disagrees_geometry_used"] = dr2Disagree,
            ["dr2_dropped_short_gap"] = dr2ShortSkipped,
            ["dr2_dropped_corner_not_adjacent"] = dr2Unplaced,
            ["rows_geometry_only"] = geoRows,
            ["tokens_allowed"] = new System.Text.Json.Nodes.JsonArray(dTok.Append(longTok).Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()),
            ["snap"] = snapNearest ? "nearest" : "floor",
            ["link_to_previous"] = chainDist,
            ["detail"] = new System.Text.Json.Nodes.JsonArray(distDetail.Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()),
        };
        report["linked_rows"] = links;
        report["dr2_finish_call_read_m"] = double.IsNaN(finishRead) ? null : Math.Round(finishRead, 1);
        report["unmapped"] = new System.Text.Json.Nodes.JsonObject(unmapped.Select(kv => KeyValuePair.Create(kv.Key, (System.Text.Json.Nodes.JsonNode?)kv.Value)));
        report["corner_placement"] = new System.Text.Json.Nodes.JsonArray(placed.Select(p => (System.Text.Json.Nodes.JsonNode?)
            $"call {p.Call}.{p.Subcall} {(p.Want > 0 ? "R" : "L")} angle {p.Angle}: read {p.Read - startS:0} -> row {p.Start - startS:0} (lead {p.Start - p.Read:0} m){(p.Localised ? "" : " fallback")}").ToArray());
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
