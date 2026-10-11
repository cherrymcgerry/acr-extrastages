using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace AcrRef;

/// Menu / HUD names of the forward variant: source strings of ST_Track entries (config/track-names.toml [ST_Track]).
/// DT_TracksVariants' forward row names the stage by ST_Track keys (Name = ..._SHORT, StageName); both keys belong to
/// this variant only. Game.locres also has them; UE shows a localized string only while its stored source-string hash
/// matches the table's source, so a changed source string is shown as is in every language [I].
/// UStringTable export: empty property header (2 bytes), int32 0, FString namespace, int32 n,
/// n x (FString key, FString source), metadata map (copied).
internal static class TrackNames
{
    public const string Table = @"acr\Content\Data\Localization\ContentTexts\ST_Track";

    static string ReadStr(byte[] d, ref int p)
    {
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (n == 0) return "";
        string s = n > 0 ? Encoding.Latin1.GetString(d, p, n - 1) : Encoding.Unicode.GetString(d, p, -n * 2 - 2);
        p += n > 0 ? n : -n * 2;
        return s;
    }

    static void WriteStr(BinaryWriter w, string s)
    {
        if (s.All(c => c < 128)) { w.Write(s.Length + 1); w.Write(Encoding.ASCII.GetBytes(s)); w.Write((byte)0); }
        else { w.Write(-(s.Length + 1)); w.Write(Encoding.Unicode.GetBytes(s)); w.Write((short)0); }
    }

    sealed record Parsed(int Prefix, string Namespace, List<(string key, string value)> Entries, int MetaAt);

    static Parsed Parse(byte[] d)
    {
        int p = 0;
        Unv.ReadHeader(d, ref p);
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)) != 0) throw new InvalidDataException("ST: expected int32 0 after the property header");
        p += 4;
        int prefix = p;
        var ns = ReadStr(d, ref p);
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        var entries = new List<(string, string)>();
        for (int i = 0; i < n; i++) { var k = ReadStr(d, ref p); entries.Add((k, ReadStr(d, ref p))); }
        return new(prefix, ns, entries, p);
    }

    public static JsonObject Build(string legacy, string outRoot, Toml cfg)
    {
        var path = HostPaths.Resolve(legacy, Table);
        var pkg = new LegacyPackage(path);
        var d = pkg.GetExportData(0);
        var st = Parse(d);
        var want = cfg["ST_Track"];
        var r = new JsonObject();
        foreach (var k in want.Keys)
            if (!st.Entries.Any(e => e.key == k)) throw new KeyNotFoundException($"ST_Track has no key {k}");
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(d, 0, st.Prefix);
        WriteStr(w, st.Namespace);
        w.Write(st.Entries.Count);
        foreach (var (k, v) in st.Entries)
        {
            WriteStr(w, k);
            if (want.TryGetValue(k, out var nv) && nv is string s) { WriteStr(w, s); r[k] = $"{v} -> {s}"; }
            else WriteStr(w, v);
        }
        w.Write(d, st.MetaAt, d.Length - st.MetaAt);
        pkg.SetExportData(0, ms.ToArray());
        pkg.SaveTo(Path.Combine(outRoot, Table + Path.GetExtension(path)));
        Console.WriteLine($"names: {string.Join("; ", r.Select(kv => $"{kv.Key}: {kv.Value}"))}");
        return r;
    }

    public static int Check(string root, Toml cfg, TextWriter w)
    {
        var st = Parse(new LegacyPackage(HostPaths.Resolve(root, Table)).GetExportData(0));
        int fail = 0;
        foreach (var (k, v) in cfg["ST_Track"])
        {
            var got = st.Entries.FirstOrDefault(e => e.key == k).value;
            bool ok = got == (string)v;
            w.WriteLine($"{(ok ? "PASS" : "FAIL")} ST_Track {k} = \"{got}\"");
            if (!ok) fail++;
        }
        w.WriteLine($"{(st.Entries.Count == 127 ? "PASS" : "FAIL")} ST_Track keeps all {st.Entries.Count} entries");
        return fail + (st.Entries.Count == 127 ? 0 : 1);
    }
}
