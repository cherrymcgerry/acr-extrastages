using System.Buffers.Binary;

namespace AcrRef;

/// FInterpCurve / FSplineCurves / UDMSplineMetadata in unversioned form, plus UE's spline math
/// (auto tangents, Legendre-Gauss segment length, reparam table).
internal static class Splines
{
    public const byte CIM_Linear = 0, CIM_CurveAuto = 1;

    /// Kind: 'f' = float, 'v' = FVector (3 doubles), 'q' = FQuat (4 doubles).
    public sealed class Point
    {
        public float InVal;
        public double[] Out = [], Arrive = [], Leave = [];
        public byte Mode;
    }

    public sealed class Curve
    {
        public char Kind;
        public List<Point> Points = new();
        public bool Looped;
        public float LoopKeyOffset;
        public bool IsZero => Points.Count == 0 && !Looped && LoopKeyOffset == 0;
    }

    public sealed class SplineCurves
    {
        public Curve Position = new() { Kind = 'v' }, Rotation = new() { Kind = 'q' }, Scale = new() { Kind = 'v' }, Reparam = new() { Kind = 'f' };
        public uint Version;
        public List<int> PresentSlots = new();
    }

    static int Width(char kind) => kind switch { 'f' => 1, 'v' => 3, 'q' => 4, _ => throw new ArgumentException() };

