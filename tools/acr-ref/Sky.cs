using System.Buffers.Binary;
using System.Text.Json.Nodes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;

namespace AcrRef;

/// Sun position of the placed sky (config/sky.toml): the persistent level's BC_DMSky_Candidate_C actor stores its
/// location and date (Latitude, Longitude, Time Zone as doubles; Year, Month, Day as int32), consecutive slots of
/// BC_DMSky_C. The Blueprint classes are not in the usmap, so the slot layout comes from their ChildProperties
/// (own class first, then supers, like the usmap classes). The values are found as one byte run of the host's
/// values ([host]) and rewritten with [nz]; a zero value goes into the zero mask, a non-zero one is serialized.
internal static class Sky
{
    const string ActorPrefix = "BC_DMSky_Candidate_C_";
    const string ClassPackage = "acr/Content/Data/Environments/Common/BC_DMSky_Candidate.uasset";
    static readonly string[] Fields = ["Latitude", "Longitude", "Time Zone", "Year", "Month", "Day"];
    static bool IsInt(string f) => f is "Year" or "Month" or "Day";

    /// Slot index of each property of the class chain, while the chain stays in Blueprint packages.
    public static Dictionary<string, int> Layout(DefaultFileProvider p, TextWriter? log = null)
    {
        var cls = p.LoadPackage(ClassPackage).GetExports().OfType<UStruct>().First(s => s.Name == "BC_DMSky_Candidate_C");
        var slots = new Dictionary<string, int>();
        int b = 0;
        for (UStruct? cur = cls; cur is not null; )
        {
            int own = 0;
            foreach (var f in cur.ChildProperties ?? [])
            {
                if (f is not CUE4Parse.UE4.Objects.UObject.FProperty prop) continue;
                for (int k = 0; k < Math.Max(1, prop.ArrayDim); k++) slots.TryAdd(prop.Name.Text, b + own++);
            }
            log?.WriteLine($"  {cur.Name}: {own} properties from slot {b}");
            b += own;
            var super = cur.SuperStruct?.Load<UStruct>();
            if (super is null || super.GetPathName().StartsWith("/Script/")) break;
            cur = super;
        }
        return slots;
    }

    static int Actor(LegacyPackage pkg) => Enumerable.Range(0, pkg.ExportCount).Single(e =>
        pkg.ExportName(e).StartsWith(ActorPrefix) && pkg.ExportOuter(e) > 0 && pkg.ExportName(pkg.ExportOuter(e) - 1) == "PersistentLevel");

    static byte[] Run(IEnumerable<(string f, double v)> values)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var (f, v) in values.Where(x => x.v != 0))
            if (IsInt(f)) w.Write((int)v); else w.Write(v);
        return ms.ToArray();
    }

    static List<(string f, double v)> Values(Toml cfg, string section) =>
        Fields.Select(f => (f, cfg.Num(section, f.Replace(" ", "_").ToLowerInvariant()))).ToList();

    /// The run of the six fields in the export: header slots, data start of the values, and the run's offset.
    static (List<Unv.Slot> slots, int valuesAt, int runAt) Locate(byte[] d, Dictionary<string, int> layout, List<(string f, double v)> current)
    {
        int p = 0;
        var slots = Unv.ReadHeader(d, ref p);
        foreach (var (f, v) in current)
        {
            var s = slots.FirstOrDefault(x => x.Index == layout[f]);
            if (s.Index != layout[f] || s.Zero != (v == 0))
                throw new InvalidDataException($"sky actor: slot {layout[f]} ({f}) is {(s.Index != layout[f] ? "not in the header" : s.Zero ? "zero" : "non-zero")}, expected {(v == 0 ? "zero" : "non-zero")} ({v})");
        }
        var run = Run(current);
        var hits = new List<int>();
        for (int i = d.AsSpan(p).IndexOf(run); i >= 0; )
        {
            hits.Add(p + i);
            int next = d.AsSpan(p + i + 1).IndexOf(run);
            i = next < 0 ? -1 : i + 1 + next;
        }
        if (hits.Count != 1) throw new InvalidDataException($"sky actor: the {run.Length}-byte run of {string.Join(", ", current.Select(c => $"{c.f} {c.v}"))} found {hits.Count} times");
        return (slots, p, hits[0]);
    }

    /// Rewrites the sky actor of the persistent level at `src` (the scenery output if present) into outRoot.
    public static JsonObject Build(string legacy, string outRoot, Toml cfg, Options o)
    {
        Dictionary<string, int> layout;
        using (var p = Program.Open(o)) layout = Layout(p);
        var dest = Path.Combine(outRoot, Scenery.Persistent + ".umap");
        var src = File.Exists(dest) ? dest : HostPaths.Resolve(legacy, Scenery.Persistent);
        var pkg = new LegacyPackage(src);
        int e = Actor(pkg);
        var d = pkg.GetExportData(e);
        var host = Values(cfg, "host");
        var want = Values(cfg, "nz");
        var (slots, valuesAt, runAt) = Locate(d, layout, host);
        int runLen = Run(host).Length;
        var targets = Fields.Select(f => layout[f]).ToHashSet();
        var newSlots = slots.Select(s => targets.Contains(s.Index) ? new Unv.Slot(s.Index, want.Single(x => layout[x.f] == s.Index).v == 0) : s).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        Unv.WriteHeader(w, newSlots);
        w.Write(d, valuesAt, runAt - valuesAt);
        w.Write(Run(want));
        w.Write(d, runAt + runLen, d.Length - runAt - runLen);
        pkg.SetExportData(e, ms.ToArray());
        if (src != dest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            foreach (var ext in new[] { ".ubulk", ".uptnl" })
                if (File.Exists(Path.ChangeExtension(src, ext))) File.Copy(Path.ChangeExtension(src, ext), Path.ChangeExtension(dest, ext), true);
        }
        pkg.SaveTo(dest);
        var r = new JsonObject { ["actor"] = pkg.ExportName(e), ["slots"] = $"{layout["Latitude"]}..{layout["Day"]}" };
        foreach (var ((f, hv), (_, nv)) in host.Zip(want)) r[f] = $"{hv} -> {nv}";
        Console.WriteLine($"sky: {pkg.ExportName(e)}: {string.Join(", ", host.Zip(want).Select(x => $"{x.First.f} {x.First.v} -> {x.Second.v}"))}");
        return r;
    }

    public static int Check(string root, Toml cfg, Options o, TextWriter w)
    {
        Dictionary<string, int> layout;
        using (var p = Program.Open(o)) layout = Layout(p);
        var pkg = new LegacyPackage(HostPaths.Resolve(root, Scenery.Persistent));
        var want = Values(cfg, "nz");
        string what = string.Join(", ", want.Select(x => $"{x.f} {x.v}"));
        try
        {
            Locate(pkg.GetExportData(Actor(pkg)), layout, want);
            w.WriteLine($"PASS sky: placed sky actor at {what} (slots {layout["Latitude"]}..{layout["Day"]}, one byte run)");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            w.WriteLine($"FAIL sky: {ex.Message}");
            return 1;
        }
    }

    public static int Probe(Options o)
    {
        using var p = Program.Open(o);
        var l = Layout(p, Console.Out);
        foreach (var f in Fields.Append("Time of Day")) Console.WriteLine($"  {f}: {(l.TryGetValue(f, out var s) ? s : -1)}");
        return 0;
    }
}
