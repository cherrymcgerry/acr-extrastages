using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace AcrRef;

/// Hafren South's own scenery, hidden for the forward variant by data edits to the persistent level
/// (config/scenery.toml, docs/decisions.md D7 v1.6):
/// - streaming cells: a World Partition cell loads when any of its data layers is active, and a cell without data
///   layers always loads. Every cell except the kept ones gets only reverse-direction layers (the reverse rally-props
///   layer if it had none), so the forward variant never streams it in. The reverse variant still loads all of them.
/// - persistent actors (HLOD proxies, terrain tiles, background, treelines, turbines): their components get
///   bVisible = false, written as a zero-valued slot in the unversioned header (no value bytes change). The HLOD
///   runtime calls SetVisibility on HLOD components and overrides that, so with actorHidden (v1.9) the actor's
///   bHidden is also set, which hides all its components in game.
internal static class Scenery
{
    public const string PersistentLevel = "acr/Content/Levels/WalesS4HafrenSouth.umap";
    public const string Persistent = @"acr\Content\Levels\WalesS4HafrenSouth";
    const string CellClass = "WorldPartitionRuntimeLevelStreamingCell";
    // WorldPartitionRuntimeLevelStreamingCell slots (acr-ref schema): 0 LevelStreaming, 1 bIsAlwaysLoaded,
    // 2 bIsSpatiallyLoaded, 3 DataLayers, 4 bClientOnlyVisible, 5 bIsHLOD, 6 bBlockOnSlowLoading, 7 ContentBundleID,
    // 8 CellDebugColor, 9 CellGuid, 10 SourceCellGuid, 11 RuntimeCellData
    const int SlotDataLayers = 3;
    const int SlotAlwaysLoaded = 1;

    public static int Survey(Options o)
    {
        using var p = Program.Open(o);
        var exports = p.LoadPackage(o.Positional.FirstOrDefault() ?? PersistentLevel).GetExports().ToList();
        var actors = exports.Where(e => e.Outer?.Name.ToString() == "PersistentLevel" && e.TryGetValue(out FPackageIndex _, "RootComponent")).ToList();
        var rows = new List<(string cls, string key, FVector at)>();
        foreach (var a in actors)
        {
            var comps = exports.Where(e => e.Outer?.Name.ToString() == a.Name).ToList();
            var root = a.GetOrDefault<FPackageIndex>("RootComponent")?.ResolvedObject?.Load();
            var at = root?.GetOrDefault<FVector>("RelativeLocation") ?? default;
            var meshes = comps.Select(c => c.GetOrDefault<FPackageIndex>("StaticMesh")?.ResolvedObject?.Name.ToString())
                .Where(n => n is not null).Distinct().ToList();
            var decal = comps.Select(c => c.GetOrDefault<FPackageIndex>("DecalMaterial")?.ResolvedObject?.Name.ToString()).FirstOrDefault(n => n is not null);
            string key = a.ExportType + " " + (meshes.FirstOrDefault() ?? decal ?? "")
                         + (a.Name.Contains("HLODTrees") ? " (HLODTrees)" : a.Name.Contains("HLODTrack") ? " (HLODTrack)" : "");
            rows.Add((a.ExportType, key, at));
        }
        Console.WriteLine($"{actors.Count} actors with a root component in {o.Positional.FirstOrDefault() ?? PersistentLevel}");
        foreach (var g in rows.GroupBy(r => r.key).OrderByDescending(g => g.Count()))
        {
            double Mn(Func<FVector, float> f) => g.Min(r => f(r.at)) / 100;
            double Mx(Func<FVector, float> f) => g.Max(r => f(r.at)) / 100;
            Console.WriteLine($"{g.Count(),4}  {g.Key,-70} X {Mn(v => v.X),7:0}..{Mx(v => v.X),7:0}  Y {Mn(v => v.Y),7:0}..{Mx(v => v.Y),7:0}  Z {Mn(v => v.Z),5:0}..{Mx(v => v.Z),5:0} m");
        }
        return 0;
    }

    // ---------------- cells ----------------

    sealed record CellProps(List<Unv.Slot> Slots, Dictionary<int, (int at, int len)> Values, int End);

