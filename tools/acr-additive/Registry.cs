using System.Buffers.Binary;
using Newtonsoft.Json;

namespace AcrAdditive;

/// One ported stage as the registry sees it (work/acr/specs/*.json, generated).
internal sealed class StageSpec
{
    public string Id = "";                 // DT_Levels and DT_Tracks row; prefix rows with "AcrXs" to stay clear of vanilla keys
    public string Level = "";              // package of the stage's own level, e.g. /Game/AcrXs/Stages/AcrXsTest/AcrXsTest
    public string? Stage;                  // DR2 stage id (work/ir/<Stage>); null for clone-only prototypes
    public LocationSpec Location = new();
    public TrackSpec Track = new();
    public List<VariantSpec> Variants = new();
    public MenuSpec? Menu;                 // generated menu images (docs/menu-images.md); null = the template's

    public sealed class MenuSpec
    {
        public bool Images;                // route map, track map and elevation textures (stage container) + DT_TracksTextures rows
        public string? Art;                // with Images: DR2 loading art (work/dr2/menu-art/<Art>.tpk) for the backdrop, banner and lobby images
        public double? ElevationOffset;    // metres added to DR2 heights for the menu's min / max / start elevation (DR2 Y is not altitude); null = keep the template's
    }

    public sealed class LocationSpec
    {
        public string Row = "Wales";       // existing DT_TracksLocations row, or a new one when Template is set
        public string? Template;           // new location: copy this row
        public string? Name;               // new location: display name (culture-invariant)
        public string? Description;
        public string? Country;            // new location: DT_Countries row (default: template's)
        public string? Tile;               // new location: vanilla DT_TracksLocationsBackgrounds row (main-menu tile); null = generated from TileArt
        public string? Logo;               // new location: vanilla DT_TracksLocationsLogos row; null = generated when GenLogo
        public string? TileArt;            // new location: DR2 loading art (work/dr2/menu-art/.../<TileArt>.tpk) for a generated tile
        public bool GenLogo;               // new location: rally plate logo drawn by scripts/menu-location-logos.ps1 (<logo dir>/<Row>.png)
        public string? Surface;            // Gravel / Tarmac / Snow: location SurfaceTypesData and every variant's surfaces + tyres (MenuLocations.Surfaces)
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

    /// Loads a spec. Stage display names are not stored in the (committed) specs: a ported stage's empty Track.Name /
    /// Variants[].Name / StageName are filled from the user's own DR2 localisation, i.e. the `name` of work/ir/<Stage>/stage.json
    /// (written by `dr2-extract stage-ir` from the installed game's language_eng.lng), when `irRoot` is given.
    public static StageSpec Load(string path, string? irRoot = null)
    {
        var s = JsonConvert.DeserializeObject<StageSpec>(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
        if (irRoot is not null) s.ResolveNames(irRoot);
        return s;
    }

    public void ResolveNames(string irRoot)
    {
        if (Stage is null || (Track.Name.Length > 0 && Variants.All(v => v.Name.Length > 0))) return;
        var json = Path.Combine(irRoot, Stage, "stage.json");
        if (!File.Exists(json)) throw new FileNotFoundException($"{Id}: stage names come from the DR2 localisation in {json} (run scripts\\prepare-stage-data.ps1)");
        var name = ((string?)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(json))["name"])?.Trim();
        if (string.IsNullOrEmpty(name)) throw new InvalidDataException($"{json}: no stage name (DR2 language_eng.lng has no lng_<location>_route_<n> entry)");
        // track card: the stage name without the Forward / Reverse / Sprint suffix (the location already shows the country)
        if (Track.Name.Length == 0)
            Track.Name = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(name, @"\s+(Forward|Reverse)$", ""), @"\s+Sprint$", "");
        foreach (var v in Variants)
        {
            if (v.Name.Length == 0) v.Name = name;
            if (string.IsNullOrEmpty(v.StageName)) v.StageName = name;
        }
    }
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

    /// Only written when a spec has Menu.Images: new rows pointing at the stage container's menu textures.
    public static readonly (string table, string file, string rowStruct) TexturesTable = ("TracksTextures", "TrackSelection/DT_TracksTextures", "DBTexture");

