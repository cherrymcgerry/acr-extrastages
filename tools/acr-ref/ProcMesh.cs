using System.Numerics;

namespace AcrRef;

/// Writer for UProceduralMeshComponent exports in a legacy (unversioned) cooked level: ProcMeshSections is a plain
/// UPROPERTY, so visible geometry can be authored as data. Layout (usmap, UE 5.6):
///   component slots: 0 bUseComplexAsSimpleCollision, 2 ProcMeshBodySetup, 3 ProcMeshSections,
///                    5 LocalBounds (unversioned struct: 0 Origin, 1 BoxExtent, 2 SphereRadius f64),
///                    7 OverrideMaterials, 124 AttachParent, 133-135 bAbsolute{Location,Rotation,Scale}, 136 bVisible,
///                    142 bHiddenInGame
///   FProcMeshSection: 0 ProcVertexBuffer, 1 ProcIndexBuffer u32[], 2 SectionLocalBox (FBox, native), 3 bEnableCollision,
///                     4 bSectionVisible
///   FProcMeshVertex:  0 Position, 1 Normal (FVector, native 3 x f64), 2 Tangent {0 TangentX, 1 bFlipTangentY},
///                     3 Color (FColor BGRA), 4..7 UV0..UV3 (FVector2D, 2 x f64)
/// Unversioned bools: true = one byte 1, false = zero-mask bit.
internal static class ProcMesh
{
    internal sealed class Section
    {
        public List<Vector3> Pos = new(), Nrm = new(), Tan = new();   // Tan optional (derived from the normal if empty)
        public List<Vector2> Uv = new();
        public List<Vector2> Uv1 = new(), Uv2 = new(), Uv3 = new();  // optional: written when filled for every vertex
        public List<uint> Idx = new();
        public uint Color = 0xFFFFFFFF;            // BGRA bytes as written
        public bool Collision;                     // keep false: the car uses Kunos BakedData, not UE collision
    }

    internal sealed record Component(int Export, Dictionary<int, (int at, int end, bool zero)> Slots, byte[] Data, int PropsEnd);

    // ---- parsing (enough to locate the slots and to validate the section format on the host's own data) ----

    public static Component Parse(byte[] d, int export)
    {
        int p = 0;
        var hdr = Unv.ReadHeader(d, ref p);
        var slots = new Dictionary<int, (int, int, bool)>();
        foreach (var s in hdr)
        {
            int at = p;
            if (!s.Zero) p = Skip(d, p, s.Index);
            slots[s.Index] = (at, p, s.Zero);
        }
        return new Component(export, slots, d, p);
    }

    static int Skip(byte[] d, int p, int slot) => slot switch
    {
        0 or 1 or 133 or 134 or 135 or 136 or 142 or 144 => p + 1,           // bools (true)
        2 or 8 or 124 => p + 4,                                              // object refs
        3 => SkipSections(d, p),
        5 => SkipBounds(d, p),                                               // FBoxSphereBounds (unversioned struct)
        7 => p + 4 + 4 * BitConverter.ToInt32(d, p),
        128 or 130 => p + 24,
        129 => p + 24,
        _ => throw new NotSupportedException($"ProceduralMeshComponent slot {slot} not handled"),
    };

    static int SkipBounds(byte[] d, int p)
    {
        foreach (var s in Unv.ReadHeader(d, ref p)) if (!s.Zero) p += s.Index == 2 ? 8 : 24;
        return p;
    }

    static int SkipSections(byte[] d, int p)
    {
        int n = BitConverter.ToInt32(d, p); p += 4;
        for (int i = 0; i < n; i++) ReadSection(d, ref p, out _);
        return p;
    }