    static CellProps ParseCell(byte[] d)
    {
        int p = 0;
        var slots = Unv.ReadHeader(d, ref p);
        var vals = new Dictionary<int, (int, int)>();
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            int len = s.Index switch
            {
                0 or 11 => 4,
                1 or 2 or 4 or 5 or 6 => 1,
                7 or 8 or 9 or 10 => 16,
                SlotDataLayers => LayersLength(d, p),
                _ => throw new InvalidDataException($"cell property slot {s.Index} not handled"),
            };
            vals[s.Index] = (p, len);
            p += len;
        }
        return new(slots, vals, p);
    }

    // DataLayerInstanceNames: 0 bIsFirstDataLayerExternal (bool), 1 DataLayers (TArray<FName>)
    static int LayersLength(byte[] d, int p0)
    {
        int p = p0;
        foreach (var s in Unv.ReadHeader(d, ref p))
        {
            if (s.Zero) continue;
            if (s.Index == 0) p += 1;
            else if (s.Index == 1) p += 4 + 8 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            else throw new InvalidDataException($"DataLayerInstanceNames slot {s.Index}");
        }
        return p - p0;
    }

    static List<string> Layers(LegacyPackage pkg, byte[] d, CellProps c)
    {
        if (!c.Values.TryGetValue(SlotDataLayers, out var v)) return [];
        int p = v.at;
        var res = new List<string>();
        foreach (var s in Unv.ReadHeader(d, ref p))
        {
            if (s.Zero) continue;
            if (s.Index == 0) { p += 1; continue; }
            int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
            for (int i = 0; i < n; i++, p += 8)
                res.Add(pkg.NameAt(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4))));
        }
        return res;
    }

    static byte[] WithLayers(LegacyPackage pkg, byte[] d, CellProps c, IReadOnlyList<string> layers)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var slots = c.Slots.Where(s => s.Index != SlotDataLayers).Append(new Unv.Slot(SlotDataLayers, false)).OrderBy(s => s.Index).ToList();
        Unv.WriteHeader(w, slots);
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            if (s.Index != SlotDataLayers) { var (at, len) = c.Values[s.Index]; w.Write(d, at, len); continue; }
            Unv.WriteHeader(w, [new Unv.Slot(1, false)]);
            w.Write(layers.Count);
            foreach (var l in layers)
            {
                int n = pkg.FindName(l);
                if (n < 0) throw new InvalidDataException($"{l} is not in the persistent level's name map");
                w.Write(n); w.Write(0);
            }
        }
        w.Write(d, c.End, d.Length - c.End);
        return ms.ToArray();
    }

    /// bIsAlwaysLoaded of a streaming cell export (slot 1, one value byte when non-zero), and the cell's other slots / layers.
    internal static (bool alwaysLoaded, string describe) CellState(LegacyPackage pkg, byte[] d)
    {
        var c = ParseCell(d);
        bool on = c.Values.TryGetValue(SlotAlwaysLoaded, out var v) && d[v.at] != 0;
        var layers = Layers(pkg, d, c);
        return (on, $"slots {string.Join(",", c.Slots.Select(s => s.Index + (s.Zero ? "z" : "")))}; layers {(layers.Count == 0 ? "none" : string.Join("+", layers.Select(x => x.Replace("DataLayer_", "")[..4])))}");
    }

    /// bIsSpatiallyLoaded of a streaming cell export (slot 2).
    internal static bool SpatiallyLoaded(byte[] d)
    {
        var c = ParseCell(d);
        return c.Values.TryGetValue(2, out var v) && d[v.at] != 0;
    }

    internal const string CellExportClass = CellClass;

    /// The cell export with bIsAlwaysLoaded set (a non-zero bool: its slot gets one value byte among the values).
    internal static byte[] WithAlwaysLoaded(byte[] d)
    {
        var c = ParseCell(d);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var slots = c.Slots.Where(s => s.Index != SlotAlwaysLoaded).Append(new Unv.Slot(SlotAlwaysLoaded, false)).OrderBy(s => s.Index).ToList();
        Unv.WriteHeader(w, slots);
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            if (s.Index == SlotAlwaysLoaded) { w.Write((byte)1); continue; }
            var (at, len) = c.Values[s.Index];
            w.Write(d, at, len);
        }
        w.Write(d, c.End, d.Length - c.End);
        return ms.ToArray();
    }

    // ---------------- build ----------------

    internal sealed record Plan(HashSet<string> KeepCells, string[] ReverseLayers, string HiddenLayer, HashSet<string> HideClasses, Regex[] HideMeshes);

    static Plan LoadPlan(Toml cfg) => new(
        cfg.Arr("cells", "keep").Cast<string>().ToHashSet(),
        cfg.Arr("cells", "reverse_layers").Cast<string>().ToArray(),
        cfg.Str("cells", "hidden_layer"),
        cfg.Arr("persistent", "hide_actor_classes").Cast<string>().ToHashSet(),
        cfg.Arr("persistent", "hide_meshes").Cast<string>().Select(x => new Regex(x)).ToArray());

    public static JsonObject Build(string legacy, string outRoot, Toml cfg, string usmap, bool actorHidden = false)
    {
        var path = HostPaths.Resolve(legacy, Persistent);
        var raw = (File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".uexp")));
        var pkg = new LegacyPackage(path);
        var (h0, u0) = pkg.Save();
        if (!h0.AsSpan().SequenceEqual(raw.Item1) || !u0.AsSpan().SequenceEqual(raw.Item2))
            throw new InvalidDataException("persistent level does not round-trip byte-exactly through LegacyPackage");
        var plan = LoadPlan(cfg);
        var report = new JsonObject { ["round_trip"] = "byte-exact" };

        // cells
        var moves = new SortedDictionary<string, int>();
        int kept = 0, unchanged = 0, retagged = 0;
        for (int e = 0; e < pkg.ExportCount; e++)
        {
            if (pkg.ExportClass(e) != CellClass) continue;
            var d = pkg.GetExportData(e);
            var c = ParseCell(d);
            if (!c.Values.ContainsKey(0)) { unchanged++; continue; }   // no LevelStreaming: nothing streams
            var name = pkg.ExportName(e);
            var now = Layers(pkg, d, c);
            if (plan.KeepCells.Contains(name)) { kept++; continue; }
            var rev = now.Where(plan.ReverseLayers.Contains).ToList();
            var next = rev.Count > 0 ? rev : [plan.HiddenLayer];
            if (next.SequenceEqual(now)) { unchanged++; continue; }
            pkg.SetExportData(e, WithLayers(pkg, d, c, next));
            retagged++;
            string Short(List<string> l) => l.Count == 0 ? "none" : string.Join("+", l.Select(x => x.Replace("DataLayer_", "")[..4]));
            var k = $"{Short(now)} -> {Short(next)}";
            moves[k] = moves.GetValueOrDefault(k) + 1;
        }
        report["cells"] = new JsonObject
        {
            ["kept"] = kept, ["retagged"] = retagged, ["unchanged"] = unchanged,
            ["moves"] = new JsonObject(moves.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };

        // persistent actors
        var mappings = new FileUsmapTypeMappingsProvider(usmap, StringComparer.OrdinalIgnoreCase).MappingsForGame!;
        var byRule = new SortedDictionary<string, int>();
        var compClasses = new SortedDictionary<string, int>();
        var skipped = new SortedDictionary<string, int>();
        int actorsHidden = 0, compsHidden = 0, already = 0, actorFlag = 0;
        var actorFlagSkipped = new SortedDictionary<string, int>();
        var actorNames = new JsonArray();
        foreach (var (actor, rule, comps) in HiddenActors(pkg, plan))
        {
            actorsHidden++;
            actorNames.Add(pkg.ExportName(actor));
            byRule[rule] = byRule.GetValueOrDefault(rule) + 1;
            if (!actorHidden) { }
            else if (WithActorHidden(mappings, pkg.ExportClass(actor), pkg.GetExportData(actor)) is { } hiddenData)
            {
                pkg.SetExportData(actor, hiddenData);
                actorFlag++;
            }
            else actorFlagSkipped[pkg.ExportClass(actor)] = actorFlagSkipped.GetValueOrDefault(pkg.ExportClass(actor)) + 1;
            foreach (var c in comps)
            {
                var ccls = pkg.ExportClass(c);
                if (VisibleSlot(mappings, ccls) is not { } slot) continue;   // not a scene component (or not in the mappings)
                var d = pkg.GetExportData(c);
                int p = 0;
                var slots = Unv.ReadHeader(d, ref p);
                if (slots.FirstOrDefault(s => s.Index == slot) is { Index: > 0 } existing)
                {
                    if (existing.Zero) already++; else skipped[ccls] = skipped.GetValueOrDefault(ccls) + 1;
                    continue;
                }
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                Unv.WriteHeader(w, slots.Append(new Unv.Slot(slot, true)).OrderBy(s => s.Index).ToList());
                w.Write(d, p, d.Length - p);
                pkg.SetExportData(c, ms.ToArray());
                compsHidden++;
                compClasses[ccls] = compClasses.GetValueOrDefault(ccls) + 1;
            }
        }
        report["persistent"] = new JsonObject
        {
            ["actors_hidden"] = actorsHidden, ["components_hidden"] = compsHidden, ["already_hidden"] = already,
            ["by_rule"] = new JsonObject(byRule.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["component_classes"] = new JsonObject(compClasses.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["explicitly_visible_left"] = new JsonObject(skipped.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["actors"] = actorNames,
        };

        if (actorHidden)
        {
            report["persistent"]!["actors_bhidden"] = actorFlag;
            report["persistent"]!["actors_bhidden_skipped"] = new JsonObject(actorFlagSkipped.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        }
        var dest = Path.Combine(outRoot, Persistent + Path.GetExtension(path));
        pkg.SaveTo(dest);
        foreach (var ext in new[] { ".ubulk", ".uptnl" })
            if (File.Exists(Path.ChangeExtension(path, ext))) File.Copy(Path.ChangeExtension(path, ext), Path.ChangeExtension(dest, ext), true);
        Console.WriteLine($"scenery: {retagged} cells moved to reverse-only layers ({kept} kept), {actorsHidden} persistent actors / {compsHidden} components hidden");
        return report;
    }

    /// The actor export with AActor.bHidden = true (a non-zero bool: one value byte at its slot's place among the
    /// values). Null when the class is not in the mappings or a value before it has no fixed size here (BP actors with
    /// arrays/maps); those keep only their components' bVisible = false.
    static byte[]? WithActorHidden(CUE4Parse.MappingsProvider.TypeMappings mappings, string cls, byte[] d)
    {
        if (SlotOf(mappings, cls, "Actor", "bHidden") is not { } slot) return null;
        int p = 0;
        var slots = Unv.ReadHeader(d, ref p);
        if (slots.Any(s => s.Index == slot && !s.Zero)) return d;
        int at = p;
        foreach (var s in slots.Where(s => s.Index < slot && !s.Zero))
        {
            if (FixedSize(mappings, cls, s.Index) is not { } n) return null;
            at += n;
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        Unv.WriteHeader(w, slots.Where(s => s.Index != slot).Append(new Unv.Slot(slot, false)).OrderBy(s => s.Index).ToList());
        w.Write(d, p, at - p);
        w.Write((byte)1);
        w.Write(d, at, d.Length - at);
        return ms.ToArray();
    }

    static bool HiddenValue(byte[] d, int slot, CUE4Parse.MappingsProvider.TypeMappings mappings, string cls)
    {
        int p = 0;
        var slots = Unv.ReadHeader(d, ref p);
        if (!slots.Any(s => s.Index == slot && !s.Zero)) return false;
        foreach (var s in slots.Where(s => s.Index < slot && !s.Zero))
        {
            if (FixedSize(mappings, cls, s.Index) is not { } n) return false;
            p += n;
        }
        return d[p] == 1;
    }

    static int? FixedSize(CUE4Parse.MappingsProvider.TypeMappings mappings, string cls, int slot)
    {
        if (!mappings.Types.TryGetValue(cls, out var s)) return null;
        for (var cur = s; cur is not null; cur = cur.Super?.Value)
        {
            if (slot < cur.PropertyCount)
            {
                if (!cur.Properties.TryGetValue(slot, out var p)) return null;
                var t = p.MappingType;
                if (p.ArraySize != 1) return null;
                return t.Type switch
                {
                    "BoolProperty" or "ByteProperty" => 1,
                    "ObjectProperty" or "ClassProperty" or "IntProperty" or "UInt32Property" or "FloatProperty" => 4,
                    "Int64Property" or "DoubleProperty" or "NameProperty" => 8,
                    "StructProperty" => t.StructType switch { "Guid" => 16, "Vector" or "Rotator" => 24, _ => null },
                    _ => null,
                };
            }
            slot -= cur.PropertyCount;
        }
        return null;
    }

    /// Unversioned slot of owner.property on class cls (the class's own properties first, then each super's).
    static int? SlotOf(CUE4Parse.MappingsProvider.TypeMappings mappings, string cls, string owner, string property)
    {
        if (!mappings.Types.TryGetValue(cls, out var s)) return null;
        int b = 0;
        for (var cur = s; cur is not null; cur = cur.Super?.Value)
        {
            if (cur.Name == owner)
                foreach (var (i, p) in cur.Properties) if (p.Name == property) return b + i;
            b += cur.PropertyCount;
        }
        return null;
    }

    static string SlotName(CUE4Parse.MappingsProvider.TypeMappings mappings, string cls, int slot)
    {
        if (!mappings.Types.TryGetValue(cls, out var s)) return "?";
        for (var cur = s; cur is not null; cur = cur.Super?.Value)
        {
            if (slot < cur.PropertyCount)
                return cur.Properties.TryGetValue(slot, out var p) ? $"{cur.Name}.{p.Name}:{p.MappingType.Type}{(p.MappingType.StructType is { } st ? "<" + st + ">" : "")}" : $"{cur.Name}.#{slot}";
            slot -= cur.PropertyCount;
        }
        return "?";
    }

    static int? VisibleSlot(CUE4Parse.MappingsProvider.TypeMappings mappings, string cls)
    {
        if (!mappings.Types.TryGetValue(cls, out var s)) return null;
        int b = 0;
        for (var cur = s; cur is not null; cur = cur.Super?.Value)
        {
            if (cur.Name == "SceneComponent")
                foreach (var (i, p) in cur.Properties) if (p.Name == "bVisible") return b + i;
            b += cur.PropertyCount;
        }
        return null;
    }

    /// Persistent-level actors matched by [persistent] (by actor class, or by a mesh one of their components
    /// references), with all their components (nested included).
    /// A round trip of the mod container alone cannot name imports from other packages (Blueprint classes, meshes), so
    /// there the rules match fewer actors; byName (the build report's list) selects them by their own export names.
    static IEnumerable<(int actor, string rule, List<int> comps)> HiddenActors(LegacyPackage pkg, Plan plan, ISet<string>? byName = null)
    {
        int level = Enumerable.Range(0, pkg.ExportCount).Single(e => pkg.ExportName(e) == "PersistentLevel" && pkg.ExportClass(e) == "Level");
        var children = Enumerable.Range(0, pkg.ExportCount).ToLookup(pkg.ExportOuter);
        string DepName(int i) => i > 0 ? pkg.ExportName(i - 1) : i < 0 ? pkg.ImportName(-i - 1) : "";
        foreach (int a in children[level + 1])
        {
            var comps = new List<int>();
            void Collect(int x) { foreach (var c in children[x + 1]) { comps.Add(c); Collect(c); } }
            Collect(a);
            string cls = pkg.ExportClass(a);
            string? rule = plan.HideClasses.Contains(cls) ? cls
                : comps.SelectMany(c => pkg.ExportDeps(c)).Select(dp => DepName(dp.index)).FirstOrDefault(n => plan.HideMeshes.Any(r => r.IsMatch(n))) is { } mesh
                    ? $"{cls} {Regex.Replace(mesh, @"\d+$", "")}" : null;
            if (rule is null && byName?.Contains(pkg.ExportName(a)) == true) rule = "build report";
            if (rule is not null) yield return (a, rule, comps);
        }
    }

    /// scenery-actor-slots <legacy root> <scenery.toml>: the unversioned slots before AActor.bHidden on every hidden actor.
    public static int ActorSlots(Options o)
    {
        var pkg = new LegacyPackage(HostPaths.Resolve(o.Positional[0], Persistent));
        var plan = LoadPlan(Toml.Load(o.Positional[1]));
        var mappings = new FileUsmapTypeMappingsProvider(o.Usmap, StringComparer.OrdinalIgnoreCase).MappingsForGame!;
        var tally = new SortedDictionary<string, int>();
        foreach (var (a, rule, _) in HiddenActors(pkg, plan))
        {
            int p = 0;
            var cls = pkg.ExportClass(a);
            var slots = Unv.ReadHeader(pkg.GetExportData(a), ref p);
            var hidden = SlotOf(mappings, cls, "Actor", "bHidden");
            var key = $"{cls} (bHidden slot {hidden?.ToString() ?? "?"}): " + string.Join(", ", slots.Where(s => hidden is null || s.Index <= hidden)
                .Select(s => $"{s.Index}{(s.Zero ? "z" : "")} {SlotName(mappings, cls, s.Index)}"));
            tally[key] = tally.GetValueOrDefault(key) + 1;
        }
        foreach (var (k, n) in tally) Console.WriteLine($"{n,4} {k}");
        return 0;
    }

    // ---------------- check ----------------

    /// Re-reads the persistent level of a (round-tripped) mod root: the only forward-layer or layer-less streaming
    /// cells must be the kept ones, and every component of the hidden actors must carry a zero bVisible slot.
    public static int Check(string root, Toml cfg, string usmap, TextWriter w, JsonNode? report = null)
    {
        var expected = (report?["persistent"]?["actors"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToHashSet();
        int fail = 0;
        void C(bool ok, string msg) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {msg}"); if (!ok) fail++; }
        var path = HostPaths.Resolve(root, Persistent);
        var pkg = new LegacyPackage(path);
        var plan = LoadPlan(cfg);
        int forward = 0, total = 0;
        var forwardNames = new List<string>();
        for (int e = 0; e < pkg.ExportCount; e++)
        {
            if (pkg.ExportClass(e) != CellClass) continue;
            var d = pkg.GetExportData(e);
            var c = ParseCell(d);
            if (!c.Values.ContainsKey(0)) continue;
            total++;
            var l = Layers(pkg, d, c);
            if (l.Count == 0 || l.Any(x => !plan.ReverseLayers.Contains(x))) { forward++; forwardNames.Add(pkg.ExportName(e)); }
        }
        C(forwardNames.ToHashSet().SetEquals(plan.KeepCells),
          $"scenery: {forward} of {total} streaming cells load in the forward variant ({string.Join(", ", forwardNames)}); kept: {string.Join(", ", plan.KeepCells)}");
        var mappings = new FileUsmapTypeMappingsProvider(usmap, StringComparer.OrdinalIgnoreCase).MappingsForGame!;
        int actors = 0, comps = 0, hidden = 0;
        var found = new HashSet<string>();
        foreach (var (a, _, cs) in HiddenActors(pkg, plan, expected))
        {
            actors++;
            found.Add(pkg.ExportName(a));
            foreach (var c in cs)
            {
                if (VisibleSlot(mappings, pkg.ExportClass(c)) is not { } slot) continue;
                comps++;
                int p = 0;
                if (Unv.ReadHeader(pkg.GetExportData(c), ref p).Any(s => s.Index == slot && s.Zero)) hidden++;
            }
        }
        C(actors > 0 && hidden == comps, $"scenery: {hidden} of {comps} scene components of {actors} hidden persistent actors have bVisible = false");
        if (report?["persistent"]?["actors_bhidden"]?.GetValue<int>() is { } flagged && flagged > 0)
        {
            // the HLOD runtime switches its actors' components with SceneComponent::SetVisibility, overriding bVisible;
            // AActor.bHidden is not touched by it and hides every component in game
            int set = 0, hlods = 0, hlodSet = 0;
            foreach (var (a, _, _) in HiddenActors(pkg, plan, expected))
            {
                var cls = pkg.ExportClass(a);
                bool on = SlotOf(mappings, cls, "Actor", "bHidden") is { } slot && HiddenValue(pkg.GetExportData(a), slot, mappings, cls);
                if (on) set++;
                if (cls == "WorldPartitionHLOD") { hlods++; if (on) hlodSet++; }
            }
            C(set == flagged && hlods > 0 && hlodSet == hlods,
              $"scenery: {set} hidden persistent actors have bHidden = true (build: {flagged}), all {hlods} WorldPartitionHLOD actors among them");
        }
        if (expected is not null)
            C(found.SetEquals(expected), $"scenery: {found.Count} hidden actors = the {expected.Count} listed in the build report" +
              (found.SetEquals(expected) ? "" : $" (missing {string.Join(", ", expected.Except(found).Take(5))}; extra {string.Join(", ", found.Except(expected).Take(5))})"));
        C(File.Exists(Path.ChangeExtension(path, ".ubulk")), "scenery: persistent level .ubulk present next to the edited package");
        return fail;
    }
}