    public static string TablePath(string root, string file) => Path.Combine(root, Db, file + ".uasset").Replace('/', Path.DirectorySeparatorChar);

    public static bool AnyMenuImages(IEnumerable<StageSpec> specs) => specs.Any(s => s.Menu?.Images == true);

    /// The overridden tables: the four stage tables, DT_TracksTextures with menu images, the location texture tables with generated tiles / logos.
    public static (string table, string file, string rowStruct)[] AllTables(IReadOnlyCollection<StageSpec> specs)
    {
        var t = Tables.ToList();
        if (AnyMenuImages(specs)) t.Add(TexturesTable);
        if (MenuLocations.Any(specs))
            t.AddRange(MenuLocations.All.Where(i => MenuLocations.NewLocations(specs).Any(l => MenuLocations.For(l).Contains(i))).Select(i => (i.Table, i.File, "DBTexture")));
        return t.ToArray();
    }

    public static int Run(Opts o)
    {
        var legacy = o.Named["legacy"];
        var outRoot = o.Named["out"];
        var irRoot = o.Named.GetValueOrDefault("ir-root");
        var specs = o.Positional.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.json").OrderBy(x => x).ToArray() : new[] { p })
            .Select(p => StageSpec.Load(p, irRoot)).ToList();
        if (specs.Count == 0) throw new ArgumentException("no stage specs");
        foreach (var s in specs)
            if (s.Track.Name.Length == 0 || s.Variants.Any(v => v.Name.Length == 0))
                throw new InvalidOperationException($"{s.Id}: no stage name in the spec and no --ir-root to read it from");
        var schema = new Schema(o.Usmap);
        var tables = AllTables(specs);
        var t = tables.ToDictionary(x => x.table, x => new DbTable(TablePath(legacy, x.file), schema, x.rowStruct));
        var vanillaKeys = t.ToDictionary(kv => kv.Key, kv => kv.Value.Rows.Select(r => r.Key).ToHashSet());

        // Create locations before stages so Template owners need not sort before Row-only peers.
        foreach (var s in specs)
            if (s.Location.Template is not null)
                EnsureLocation(s, t, schema, vanillaKeys);

        foreach (var s in specs) Add(s, t, schema, vanillaKeys);
        foreach (var s in specs.Where(s => s.Menu is not null)) AddMenu(s, t, schema, vanillaKeys, irRoot);

        foreach (var (table, file, _) in tables)
        {
            var dest = TablePath(outRoot, file);
            t[table].Save(dest);
            Console.WriteLine($"{table}: {vanillaKeys[table].Count} vanilla + {t[table].Rows.Count - vanillaKeys[table].Count} new rows -> {dest}");
        }
        return 0;
    }

    static byte[] CopyValue(DbTable.Row src, int index)
    {
        var v = src.Get(index);
        if (v.Zero) throw new InvalidDataException($"{src.Key}: property {index} is not serialized");
        return (byte[])v.Bytes.Clone();
    }

    static void NewRow(string stageId, string table, string key, Dictionary<string, DbTable> t, Dictionary<string, HashSet<string>> vanilla)
    {
        if (vanilla[table].Contains(key)) throw new InvalidOperationException($"{stageId}: row {key} already exists in vanilla DT {table}");
        if (t[table].Has(key)) throw new InvalidOperationException($"{stageId}: row {key} added twice to {table}");
    }

