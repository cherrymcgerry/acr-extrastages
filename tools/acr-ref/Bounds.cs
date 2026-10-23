using System.Text.Json.Nodes;

namespace AcrRef;

/// Enlarges the clone's PhysicsWorldBounds (the brush volume that BC_PhysicsAvatar_C reads as the dmphysics world box) in the
/// persistent level, so a DR2 route larger than Hafren South fits (`stage-build --bounds 1`, docs/decisions.md D8).
///
/// The volume is an ABrush: BrushComponent0 holds its RelativeLocation (3 doubles, UE cm), and its box is stored as +-half extents in
/// three places - BodySetup_0 (convex vertices / ElemBox as doubles, cooked convex data as floats) and Model_1 (BSP vertices, floats).
/// Every stored copy of +-hx / +-hy / +-hz is replaced by value, and the location moves to the new box centre.
internal static class Bounds
{
    public const double DefaultMarginM = 400;
    // host box: centre and half extents in UE cm, as stored (floats; the doubles are the exact floats)
    const float HostHx = 142493.75f, HostHy = 150590.53f, HostHz = 11444.36f;
    const float HostLx = -62302.863f, HostLy = 54206.09f, HostLz = 41972.54f;
    const string GameplayCell = "22VG0QIP9ATUQI633K86KWPFP";

    sealed record Half(double Hx, double Hy, double Hz, double Lx, double Ly, double Lz);

    static Half Target(StageBuild.World w) => new((w.MaxX - w.MinX) / 2 * 100, (w.MaxZ - w.MinZ) / 2 * 100, (w.MaxY - w.MinY) / 2 * 100,
        (w.MaxX + w.MinX) / 2 * 100, (w.MaxZ + w.MinZ) / 2 * 100, (w.MaxY + w.MinY) / 2 * 100);

    static List<int> Parts(LegacyPackage pkg, out int actor)
    {
        int a = Enumerable.Range(0, pkg.ExportCount).Single(e => pkg.ExportClass(e) == "PhysicsWorldBounds");
        actor = a;
        return Enumerable.Range(0, pkg.ExportCount).Where(e => pkg.ExportOuter(e) == a + 1 || pkg.ExportOuter(e) > 0 && pkg.ExportOuter(pkg.ExportOuter(e) - 1) == a + 1).ToList();
    }

    /// Replacement table: every f32 / f64 copy of the host's +-extent values -> the new ones.
    static (byte[] from, byte[] to)[] Table(Half n)
    {
        var l = new List<(byte[], byte[])>();
        foreach (var (h, v) in new[] { (HostHx, n.Hx), (HostHy, n.Hy), (HostHz, n.Hz) })
            foreach (int sign in new[] { 1, -1 })
            {
                l.Add((BitConverter.GetBytes(h * sign), BitConverter.GetBytes((float)(v * sign))));
                l.Add((BitConverter.GetBytes((double)h * sign), BitConverter.GetBytes(v * sign)));
            }
        return l.ToArray();
    }

    static List<(int at, int len, byte[] to)> Matches(byte[] d, (byte[] from, byte[] to)[] table)
    {
        var hits = new List<(int at, int len, byte[] to)>();
        foreach (var (from, to) in table)
            for (int off = 0; ;)
            {
                int i = d.AsSpan(off).IndexOf(from);
                if (i < 0) break;
                hits.Add((off + i, from.Length, to));
                off += i + 1;
            }
        var sorted = hits.OrderBy(h => h.at).ToList();
        for (int k = 1; k < sorted.Count; k++)
            if (sorted[k].at < sorted[k - 1].at + sorted[k - 1].len) throw new InvalidDataException("overlapping extent matches in the bounds volume");
        return sorted;
    }
    static int FindLocation(byte[] d, double lx, double ly, double lz)
    {
        bool Eq(int at, double v) => (float)BitConverter.ToDouble(d, at) == (float)v;
        for (int i = 0; i + 24 <= d.Length; i++)
            if (Eq(i, HostLx) && Eq(i + 8, HostLy) && Eq(i + 16, HostLz)) return i;
        return -1;
    }

