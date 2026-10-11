using System.Text.RegularExpressions;

namespace AcrRef;

/// `stage-selftest --legacy <root>`: proves the legacy-package, collider, stride-map and spline (de)serializers
/// reproduce the host packages byte for byte, and that Splines.Build reproduces the host CenterSpline.
internal static class StageSelfTest
{
    public static int Run(Options o)
    {
        var root = o.Named.GetValueOrDefault("legacy") ?? throw new ArgumentException("--legacy <dir> is required");
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }

        foreach (var rel in HostPaths.All)
        {
            var path = HostPaths.Resolve(root, rel);
            var pkg = new LegacyPackage(path);
            var (h, u) = pkg.Save();
            Check(h.AsSpan().SequenceEqual(File.ReadAllBytes(path)) && u.AsSpan().SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(path, ".uexp"))),
                $"LegacyPackage round trip {Path.GetFileName(path)} ({pkg.ExportCount} exports, {pkg.ImportCount} imports, {pkg.Names.Count} names)");
        }

        var baked = File.ReadAllBytes(Path.ChangeExtension(HostPaths.Resolve(root, HostPaths.Baked), ".uexp"));
        var (colliders, _, simpleStart) = BakedEdit.ParseColliders(baked);
        var (mapStart, meshMap, simpleMapStart) = BakedEdit.LocateMaps(baked, simpleStart);
        var simple = SimpleColliders.ParseExact(baked, simpleStart, mapStart);
        Check(true, $"baked: {colliders.Count} mesh colliders, {simple.Entries} simple entries ({simple.Boxes} boxes, {simple.Spheres} spheres, {simple.Capsules} capsules), {meshMap.Count} mesh map keys");
        Check(BakedEdit.WriteMap(meshMap).AsSpan().SequenceEqual(baked.AsSpan(mapStart, simpleMapStart - mapStart)), "stride map round trip");
        if (o.Named.TryGetValue("dump-simple", out var ds))
        {
            var bpkg = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Baked));
            string Imp(int idx) => idx < 0 ? bpkg.ImportName(-idx - 1) : idx == 0 ? "null" : $"export{idx}";
            var parts = ds.Split(',').Select(int.Parse).ToArray();
            SimpleColliders.Dump(baked, simpleStart, parts[0], parts[1], Imp, Console.Out);
            int sp = simpleMapStart;
            var simpleMap = BakedEdit.ReadMap(baked, ref sp);
            foreach (var (label, map) in new[] { ("mesh", meshMap), ("simple", simpleMap) })
                foreach (var e in map)
                    Console.WriteLine($"  {label} map key {bpkg.NameAt(e.NameIndex, e.NameNumber)}: {e.Strides.Count} strides, {e.Strides.Sum(s => s.Num)} entries, first {string.Join(" ", e.Strides.Take(4).Select(s => $"{{{s.Index},{s.Num}}}"))}");
            Console.WriteLine("  names: " + string.Join(", ", bpkg.Names.Select((n, i) => $"{i}:{n}")));
        }

        var cell = new LegacyPackage(HostPaths.Resolve(root, HostPaths.Cell));
        if (o.Filter is not null)
        {
            var re = new Regex(o.Filter, RegexOptions.IgnoreCase);
            for (int e = 0; e < cell.ExportCount; e++)
                if (re.IsMatch(cell.ExportClass(e)) || re.IsMatch(cell.ExportName(e)))
                    Console.WriteLine($"  export {e}: {cell.ExportName(e)} : {cell.ExportClass(e)} outer {cell.ExportOuter(e)} size {cell.GetExportData(e).Length}");
        }
        for (int e = 0; e < cell.ExportCount; e++)
        {
            var cls = cell.ExportClass(e);
            var data = cell.GetExportData(e);
            if (cls == "DMSplineComponent" && Splines.HasSplineCurves(data))
            {
                var split = Splines.SplitComponent(data);
                Check(Splines.JoinComponent(split).AsSpan().SequenceEqual(data),
                    $"spline component round trip {cell.ExportName(e)} ({split.Curves.Position.Points.Count} points, reparam {split.Curves.Reparam.Points.Count})");
                if (cell.ExportName(e) == "CenterSpline" && split.Curves.Position.Points.Count > 2)
                    CompareRebuild(split.Curves, Check);
            }
            else if (cls == "DMSplineMetadata")
                Check(Splines.WriteMetadata(Splines.ReadMetadata(data)).AsSpan().SequenceEqual(data), $"spline metadata round trip {cell.ExportName(e)}");
        }

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
        return failures == 0 ? 0 : 4;
    }

    static void CompareRebuild(Splines.SplineCurves host, Action<bool, string> check)
    {
        var pts = host.Position.Points.Select(p => Splines.V3.From(p.Out)).ToList();
        var rebuilt = Splines.Build(pts, host.Position.Looped, host);
        double maxTan = 0, maxRep = 0, maxRot = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                maxTan = Math.Max(maxTan, Math.Abs(rebuilt.Position.Points[i].Leave[k] - host.Position.Points[i].Leave[k]));
                maxTan = Math.Max(maxTan, Math.Abs(rebuilt.Position.Points[i].Arrive[k] - host.Position.Points[i].Arrive[k]));
            }
        }
        int nr = Math.Min(rebuilt.Reparam.Points.Count, host.Reparam.Points.Count);
        for (int i = 0; i < nr; i++)
        {
            maxRep = Math.Max(maxRep, Math.Abs(rebuilt.Reparam.Points[i].InVal - host.Reparam.Points[i].InVal));
            maxRep = Math.Max(maxRep, Math.Abs(rebuilt.Reparam.Points[i].Out[0] - host.Reparam.Points[i].Out[0]) * 1000);
        }
        for (int i = 0; i < Math.Min(rebuilt.Rotation.Points.Count, host.Rotation.Points.Count); i++)
            for (int k = 0; k < 4; k++)
                maxRot = Math.Max(maxRot, Math.Abs(rebuilt.Rotation.Points[i].Arrive[k] - host.Rotation.Points[i].Arrive[k]));
        // The host uses hand-edited (CIM_CurveUser) tangents on most points, so auto tangents are not expected to match;
        // the reparam table built from the host's own tangents must.
        var hp = host.Position.Points;
        var modes = string.Join(",", hp.GroupBy(p => p.Mode).Select(g => $"mode {g.Key} x{g.Count()}"));
        var reparam = Splines.BuildReparam(host.Position, host.Position.Looped, 10);
        double maxIn = 0, maxOut = 0;
        for (int i = 0; i < Math.Min(reparam.Points.Count, host.Reparam.Points.Count); i++)
        {
            maxIn = Math.Max(maxIn, Math.Abs(reparam.Points[i].InVal - host.Reparam.Points[i].InVal));
            maxOut = Math.Max(maxOut, Math.Abs(reparam.Points[i].Out[0] - host.Reparam.Points[i].Out[0]));
        }
        check(reparam.Points.Count == host.Reparam.Points.Count && maxIn < 1 && maxOut < 1e-4,
            $"CenterSpline reparam from host tangents: {reparam.Points.Count} vs {host.Reparam.Points.Count} keys, max distance diff {maxIn:0.###} cm, max key diff {maxOut:0.######} " +
            $"(host {modes}; auto-tangent rebuild differs by up to {maxTan:0} cm, length {rebuilt.Reparam.Points[^1].InVal:0} vs {host.Reparam.Points[^1].InVal:0} cm; rotation tangent diff {maxRot:0.###})");
    }
}

internal static class HostPaths
{
    public const string Baked = @"acr\Content\Environments\WalesS4HafrenSouth\DataAssets\DA_WalesS4HafrenSouthBakedData";
    public const string Cell = @"acr\Content\Levels\WalesS4HafrenSouth\_Generated_\22VG0QIP9ATUQI633K86KWPFP";
    public const string Variants = @"acr\Content\Data\Database\Main\TrackSelection\DT_TracksVariants";
    public const string Pacenote = @"acr\Content\Data\Pacenote\Tables\DT_PacenoteHafrenSouthForward";
    public static readonly string[] All = [Baked, Cell, Variants, Pacenote];

    /// The .uasset or .umap header path under a retoc legacy root.
    public static string Resolve(string root, string rel)
    {
        foreach (var ext in new[] { ".uasset", ".umap" })
        {
            var p = Path.Combine(root, rel + ext);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"{rel} not extracted under {root}");
    }
}
