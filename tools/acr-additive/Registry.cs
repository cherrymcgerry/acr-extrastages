using Newtonsoft.Json;

namespace AcrAdditive;

/// One ported stage as the registry sees it (mods/acrdirt-additive/stages/*.json).
internal sealed class StageSpec
{
    public string Id = "";                 // DT_Levels and DT_Tracks row; prefix rows with "AcrDirt" to stay clear of vanilla keys
    public string Level = "";              // package of the stage's own level, e.g. /Game/AcrDirt/Stages/AcrDirtTest/AcrDirtTest
    public LocationSpec Location = new();
    public TrackSpec Track = new();
    public List<VariantSpec> Variants = new();

    public sealed class LocationSpec
    {
        public string Row = "Wales";       // existing DT_TracksLocations row, or a new one when Template is set
        public string? Template;           // new location: copy this row
        public string? Name;               // new location: display name (culture-invariant)
        public string? Description;
        public string? Country;            // new location: DT_Countries row (default: template's)
    }

    public sealed class TrackSpec
    {
        public string Template = "";       // DT_Tracks row to copy (textures, weather, temperature)
        public string Name = "";
    }

    public sealed class VariantSpec
    {
        public string Row = "";
        public string Template = "";       // DT_TracksVariants row to copy (surface, textures, AI cars data, weather config, data layers)
        public string Name = "";
        public string? StageName;
        public float? Length;              // km, menu only (the HUD uses the route spline)
        public List<string>? DataLayers;   // DT_DataLayers rows; default: the template's
    }

    public static StageSpec Load(string path) => JsonConvert.DeserializeObject<StageSpec>(File.ReadAllText(path))
        ?? throw new InvalidDataException(path);
}

/// Builds the merged database tables for a set of stages: vanilla rows untouched, stage rows appended.
internal static class Registry
{
    const string Db = "acr/Content/Data/Database/Main";
    static readonly (string table, string file, string rowStruct)[] Tables =
    [
        ("Levels", "Common/DT_Levels", "DBLevel"),
        ("Tracks", "TrackSelection/DT_Tracks", "DBTrack"),
        ("TracksVariants", "TrackSelection/DT_TracksVariants", "DBTrackVariant"),
        ("TracksLocations", "TrackSelection/DT_TracksLocations", "DBTrackLocation"),
    ];

    public static string TablePath(string root, string file) => Path.Combine(root, Db, file + ".uasset").Replace('/', Path.DirectorySeparatorChar);

    public static int Run(Opts o)
    {
        var legacy = o.Named["legacy"];
        var outRoot = o.Named["out"];
        var specs = o.Positional.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.json").OrderBy(x => x).ToArray() : new[] { p })
            .Select(StageSpec.Load).ToList();
        if (specs.Count == 0) throw new ArgumentException("no stage specs");
        var schema = new Schema(o.Usmap);
        var t = Tables.ToDictionary(x => x.table, x => new DbTable(TablePath(legacy, x.file), schema, x.rowStruct));
        var vanillaKeys = t.ToDictionary(kv => kv.Key, kv => kv.Value.Rows.Select(r => r.Key).ToHashSet());

        foreach (var s in specs) Add(s, t, schema, vanillaKeys);

        foreach (var (table, file, _) in Tables)
        {
            var dest = TablePath(outRoot, file);
            t[table].Save(dest);
            Console.WriteLine($"{table}: {vanillaKeys[table].Count} vanilla + {t[table].Rows.Count - vanillaKeys[table].Count} new rows -> {dest}");
        }
        return 0;
    }