    static double[] ReadValue(byte[] d, ref int p, char kind)
    {
        int n = Width(kind);
        var v = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (kind == 'f') { v[i] = BitConverter.ToSingle(d, p); p += 4; }
            else { v[i] = BitConverter.ToDouble(d, p); p += 8; }
        }
        return v;
    }

    static byte[] ValueBytes(double[] v, char kind)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var x in v) { if (kind == 'f') w.Write((float)x); else w.Write(x); }
        return ms.ToArray();
    }

    public static Curve ReadCurve(byte[] d, ref int p, char kind)
    {
        var c = new Curve { Kind = kind };
        foreach (var s in Unv.ReadHeader(d, ref p))
        {
            if (s.Zero) continue;
            switch (s.Index)
            {
                case 0:
                    int n = BitConverter.ToInt32(d, p); p += 4;
                    for (int i = 0; i < n; i++)
                    {
                        var pt = new Point { Out = new double[Width(kind)], Arrive = new double[Width(kind)], Leave = new double[Width(kind)] };
                        foreach (var ps in Unv.ReadHeader(d, ref p))
                        {
                            if (ps.Zero) continue;
                            switch (ps.Index)
                            {
                                case 0: pt.InVal = BitConverter.ToSingle(d, p); p += 4; break;
                                case 1: pt.Out = ReadValue(d, ref p, kind); break;
                                case 2: pt.Arrive = ReadValue(d, ref p, kind); break;
                                case 3: pt.Leave = ReadValue(d, ref p, kind); break;
                                case 4: pt.Mode = d[p++]; break;
                                default: throw new InvalidDataException($"curve point property {ps.Index}");
                            }
                        }
                        c.Points.Add(pt);
                    }
                    break;
                case 1: c.Looped = d[p++] != 0; break;
                case 2: c.LoopKeyOffset = BitConverter.ToSingle(d, p); p += 4; break;
                default: throw new InvalidDataException($"curve property {s.Index}");
            }
        }
        return c;
    }

    public static void WriteCurve(BinaryWriter w, Curve c)
    {
        var slots = new List<Unv.Slot>();
        if (c.Points.Count > 0) slots.Add(new(0, false));
        if (c.Looped) slots.Add(new(1, false));
        if (c.LoopKeyOffset != 0) slots.Add(new(2, false));
        Unv.WriteHeader(w, slots);
        if (c.Points.Count > 0)
        {
            w.Write(c.Points.Count);
            foreach (var pt in c.Points)
            {
                var vals = new[]
                {
                    BitConverter.GetBytes(pt.InVal), ValueBytes(pt.Out, c.Kind), ValueBytes(pt.Arrive, c.Kind),
                    ValueBytes(pt.Leave, c.Kind), [pt.Mode],
                };
                Unv.WriteHeader(w, vals.Select((v, i) => new Unv.Slot(i, Unv.AllZero(v))).ToList());
                foreach (var v in vals) if (!Unv.AllZero(v)) w.Write(v);
            }
        }
        if (c.Looped) w.Write((byte)1);
        if (c.LoopKeyOffset != 0) w.Write(c.LoopKeyOffset);
    }

    public static SplineCurves ReadSplineCurves(byte[] d, ref int p)
    {
        var sc = new SplineCurves();
        foreach (var s in Unv.ReadHeader(d, ref p))
        {
            sc.PresentSlots.Add(s.Index);
            if (s.Zero) continue;
            switch (s.Index)
            {
                case 0: sc.Position = ReadCurve(d, ref p, 'v'); break;
                case 1: sc.Rotation = ReadCurve(d, ref p, 'q'); break;
                case 2: sc.Scale = ReadCurve(d, ref p, 'v'); break;
                case 3: sc.Reparam = ReadCurve(d, ref p, 'f'); break;
                case 4: sc.Version = BitConverter.ToUInt32(d, p); p += 4; break;
                default: throw new InvalidDataException($"FSplineCurves property {s.Index}");
            }
        }
        return sc;
    }

    public static void WriteSplineCurves(BinaryWriter w, SplineCurves sc)
    {
        var curves = new[] { sc.Position, sc.Rotation, sc.Scale, sc.Reparam };
        var slots = sc.PresentSlots.Select(i => new Unv.Slot(i, i < 4 ? curves[i].IsZero : sc.Version == 0)).ToList();
        Unv.WriteHeader(w, slots);
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            if (s.Index < 4) WriteCurve(w, curves[s.Index]);
            else w.Write(sc.Version);
        }
    }

    // ---- DMSplineMetadata export: 6 float curves + 2 enums, then the object GUID flag ----

    public sealed class Metadata
    {
        public Curve?[] Curves = new Curve?[6];
        public byte[] Enums = new byte[2];
        public List<int> PresentSlots = new();
        public byte[] Tail = [];
    }

    public static Metadata ReadMetadata(byte[] d)
    {
        var m = new Metadata();
        int p = 0;
        foreach (var s in Unv.ReadHeader(d, ref p))
        {
            m.PresentSlots.Add(s.Index);
            if (s.Zero) continue;
            if (s.Index < 6) m.Curves[s.Index] = ReadCurve(d, ref p, 'f');
            else if (s.Index < 8) m.Enums[s.Index - 6] = d[p++];
            else throw new InvalidDataException($"DMSplineMetadata property {s.Index}");
        }
        m.Tail = d.AsSpan(p).ToArray();
        return m;
    }

    public static byte[] WriteMetadata(Metadata m)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var slots = m.PresentSlots.Select(i => new Unv.Slot(i, i < 6 ? m.Curves[i] is null || m.Curves[i]!.IsZero : m.Enums[i - 6] == 0)).ToList();
        Unv.WriteHeader(w, slots);
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            if (s.Index < 6) WriteCurve(w, m.Curves[s.Index]!);
            else w.Write(m.Enums[s.Index - 6]);
        }
        w.Write(m.Tail);
        return ms.ToArray();
    }

    // ---- DMSplineComponent export: [MetaData obj ref] SplineCurves [rest verbatim] ----

    public sealed class ComponentSplit
    {
        public byte[] Prefix = [];     // header + values before SplineCurves
        public SplineCurves Curves = new();
        public byte[] Suffix = [];     // values after SplineCurves + object GUID flag
    }

    public const int SlotSplineCurves = 9, SlotMetaData = 7;

    public static bool HasSplineCurves(byte[] d)
    {
        int p = 0;
        return Unv.ReadHeader(d, ref p).Any(s => s.Index == SlotSplineCurves && !s.Zero);
    }

    public static ComponentSplit SplitComponent(byte[] d)
    {
        int p = 0;
        var slots = Unv.ReadHeader(d, ref p);
        foreach (var s in slots)
        {
            if (s.Index >= SlotSplineCurves) break;
            if (s.Zero) continue;
            if (s.Index == SlotMetaData) p += 4;
            else throw new NotSupportedException($"DMSplineComponent property {s.Index} before SplineCurves");
        }
        if (!slots.Any(s => s.Index == SlotSplineCurves && !s.Zero)) throw new InvalidDataException("component has no SplineCurves");
        var split = new ComponentSplit { Prefix = d.AsSpan(0, p).ToArray() };
        split.Curves = ReadSplineCurves(d, ref p);
        split.Suffix = d.AsSpan(p).ToArray();
        return split;
    }

    public static byte[] JoinComponent(ComponentSplit s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(s.Prefix);
        WriteSplineCurves(w, s.Curves);
        w.Write(s.Suffix);
        return ms.ToArray();
    }

    // ---- spline math ----

    public readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double[] ToArray() => [X, Y, Z];
        public static V3 From(double[] a) => new(a[0], a[1], a[2]);
    }

    static readonly (double arg, double weight)[] LegendreGauss =
    [
        (0.0, 0.5688889), (-0.5384693, 0.47862867), (0.5384693, 0.47862867), (-0.90617985, 0.23692688), (0.90617985, 0.23692688),
    ];

    public static double SegmentLength(V3 p0, V3 t0, V3 p1, V3 t1, double param)
    {
        var c1 = ((p0 - p1) * 2.0 + t0 + t1) * 3.0;
        var c2 = (p1 - p0) * 6.0 - t0 * 4.0 - t1 * 2.0;
        var c3 = t0;
        double half = param * 0.5, len = 0;
        foreach (var (arg, weight) in LegendreGauss)
        {
            double a = half * (1.0 + arg);
            len += ((c1 * a + c2) * a + c3).Length * weight;
        }
        return len * half;
    }

    /// Builds FSplineCurves for points in component space (UE cm), with auto tangents, identity rotations,
    /// unit scale and a reparam table of `steps` samples per segment, the way USplineComponent::UpdateSpline does.
    public static SplineCurves Build(IReadOnlyList<V3> pts, bool looped, SplineCurves template, int steps = 10)
    {
        int n = pts.Count;
        V3 Tangent(int i)
        {
            if (looped) return (pts[(i + 1) % n] - pts[(i - 1 + n) % n]) * 0.5;
            if (i == 0) return pts[1] - pts[0];
            if (i == n - 1) return pts[n - 1] - pts[n - 2];
            return (pts[i + 1] - pts[i - 1]) * 0.5;
        }
        var sc = new SplineCurves { Version = template.Version, PresentSlots = template.PresentSlots.ToList() };
        sc.Position = new Curve { Kind = 'v', Looped = looped, LoopKeyOffset = looped ? 1 : 0 };
        sc.Rotation = new Curve { Kind = 'q', Looped = looped, LoopKeyOffset = looped ? 1 : 0 };
        sc.Scale = new Curve { Kind = 'v', Looped = looped, LoopKeyOffset = looped ? 1 : 0 };
        double[] identity = [0, 0, 0, 1], halfIdentity = [0, 0, 0, 0.5];
        for (int i = 0; i < n; i++)
        {
            var t = Tangent(i).ToArray();
            sc.Position.Points.Add(new Point { InVal = i, Out = pts[i].ToArray(), Arrive = t, Leave = t, Mode = CIM_CurveAuto });
            var qt = !looped && (i == 0 || i == n - 1) ? identity : halfIdentity;
            sc.Rotation.Points.Add(new Point { InVal = i, Out = identity, Arrive = qt, Leave = qt, Mode = CIM_CurveAuto });
            sc.Scale.Points.Add(new Point { InVal = i, Out = [1, 1, 1], Arrive = [0, 0, 0], Leave = [0, 0, 0], Mode = CIM_CurveAuto });
        }
        sc.Reparam = BuildReparam(sc.Position, looped, steps);
        return sc;
    }

    public static Curve BuildReparam(Curve position, bool looped, int steps)
    {
        var pts = position.Points;
        int segments = looped ? pts.Count : pts.Count - 1;
        var r = new Curve { Kind = 'f' };
        double acc = 0;
        for (int s = 0; s < segments; s++)
        {
            var a = pts[s]; var b = pts[(s + 1) % pts.Count];
            V3 p0 = V3.From(a.Out), t0 = V3.From(a.Leave), p1 = V3.From(b.Out), t1 = V3.From(b.Arrive);
            for (int k = 0; k < steps; k++)
            {
                float param = (float)k / steps;
                double len = k == 0 ? 0 : SegmentLength(p0, t0, p1, t1, param);
                r.Points.Add(new Point { InVal = (float)(acc + len), Out = [s + param], Arrive = [0], Leave = [0], Mode = CIM_Linear });
            }
            acc += SegmentLength(p0, t0, p1, t1, 1.0);
        }
        r.Points.Add(new Point { InVal = (float)acc, Out = [segments], Arrive = [0], Leave = [0], Mode = CIM_Linear });
        return r;
    }

    public static Curve ConstantFloatCurve(int n, Curve template)
    {
        var first = template.Points.Count > 0 ? template.Points[0] : new Point { Out = [0], Arrive = [0], Leave = [0] };
        var c = new Curve { Kind = 'f', Looped = template.Looped, LoopKeyOffset = template.LoopKeyOffset };
        for (int i = 0; i < n; i++)
            c.Points.Add(new Point { InVal = i, Out = [first.Out[0]], Arrive = [0], Leave = [0], Mode = first.Mode });
        return c;
    }
}