    /// Creates a DT_TracksLocations row from Location.Template when missing (idempotent across specs).
    static void EnsureLocation(StageSpec s, Dictionary<string, DbTable> t, Schema schema, Dictionary<string, HashSet<string>> vanilla)
    {
        var locs = t["TracksLocations"];
        var locRow = s.Location.Row;
        if (locs.Has(locRow)) return;
        NewRow(s.Id, "TracksLocations", locRow, t, vanilla);
        int I(string type, string prop) => schema.IndexOf(type, prop);
        var l = locs[s.Location.Template!].Clone(locRow);
        l.Set(I("DBTrackLocation", "Name"), DbTable.InvariantText(s.Location.Name ?? locRow));
        l.Set(I("DBTrackLocation", "HistoryAndTrivia"), DbTable.InvariantText(s.Location.Description ?? ""));
        if (s.Location.Country is not null) l.Set(I("DBTrackLocation", "Country"), locs.DbRef("Countries", s.Location.Country));
        if (s.Location.Tile is not null) l.Set(I("DBTrackLocation", "BackgroundImage"), locs.DbRef("TracksLocationsBackgrounds", s.Location.Tile));
        if (s.Location.Logo is not null) l.Set(I("DBTrackLocation", "Logo"), locs.DbRef("TracksLocationsLogos", s.Location.Logo));
        foreach (var img in MenuLocations.For(s.Location))
        {
            var key = MenuLocations.Row(locRow);
            NewRow(s.Id, img.Table, key, t, vanilla);
            var tex = t[img.Table];
            var row = tex.Rows[0].Clone(key);
            row.Set(I("DBTexture", "Value"), tex.SoftPath(MenuLocations.ObjectPath(locRow, img.Kind)));
            tex.Rows.Add(row);
            l.Set(I("DBTrackLocation", img.Field), locs.DbRef(img.Table, key));
        }
        if (s.Location.Surface is { } surface)
        {
            int idx = I("DBTrackLocation", "SurfaceTypesData");
            l.Set(idx, CopyValue(locs[MenuLocations.Surfaces[surface].location], idx));
        }
        l.Set(I("DBTrackLocation", "Tracks"), locs.DbRefs([], true));
        l.Set(I("DBTrackLocation", "DefaultVariant"), locs.DbRef("TracksVariants", s.Variants[0].Row));
        locs.Rows.Add(l);
    }

