using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcrRef;

/// Streams the (very large) JSON export of a UBakedDataContainer and reports its contents
/// without loading the whole document.
internal static class BakedSummary
{
    public static int Run(Options o)
    {
        if (o.Positional.Count != 1) throw new ArgumentException("baked-summary needs the JSON produced by `export`");
        var outDir = o.Out ?? Path.GetDirectoryName(Path.GetFullPath(o.Positional[0]))!;
        Directory.CreateDirectory(outDir);
        var stem = Path.Combine(outDir, Path.GetFileName(o.Positional[0]).Replace(".uasset.json", ""));

        using var reader = new JsonTextReader(File.OpenText(o.Positional[0])) { FloatParseHandling = FloatParseHandling.Double };
        var summary = new JObject();
        using var csv = new StreamWriter(stem + ".mesh-colliders.csv");
        csv.WriteLine("index,vertices,triangles,colors,material,owner,tx,ty,tz,minx,miny,minz,maxx,maxy,maxz");

        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 3) continue;
            var name = (string)reader.Value!;
            reader.Read();
            switch (name)
            {
                case "BakedMeshCollisionData": summary[name] = MeshColliders(reader, csv); break;
                case "BakedSimpleCollisionData": summary[name] = SimpleColliders(reader); break;
                case "BakedSplinesData": summary[name] = Splines(reader); break;
                default: summary[name] = JToken.Load(reader); break;
            }
        }

        File.WriteAllText(stem + ".summary.json", summary.ToString(Formatting.Indented));
        Console.WriteLine($"summary -> {stem}.summary.json, per-collider table -> {stem}.mesh-colliders.csv");
        return 0;
    }

    static string Ref(JToken? t) => t switch
    {
        null or { Type: JTokenType.Null } => "null",
        JObject jo when jo["ObjectName"] is not null => (string?)jo["ObjectName"] ?? "null",
        JObject jo when jo["AssetPathName"] is not null => $"{jo["AssetPathName"]}{(string.IsNullOrEmpty((string?)jo["SubPathString"]) ? "" : ":" + jo["SubPathString"])}",
        _ => t.ToString(Formatting.None),
    };

    static JObject MeshColliders(JsonTextReader r, StreamWriter csv)
    {
        var byMat = new SortedDictionary<string, (int n, long tris)>();
        var ownerLevels = new SortedDictionary<string, int>();
        double[] gmin = [double.MaxValue, double.MaxValue, double.MaxValue], gmax = [double.MinValue, double.MinValue, double.MinValue];
        int count = 0, nonIdentity = 0, withColors = 0, maxVerts = 0;
        long verts = 0, tris = 0;
        while (r.Read() && r.TokenType != JsonToken.EndArray)
        {
            var c = JObject.Load(r);
            var pos = (JArray)c["Positions"]!;
            var idx = (JArray)c["Indices"]!;
            var cols = c["Colors"] as JArray;
            int nv = pos.Count / 3, nt = idx.Count / 3;
            var t = c["Transform"]!;
            var tr = t["Translation"]!;
            var rot = t["Rotation"]!;
            var sc = t["Scale3D"]!;
            bool ident = (double)tr["X"]! == 0 && (double)tr["Y"]! == 0 && (double)tr["Z"]! == 0 && (double)rot["W"]! == 1 && (double)sc["X"]! == 1 && (double)sc["Y"]! == 1 && (double)sc["Z"]! == 1;
            if (!ident) nonIdentity++;
            double[] mn = [double.MaxValue, double.MaxValue, double.MaxValue], mx = [double.MinValue, double.MinValue, double.MinValue];
            for (int i = 0; i < pos.Count; i++)
            {
                var v = (double)pos[i];
                mn[i % 3] = Math.Min(mn[i % 3], v);
                mx[i % 3] = Math.Max(mx[i % 3], v);
            }
            for (int k = 0; k < 3; k++) { gmin[k] = Math.Min(gmin[k], mn[k]); gmax[k] = Math.Max(gmax[k], mx[k]); }
            var mat = Ref(c["PhysMaterial"]);
            var owner = Ref(c["Owner"]);
            var lvl = owner.Contains(':') ? owner[..owner.IndexOf(':')] : owner;
            ownerLevels[lvl] = ownerLevels.GetValueOrDefault(lvl) + 1;
            var e = byMat.GetValueOrDefault(mat);
            byMat[mat] = (e.n + 1, e.tris + nt);
            if (cols is { Count: > 0 }) withColors++;
            maxVerts = Math.Max(maxVerts, nv);
            verts += nv; tris += nt;
            csv.WriteLine(string.Join(",", new object[] { count, nv, nt, cols?.Count ?? 0, Q(mat), Q(owner), F(tr["X"]), F(tr["Y"]), F(tr["Z"]), F(mn[0]), F(mn[1]), F(mn[2]), F(mx[0]), F(mx[1]), F(mx[2]) }));
            if (count == 0) Console.WriteLine($"first collider keys: {string.Join(", ", c.Properties().Select(p => p.Name))}");
            count++;
        }
        return new JObject
        {
            ["count"] = count,
            ["totalVertices"] = verts,
            ["totalTriangles"] = tris,
            ["maxVerticesInOneCollider"] = maxVerts,
            ["nonIdentityTransforms"] = nonIdentity,
            ["collidersWithColors"] = withColors,
            ["boundsMin"] = new JArray(gmin),
            ["boundsMax"] = new JArray(gmax),
            ["byMaterial"] = new JObject(byMat.Select(kv => new JProperty(kv.Key, new JObject { ["colliders"] = kv.Value.n, ["triangles"] = kv.Value.tris }))),
            ["byOwnerPackage"] = new JObject(ownerLevels.Select(kv => new JProperty(kv.Key, kv.Value))),
        };
    }

    static JObject SimpleColliders(JsonTextReader r)
    {
        var byMat = new SortedDictionary<string, int>();
        int count = 0, boxes = 0, spheres = 0, caps = 0;
        JObject? sample = null;
        while (r.Read() && r.TokenType != JsonToken.EndArray)
        {
            var c = JObject.Load(r);
            sample ??= c;
            boxes += (c["Boxes"] as JArray)?.Count ?? 0;
            spheres += (c["Spheres"] as JArray)?.Count ?? 0;
            caps += (c["Capsules"] as JArray)?.Count ?? 0;
            var mat = Ref(c["PhysMaterial"]);
            byMat[mat] = byMat.GetValueOrDefault(mat) + 1;
            count++;
        }
        return new JObject
        {
            ["count"] = count,
            ["boxes"] = boxes,
            ["spheres"] = spheres,
            ["capsules"] = caps,
            ["byMaterial"] = new JObject(byMat.Select(kv => new JProperty(kv.Key, kv.Value))),
            ["sample"] = sample,
        };
    }

    static JArray Splines(JsonTextReader r)
    {
        var list = new JArray();
        while (r.Read() && r.TokenType != JsonToken.EndArray)
        {
            var s = JObject.Load(r);
            var b = s["BaseSplineData"]!;
            var pts = (JArray?)b["Points"] ?? new JArray();
            var pay = (JArray?)s["Payloads"] ?? new JArray();
            list.Add(new JObject
            {
                ["ID"] = s["ID"],
                ["points"] = pts.Count,
                ["firstPoint"] = pts.FirstOrDefault(),
                ["lastPoint"] = pts.LastOrDefault(),
                ["gridElements"] = ((JArray?)b["GridElementsData"])?.Count ?? 0,
                ["gridData"] = b["GridData"],
                ["payloads"] = pay.Count,
                ["firstPayload"] = pay.FirstOrDefault(),
            });
        }
        return list;
    }

    static string F(JToken? t) => F((double)t!);
    static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