    /// Reads one FProcMeshSection; returns its raw property slots for validation.
    public static Section ReadSection(byte[] d, ref int p, out List<Unv.Slot> slots)
    {
        var s = new Section();
        slots = Unv.ReadHeader(d, ref p);
        foreach (var sl in slots)
        {
            if (sl.Zero) { if (sl.Index == 3) s.Collision = false; continue; }
            switch (sl.Index)
            {
                case 0:
                    int nv = BitConverter.ToInt32(d, p); p += 4;
                    var extra = new[] { s.Uv1, s.Uv2, s.Uv3 };
                    for (int v = 0; v < nv; v++)
                    {
                        var vertex = Unv.ReadHeader(d, ref p);
                        foreach (var (list, slot) in extra.Select((l, k) => (l, k + 5)))
                            if (vertex.Any(f => f.Index == slot && !f.Zero)) while (list.Count < v) list.Add(Vector2.Zero);
                        foreach (var f in vertex)
                        {
                            if (f.Zero) { if (f.Index == 4) s.Uv.Add(Vector2.Zero); continue; }
                            switch (f.Index)
                            {
                                case 0: s.Pos.Add(V3(d, p)); p += 24; break;
                                case 1: s.Nrm.Add(V3(d, p)); p += 24; break;
                                case 2:
                                    foreach (var t in Unv.ReadHeader(d, ref p))
                                    {
                                        if (t.Zero) { if (t.Index == 0) s.Tan.Add(Vector3.Zero); continue; }
                                        if (t.Index == 0) { s.Tan.Add(V3(d, p)); p += 24; }
                                        else throw new InvalidDataException("bFlipTangentY set");
                                    }
                                    break;
                                case 3: s.Color = BitConverter.ToUInt32(d, p); p += 4; break;
                                case 4: s.Uv.Add(new Vector2((float)BitConverter.ToDouble(d, p), (float)BitConverter.ToDouble(d, p + 8))); p += 16; break;
                                case 5 or 6 or 7:
                                    extra[f.Index - 5].Add(new Vector2((float)BitConverter.ToDouble(d, p), (float)BitConverter.ToDouble(d, p + 8))); p += 16; break;
                                default: throw new InvalidDataException($"ProcMeshVertex slot {f.Index}");
                            }
                        }
                        foreach (var list in extra) if (list.Count > 0 && list.Count < v + 1) list.Add(Vector2.Zero);
                    }
                    break;
                case 1:
                    int ni = BitConverter.ToInt32(d, p); p += 4;
                    for (int k = 0; k < ni; k++) s.Idx.Add(BitConverter.ToUInt32(d, p + 4 * k));
                    p += 4 * ni;
                    break;
                case 2: p += 49; break;
                case 3: s.Collision = d[p] != 0; p += 1; break;
                case 4: p += 1; break;
                default: throw new InvalidDataException($"ProcMeshSection slot {sl.Index}");
            }
        }
        return s;
    }

    static Vector3 V3(byte[] d, int p) => new((float)BitConverter.ToDouble(d, p), (float)BitConverter.ToDouble(d, p + 8), (float)BitConverter.ToDouble(d, p + 16));

    // ---- writing ----

    public static void WriteSection(BinaryWriter w, Section s)
    {
        if (s.Pos.Count != s.Nrm.Count || s.Pos.Count != s.Uv.Count) throw new ArgumentException("vertex attribute counts differ");
        if (new[] { s.Uv1, s.Uv2, s.Uv3 }.Any(l => l.Count != 0 && l.Count != s.Pos.Count)) throw new ArgumentException("UV1..UV3 must be empty or one per vertex");
        Unv.WriteHeader(w, [new(0, s.Pos.Count == 0), new(1, s.Idx.Count == 0), new(2, false), new(3, !s.Collision), new(4, false)]);
        if (s.Pos.Count > 0)
        {
            w.Write(s.Pos.Count);
            for (int i = 0; i < s.Pos.Count; i++)
            {
                var n = s.Nrm[i];
                var tangent = s.Tan.Count == s.Pos.Count ? s.Tan[i]
                    : Vector3.Normalize(Math.Abs(n.Z) < 0.9f ? Vector3.Cross(Vector3.UnitZ, n) : Vector3.Cross(Vector3.UnitX, n));
                bool uvZero = s.Uv[i] == Vector2.Zero;
                Vector2 Extra(List<Vector2> l) => l.Count == s.Pos.Count ? l[i] : Vector2.Zero;
                Vector2 u1 = Extra(s.Uv1), u2 = Extra(s.Uv2), u3 = Extra(s.Uv3);
                Unv.WriteHeader(w, [new(0, false), new(1, false), new(2, false), new(3, false), new(4, uvZero),
                                    new(5, u1 == Vector2.Zero), new(6, u2 == Vector2.Zero), new(7, u3 == Vector2.Zero)]);
                W3(w, s.Pos[i]); W3(w, n);
                Unv.WriteHeader(w, [new(0, false), new(1, true)]);
                W3(w, tangent);
                w.Write(s.Color);
                foreach (var uv in new[] { s.Uv[i], u1, u2, u3 })
                    if (uv != Vector2.Zero) { w.Write((double)uv.X); w.Write((double)uv.Y); }
            }
        }
        if (s.Idx.Count > 0) { w.Write(s.Idx.Count); foreach (var i in s.Idx) w.Write(i); }
        var (mn, mx) = Bounds(s.Pos);
        W3(w, mn); W3(w, mx); w.Write((byte)1);
        if (s.Collision) w.Write((byte)1);
        w.Write((byte)1);                                          // bSectionVisible
    }

    static void W3(BinaryWriter w, Vector3 v) { w.Write((double)v.X); w.Write((double)v.Y); w.Write((double)v.Z); }