    public static JsonObject Build(string legacy, string outRoot, StageBuild.World world, Options o)
    {
        var dest = Path.Combine(outRoot, Scenery.Persistent + ".umap");
        var src = File.Exists(dest) ? dest : HostPaths.Resolve(legacy, Scenery.Persistent);
        var pkg = new LegacyPackage(src);
        var r = new JsonObject { ["world"] = world.ToJson(), ["enlarged"] = !world.IsHost };
        var n = Target(world);

        // gameplay cell: the host's forward cell is not spatially loaded (docs/decisions.md D6, verified in game), so it is resident
        // for the whole variant; only force bIsAlwaysLoaded if a build of the game ever makes it spatial.
        for (int e = 0; e < pkg.ExportCount; e++)
        {
            if (pkg.ExportClass(e) != Scenery.CellExportClass || pkg.ExportName(e) != GameplayCell) continue;
            var d = pkg.GetExportData(e);
            var (always, desc) = Scenery.CellState(pkg, d);
            bool spatial = Scenery.SpatiallyLoaded(d);
            r["gameplay_cell"] = $"{desc}; alwaysLoaded {always}, spatiallyLoaded {spatial}";
            if (spatial && !always) { pkg.SetExportData(e, Scenery.WithAlwaysLoaded(d)); r["gameplay_cell_forced_always_loaded"] = true; }
        }

        if (!world.IsHost)
        {
            var parts = Parts(pkg, out _);
            var table = Table(n);
            var counts = new JsonObject();
            foreach (int e in parts)
            {
                var cls = pkg.ExportClass(e);
                var d = pkg.GetExportData(e);
                if (cls == "BrushComponent")
                {
                    int at = FindLocation(d, HostLx, HostLy, HostLz);
                    if (at < 0) throw new InvalidDataException("PhysicsWorldBounds brush component: RelativeLocation not found");
                    var nd = (byte[])d.Clone();
                    BitConverter.GetBytes(n.Lx).CopyTo(nd, at); BitConverter.GetBytes(n.Ly).CopyTo(nd, at + 8); BitConverter.GetBytes(n.Lz).CopyTo(nd, at + 16);
                    pkg.SetExportData(e, nd);
                    counts[cls + " location"] = 1;
                    continue;
                }
                var hits = Matches(d, table);
                if (hits.Count == 0) continue;
                var data = (byte[])d.Clone();
                foreach (var (at, len, to) in hits) to.CopyTo(data, at);
                pkg.SetExportData(e, data);
                counts[pkg.ExportName(e)] = hits.Count;
            }
            if (counts.Count < 3) throw new InvalidDataException($"PhysicsWorldBounds: expected the location, BodySetup and Model to be patched, got {counts.ToJsonString()}");
            r["patched"] = counts;
            Console.WriteLine($"bounds: PhysicsWorldBounds -> X {world.MinX:0}..{world.MaxX:0}, Z {world.MinZ:0}..{world.MaxZ:0}, Y {world.MinY:0}..{world.MaxY:0} m ({counts.ToJsonString()})");
        }
        else Console.WriteLine("bounds: host PhysicsWorldBounds kept");

        if (src != dest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            foreach (var ext in new[] { ".ubulk", ".uptnl" })
                if (File.Exists(Path.ChangeExtension(src, ext))) File.Copy(Path.ChangeExtension(src, ext), Path.ChangeExtension(dest, ext), true);
        }
        pkg.SaveTo(dest);
        return r;
    }

    /// Re-reads the persistent level: the new extents / location must be present and no host copy may be left (when enlarged).
    public static int Check(string root, StageBuild.World world, JsonNode report, TextWriter w)
    {
        int fail = 0;
        void C(bool ok, string msg) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {msg}"); if (!ok) fail++; }
        var pkg = new LegacyPackage(HostPaths.Resolve(root, Scenery.Persistent));
        var parts = Parts(pkg, out _);
        if (world.IsHost)
        {
            C(parts.Count > 0, "bounds: host PhysicsWorldBounds kept");
            return fail;
        }
        var n = Target(world);
        int newCopies = 0, hostCopies = 0; bool located = false;
        foreach (int e in parts)
        {
            var d = pkg.GetExportData(e);
            if (pkg.ExportClass(e) == "BrushComponent")
            {
                bool Eq(int a, double v) => (float)BitConverter.ToDouble(d, a) == (float)v;
                for (int i = 0; i + 24 <= d.Length && !located; i++) located = Eq(i, n.Lx) && Eq(i + 8, n.Ly) && Eq(i + 16, n.Lz);
                continue;
            }
            hostCopies += Matches(d, Table(n).Where(t => !t.from.AsSpan().SequenceEqual(t.to)).ToArray()).Count;
            newCopies += Matches(d, NewTable(n)).Count;
        }
        C(located, $"bounds: brush location at the new centre ({n.Lx / 100:0.#}, {n.Ly / 100:0.#}, {n.Lz / 100:0.#}) m");
        int expected = report["patched"]?.AsObject().Where(kv => kv.Key != "BrushComponent location").Sum(kv => kv.Value!.GetValue<int>()) ?? 0;
        C(newCopies == expected && newCopies > 0, $"bounds: {newCopies} copies of the new half extents ({n.Hx / 100:0.#} x {n.Hz / 100:0.#} x {n.Hy / 100:0.#} m) in BodySetup / Model (build: {expected})");
        C(hostCopies == 0, $"bounds: {hostCopies} copies of the host half extents left");
        return fail;
    }

    static (byte[] from, byte[] to)[] NewTable(Half n)
    {
        var l = new List<(byte[], byte[])>();
        foreach (var v in new[] { n.Hx, n.Hy, n.Hz })
            foreach (int sign in new[] { 1, -1 })
            {
                l.Add((BitConverter.GetBytes((float)(v * sign)), []));
                l.Add((BitConverter.GetBytes(v * sign), []));
            }
        return l.ToArray();
    }

    /// bounds-probe <persistent .umap>: where the box is stored (used to write Build).
    public static int Probe(Options o)
    {
        var pkg = new LegacyPackage(o.Positional[0]);
        foreach (var e in Parts(pkg, out _))
        {
            var d = pkg.GetExportData(e);
            Console.WriteLine($"#{e} {pkg.ExportName(e)} ({pkg.ExportClass(e)}) {d.Length} bytes; extent copies {Matches(d, Table(new Half(1, 2, 3, 0, 0, 0))).Count}; location at {(pkg.ExportClass(e) == "BrushComponent" ? FindLocation(d, HostLx, HostLy, HostLz) : -1)}");
        }
        for (int e = 0; e < pkg.ExportCount; e++)
            if (pkg.ExportClass(e) == Scenery.CellExportClass && pkg.ExportName(e) == GameplayCell)
            {
                var d = pkg.GetExportData(e);
                Console.WriteLine($"gameplay cell {Scenery.CellState(pkg, d).describe}; always {Scenery.CellState(pkg, d).alwaysLoaded}; spatial {Scenery.SpatiallyLoaded(d)}");
            }
        return 0;
    }
}