    static void Add(StageSpec s, Dictionary<string, DbTable> t, Schema schema, Dictionary<string, HashSet<string>> vanilla)
    {
        void New(string table, string key)
        {
            if (vanilla[table].Contains(key)) throw new InvalidOperationException($"{s.Id}: row {key} already exists in vanilla DT {table}");
            if (t[table].Has(key)) throw new InvalidOperationException($"{s.Id}: row {key} added twice to {table}");
        }
        int I(string type, string prop) => schema.IndexOf(type, prop);

        // level
        var levels = t["Levels"];
        New("Levels", s.Id);
        var shortName = s.Level[(s.Level.LastIndexOf('/') + 1)..];
        var lv = levels.Rows[0].Clone(s.Id);
        lv.Set(I("DBLevel", "Value"), levels.SoftPath($"{s.Level}.{shortName}"));
        levels.Rows.Add(lv);

        // location
        var locs = t["TracksLocations"];
        var locRow = s.Location.Row;
        bool newLocation = s.Location.Template is not null;
        if (newLocation)
        {
            New("TracksLocations", locRow);
            var l = locs[s.Location.Template!].Clone(locRow);
            l.Set(I("DBTrackLocation", "Name"), DbTable.InvariantText(s.Location.Name ?? locRow));
            l.Set(I("DBTrackLocation", "HistoryAndTrivia"), DbTable.InvariantText(s.Location.Description ?? ""));
            if (s.Location.Country is not null) l.Set(I("DBTrackLocation", "Country"), locs.DbRef("Countries", s.Location.Country));
            l.Set(I("DBTrackLocation", "Tracks"), locs.DbRefs([], true));
            l.Set(I("DBTrackLocation", "DefaultVariant"), locs.DbRef("TracksVariants", s.Variants[0].Row));
            locs.Rows.Add(l);
        }
        var loc = locs[locRow];
        int tracksIdx = I("DBTrackLocation", "Tracks");
        var tracksList = loc.Get(tracksIdx).Zero ? [] : locs.ReadDbRefs(loc.Get(tracksIdx).Bytes, true);
        tracksList.Add(("Tracks", s.Id));
        loc.Set(tracksIdx, locs.DbRefs(tracksList, true));

        // track
        var tracks = t["Tracks"];
        New("Tracks", s.Id);
        var tr = tracks[s.Track.Template].Clone(s.Id);
        tr.Set(I("DBTrack", "Name"), DbTable.InvariantText(s.Track.Name));
        tr.Set(I("DBTrack", "Asset"), tracks.DbRef("Levels", s.Id));
        tr.Set(I("DBTrack", "Location"), tracks.DbRef("TracksLocations", locRow));
        tr.Set(I("DBTrack", "Variants"), tracks.DbRefs(s.Variants.Select(v => ("TracksVariants", v.Row)), true));
        tracks.Rows.Add(tr);

        // variants
        var vars = t["TracksVariants"];
        foreach (var v in s.Variants)
        {
            New("TracksVariants", v.Row);
            var r = vars[v.Template].Clone(v.Row);
            r.Set(I("DBTrackVariant", "Name"), DbTable.InvariantText(v.Name));
            r.Set(I("DBTrackVariant", "StageName"), DbTable.InvariantText(v.StageName ?? v.Name));
            r.Set(I("DBTrackVariant", "Track"), vars.DbRef("Tracks", s.Id));
            if (v.Length is { } len) r.Set(I("DBTrackVariant", "Length"), BitConverter.GetBytes(len));
            if (v.DataLayers is { } dl)
            {
                // FTrackVariantDataLayers {Common: FDataLayersList {Value: TArray<FDBReference>}, RaceEventTypes: TMap}
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                AcrRef.Unv.WriteHeader(w, [new(0, false)]);
                AcrRef.Unv.WriteHeader(w, [new(0, false)]);
                w.Write(vars.DbRefs(dl.Select(x => ("DataLayers", x)), false));
                r.Set(I("DBTrackVariant", "DataLayers"), ms.ToArray());
            }
            vars.Rows.Add(r);
        }
        Console.WriteLine($"stage {s.Id}: level {s.Level}, location {locRow}{(newLocation ? " (new)" : "")}, variants {string.Join(", ", s.Variants.Select(v => v.Row))}");
    }

    /// `dt-selftest --legacy <root>`: every database table round-trips byte for byte, with rows kept raw and re-encoded.
    public static int SelfTest(Opts o)
    {
        var schema = new Schema(o.Usmap);
        int bad = 0;
        foreach (var (table, file, rowStruct) in Tables)
        {
            var a = new DbTable(TablePath(o.Named["legacy"], file), schema, rowStruct);
            var orig = a.Package.GetExportData(0);
            var raw = a.Write();
            foreach (var r in a.Rows) r.Raw = null;
            var enc = a.Write();
            bool ok = raw.AsSpan().SequenceEqual(orig) && enc.AsSpan().SequenceEqual(orig);
            if (!ok) bad++;
            Console.WriteLine($"{table}: {a.Rows.Count} rows, raw {(raw.AsSpan().SequenceEqual(orig) ? "identical" : "DIFFERS")}, re-encoded {(enc.AsSpan().SequenceEqual(orig) ? "identical" : "DIFFERS")}");
        }
        return bad == 0 ? 0 : 3;
    }
}