    static void Add(StageSpec s, Dictionary<string, DbTable> t, Schema schema, Dictionary<string, HashSet<string>> vanilla)
    {
        void New(string table, string key) => NewRow(s.Id, table, key, t, vanilla);
        int I(string type, string prop) => schema.IndexOf(type, prop);

        // level
        var levels = t["Levels"];
        New("Levels", s.Id);
        var shortName = s.Level[(s.Level.LastIndexOf('/') + 1)..];
        var lv = levels.Rows[0].Clone(s.Id);
        lv.Set(I("DBLevel", "Value"), levels.SoftPath($"{s.Level}.{shortName}"));
        levels.Rows.Add(lv);

        // location (created in EnsureLocation when Template is set; Row-only peers append to existing)
        var locs = t["TracksLocations"];
        var locRow = s.Location.Row;
        bool newLocation = s.Location.Template is not null;
        if (!locs.Has(locRow))
        {
            if (s.Location.Template is null)
                throw new InvalidOperationException($"{s.Id}: location {locRow} does not exist and no Template to create it");
            EnsureLocation(s, t, schema, vanilla);
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
            if (s.Location.Surface is { } surface)
            {
                // menu surface split, car-setup default surface and allowed tyres of a vanilla stage on the same surface
                var src = vars[MenuLocations.Surfaces[surface].variant];
                foreach (var f in MenuLocations.VariantSurfaceFields)
                    r.Set(I("DBTrackVariant", f), CopyValue(src, I("DBTrackVariant", f)));
            }
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

    /// Menu block: texture rows for the stage container's generated images (Menu.Images), the race sector lengths of the
    /// level (StageBuild places RaceSector1/2 at the DR2 splits nearest 1/3 and 2/3) and, with an ElevationOffset, the
    /// min / max / start elevation. Needs the stage IR (--ir-root).
    static void AddMenu(StageSpec s, Dictionary<string, DbTable> t, Schema schema, Dictionary<string, HashSet<string>> vanilla, string? irRoot)
    {
        int I(string type, string prop) => schema.IndexOf(type, prop);
        var m = s.Menu!;
        if (s.Stage is null || irRoot is null) throw new InvalidOperationException($"{s.Id}: Menu needs Stage and --ir-root");
        var route = new StageRoute(Path.Combine(irRoot, s.Stage, "stage.json"));
        var vars = t["TracksVariants"];
        var tracks = t["Tracks"];
        if (m.Images)
        {
            var tex = t["TracksTextures"];
            foreach (var img in Menu.For(s))
            {
                var key = Menu.TextureRow(s.Id, img.Kind);
                NewRow(s.Id, "TracksTextures", key, t, vanilla);
                var row = tex.Rows[0].Clone(key);
                row.Set(I("DBTexture", "Value"), tex.SoftPath(Menu.ObjectPath(s.Id, img.Kind)));
                tex.Rows.Add(row);
            }
            tracks[s.Id].Set(I("DBTrack", "Map"), tracks.DbRef("TracksTextures", Menu.TextureRow(s.Id, "Map")));
            if (m.Art is not null)
            {
                tracks[s.Id].Set(I("DBTrack", "TrackImage"), tracks.DbRef("TracksTextures", Menu.TextureRow(s.Id, "Backdrop")));
                tracks[s.Id].Set(I("DBTrack", "LobbyTrackImage"), tracks.DbRef("TracksTextures", Menu.TextureRow(s.Id, "Backdrop")));
            }
        }
        int sectorsIdx = I("DBTrackVariant", "Sectors");
        foreach (var v in s.Variants)
        {
            var r = vars[v.Row];
            if (m.Images)
            {
                r.Set(I("DBTrackVariant", "MapSmall"), vars.DbRef("TracksTextures", Menu.TextureRow(s.Id, "MapSmall")));
                r.Set(I("DBTrackVariant", "ElevationMap"), vars.DbRef("TracksTextures", Menu.TextureRow(s.Id, "Elevation")));
                if (m.Art is not null)
                {
                    r.Set(I("DBTrackVariant", "VariantImage"), vars.DbRef("TracksTextures", Menu.TextureRow(s.Id, "Banner")));
                    r.Set(I("DBTrackVariant", "LobbyVariantImage"), vars.DbRef("TracksTextures", Menu.TextureRow(s.Id, "Lobby")));
                }
            }
            var ends = route.SectorEnds;
            var km = ends.Select((e, i) => Math.Round((e - (i == 0 ? 0 : ends[i - 1])) / 1000, 1)).ToArray();
            r.Set(sectorsIdx, SectorLengths(r.Get(sectorsIdx).Bytes, km, schema, schema.Property("DBTrackVariant", sectorsIdx), vars.Package));
            if (m.ElevationOffset is { } off)
            {
                var e = new byte[16];
                BinaryPrimitives.WriteDoubleLittleEndian(e, Math.Round(route.MinY + off));
                BinaryPrimitives.WriteDoubleLittleEndian(e.AsSpan(8), Math.Round(route.MaxY + off));
                r.Set(I("DBTrackVariant", "Elevation"), e);
                r.Set(I("DBTrackVariant", "ElevationAtStart"), BitConverter.GetBytes((float)(route.Points[0].y + off)));
            }
            Console.WriteLine($"  menu {v.Row}: {(m.Images ? "map / elevation images, " : "")}{(m.Images && m.Art is not null ? $"art {m.Art}, " : "")}sectors {string.Join(" + ", km)} km" +
                              (m.ElevationOffset is { } o2 ? $", elevation {route.MinY + o2:0}..{route.MaxY + o2:0} m (start {route.Points[0].y + o2:0})" : ""));
        }
    }

    /// TArray<FTrackSector> with each element's Length (km) replaced; the element count must match (it is the level's 3 sectors).
    static byte[] SectorLengths(byte[] bytes, double[] km, Schema schema, CUE4Parse.MappingsProvider.PropertyType arrayType, AcrRef.LegacyPackage pkg)
    {
        var d = (byte[])bytes.Clone();
        int p = 0;
        int n = BinaryPrimitives.ReadInt32LittleEndian(d); p += 4;
        if (n != km.Length) throw new InvalidDataException($"template has {n} sectors, the level has {km.Length}");
        var st = arrayType.InnerType!.StructType!;
        int lengthIdx = schema.IndexOf(st, "Length");
        for (int i = 0; i < n; i++)
        {
            bool done = false;
            foreach (var slot in AcrRef.Unv.ReadHeader(d, ref p))
            {
                if (slot.Zero) continue;
                if (slot.Index == lengthIdx) { BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(p), (float)km[i]); p += 4; done = true; }
                else schema.SkipProperty(d, ref p, schema.Property(st, slot.Index), pkg);
            }
            if (!done) throw new InvalidDataException($"sector {i} has no serialized Length");
        }
        if (p != d.Length) throw new InvalidDataException("sector array has trailing bytes");
        return d;
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