    public static (Vector3 min, Vector3 max) Bounds(IEnumerable<Vector3> pts)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in pts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        if (mn.X > mx.X) mn = mx = Vector3.Zero;
        return (mn, mx);
    }

    /// Rebuilds the component export: keeps its body setup and attach parent, replaces the sections and bounds,
    /// makes it absolute (vertices are world UE cm), visible in game, with optional per-section material imports.
    public static byte[] BuildComponent(Component c, IReadOnlyList<Section> sections, IReadOnlyList<int>? materials)
    {
        var d = c.Data;
        foreach (var k in c.Slots.Keys)
            if (k is not (2 or 3 or 5 or 7 or 124 or 128 or 129 or 130 or 133 or 134 or 135 or 136 or 142 or 144))
                throw new NotSupportedException($"ProceduralMeshComponent has slot {k}; not handled by the rebuild");
        var slots = new List<Unv.Slot>();
        var body = new List<Action<BinaryWriter>>();
        void Keep(int k) { if (c.Slots.TryGetValue(k, out var s)) { slots.Add(new(k, s.zero)); if (!s.zero) body.Add(w => w.Write(d, s.at, s.end - s.at)); } }

        Keep(2);
        slots.Add(new(3, false)); body.Add(w => { w.Write(sections.Count); foreach (var s in sections) WriteSection(w, s); });
        var (mn, mx) = Bounds(sections.SelectMany(s => s.Pos));
        slots.Add(new(5, false)); body.Add(w =>
        {
            var o = (mn + mx) / 2; var e = (mx - mn) / 2;
            Unv.WriteHeader(w, [new(0, false), new(1, false), new(2, false)]);
            W3(w, o); W3(w, e); w.Write((double)e.Length());
        });
        if (materials is { Count: > 0 }) { slots.Add(new(7, false)); body.Add(w => { w.Write(materials.Count); foreach (var m in materials) w.Write(m); }); }
        Keep(124);
        foreach (var k in new[] { 133, 134, 135, 136 }) { slots.Add(new(k, false)); body.Add(w => w.Write((byte)1)); }
        slots.Add(new(142, true));                                 // bHiddenInGame = false
        if (c.Slots.TryGetValue(144, out _)) Keep(144);
        slots.Sort((a, b) => a.Index.CompareTo(b.Index));

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        Unv.WriteHeader(bw, slots);
        int bi = 0;
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            body[bi++](bw);
        }
        bw.Write(d, c.PropsEnd, d.Length - c.PropsEnd);
        return ms.ToArray();
    }

    /// `procmesh-probe --legacy <root>`: the gameplay cell's procedural mesh components, their slots and a section round trip.
    public static int Probe(Options o)
    {
        var legacy = o.Named.GetValueOrDefault("legacy") ?? throw new ArgumentException("--legacy is required");
        var pkg = new LegacyPackage(HostPaths.Resolve(legacy, HostPaths.Cell));
        int bad = 0;
        for (int e = 0; e < pkg.ExportCount; e++)
        {
            if (pkg.ExportClass(e) != "ProceduralMeshComponent") continue;
            var c = Parse(pkg.GetExportData(e), e);
            var outer = pkg.ExportOuter(e) > 0 ? pkg.ExportName(pkg.ExportOuter(e) - 1) : "?";
            Console.WriteLine($"export {e} {pkg.ExportName(e)} in {outer}: {c.Data.Length} bytes, slots {string.Join(' ', c.Slots.OrderBy(k => k.Key).Select(k => $"{k.Key}{(k.Value.zero ? "z" : "")}"))}, tail {c.Data.Length - c.PropsEnd} bytes");
            var s3 = c.Slots[3];
            Console.WriteLine($"  sections end at {s3.end}; after: {Convert.ToHexString(c.Data, s3.end, Math.Min(80, c.Data.Length - s3.end))}");
            {
                int sp = s3.at + 4;
                var sec = ReadSection(c.Data, ref sp, out _);
                int pos = 0, neg = 0;
                for (int t = 0; t + 2 < sec.Idx.Count; t += 3)
                {
                    var a = sec.Pos[(int)sec.Idx[t]]; var b = sec.Pos[(int)sec.Idx[t + 1]]; var cc = sec.Pos[(int)sec.Idx[t + 2]];
                    if (Vector3.Dot(Vector3.Cross(b - a, cc - a), sec.Nrm[(int)sec.Idx[t]]) > 0) pos++; else neg++;
                }
                Console.WriteLine($"  {sec.Pos.Count} vertices, {sec.Idx.Count / 3} triangles; cross(b-a, c-a) along the normal: {pos}, against: {neg}; collision {sec.Collision}, colour {sec.Color:X8}");
            }
            bool ok = RoundTrips(c, out var why);
            Console.WriteLine($"  sections round trip: {(ok ? "byte-exact" : why)}");
            if (!ok) bad++;
        }
        return bad == 0 ? 0 : 1;
    }

    /// Byte-exact check of the section reader/writer on an existing export.
    public static bool RoundTrips(Component c, out string why)
    {
        var d = c.Data;
        if (!c.Slots.TryGetValue(3, out var s) || s.zero) { why = "no sections"; return false; }
        int p = s.at; int n = BitConverter.ToInt32(d, p); p += 4;
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(n);
        for (int i = 0; i < n; i++) { var sec = ReadSection(d, ref p, out _); WriteSection(w, sec); }
        var a = ms.ToArray(); var b = d.AsSpan(s.at, s.end - s.at).ToArray();
        if (a.AsSpan().SequenceEqual(b)) { why = ""; return true; }
        int k = 0; while (k < Math.Min(a.Length, b.Length) && a[k] == b[k]) k++;
        why = $"differs at byte {k} of {b.Length} (rewrite {a.Length})";
        return false;
    }
}
