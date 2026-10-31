using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcrRef;

/// <c>preview --ir &lt;stage IR&gt; --report &lt;stage-build report.json&gt; --cell &lt;built cell .umap or mod-build dir&gt;
/// [--out work/preview/&lt;stage&gt;] [--every 250] [--size 960x540] [--far 2500]</c>
///
/// Offline visual comparison of a ported stage against DR2, for humans and for agents iterating on the visuals. The
/// same cameras (along the DR2 centreline: a chase view and an aerial view every N metres) render two scenes:
///   dr2:  the port's ground (terrain, road and vista sections of the built cell) plus every DR2 object instance with
///         its DR2 render mesh and texture, vegetation included: what the stage should look like;
///   port: exactly the visuals mesh of the built cell, textured as the build mapped it (DR2 textures where it made
///         DR2 materials, a flat class colour for the ACR wall materials).
/// Both are drawn by the CPU rasterizer in Raster.cs (no ACR materials, lighting or LODs; DR2 billboard LODs and
/// shadow cards are skipped, full meshes are drawn at every distance).
///
/// Output: station-&lt;m&gt;.png (2 x 2: DR2 | port for the chase and aerial view), contact.png (all chase views,
/// DR2 | port per row), metrics.json (per view: coverage per category and pixel agreement; a summary) and index.md.
internal static class Preview
{
    // pixel categories
    const byte Sky = 0, Ground = 1, Road = 2, Object = 3, Vegetation = 4, Trunk = 5;
    static readonly string[] CatNames = ["sky", "ground", "road", "object", "vegetation", "trunk"];

    sealed class Chunk
    {
        public Vector3[] P = [];
        public Vector2[] Uv = [];
        public uint[] Idx = [];
        public Tex? Tex;
        public Vector3 Colour;
        public bool Alpha;
        public byte Cat;
        public Vector3 C;
        public float R;
    }

    /// One DR2 template material in template space, drawn per instance.
    sealed record Sub(Vector3[] P, Vector2[] Uv, uint[] Idx, Tex? Tex, Vector3 Colour, bool Alpha, byte Cat);
    sealed record Template(List<Sub> Subs, Vector3 C, float R);
    sealed record Instance(Template T, Matrix4x4 M, Vector3 C, float R);

    public static int Run(Options o)
    {
        string Need(string k) => o.Named.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required");
        var ir = StageBuild.Ir.Load(Need("ir"));
        var report = JsonNode.Parse(File.ReadAllText(Need("report")))!;
        var p = report["placement"]!;
        var pl = new StageBuild.Placement(p["theta_deg"]!.GetValue<double>(), p["cx"]!.GetValue<double>(), p["cz"]!.GetValue<double>(),
                                          p["tx"]!.GetValue<double>(), p["tz"]!.GetValue<double>(), p["dy"]!.GetValue<double>(), p["slack_m"]!.GetValue<double>());
        var toUe = UeMatrix(pl);
        string cellArg = Need("cell");
        string cellPath = Directory.Exists(cellArg) ? HostPaths.Resolve(cellArg, HostPaths.Cell) : cellArg;
        string outDir = o.Out ?? o.Named.GetValueOrDefault("out") ?? Path.Combine("work", "preview", ir.Id);
        double every = double.Parse(o.Named.GetValueOrDefault("every", "250"), System.Globalization.CultureInfo.InvariantCulture);
        var size = o.Named.GetValueOrDefault("size", "960x540").Split('x').Select(int.Parse).ToArray();
        float farCm = float.Parse(o.Named.GetValueOrDefault("far", "2500"), System.Globalization.CultureInfo.InvariantCulture) * 100;
        Directory.CreateDirectory(outDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (portGround, portObjects, unresolved) = LoadPort(cellPath, report, ir);
        var dr2 = LoadDr2(ir, toUe);
        Console.WriteLine($"preview {ir.Id}: port {portGround.Sum(c => c.Idx.Length / 3)} ground + {portObjects.Sum(c => c.Idx.Length / 3)} object triangles; " +
                          $"dr2 {dr2.Count} instances of {dr2.Select(i => i.T).Distinct().Count()} templates; loaded in {sw.Elapsed.TotalSeconds:0.0} s");
        foreach (var u in unresolved) Console.WriteLine($"  no texture for {u} (drawn flat)");

        // cameras along the DR2 centreline (gate crossings), DR2 frame (Y up)
        var stations = new List<(double d, Vector3 at, Vector3 fwd)>();
        for (double d = Math.Min(every / 2, 50); d < ir.LengthM; d += every)
        {
            var a = Along(ir, d); var b = Along(ir, Math.Min(d + 20, ir.LengthM));
            var f = b - a; f.Y = 0;
            if (f.LengthSquared() < 1e-6) continue;
            stations.Add((d, a, Vector3.Normalize(f)));
        }
        var views = new (string name, Func<Vector3, Vector3, (Vector3 eye, Vector3 target)> cam)[]
        {
            ("chase", (at, f) => (at - f * 9 + Vector3.UnitY * 3.2f, at + f * 15 + Vector3.UnitY * 1f)),
            ("aerial", (at, f) => (at - f * 35 + Vector3.UnitY * 30f, at + f * 45)),
        };
        var jobs = (from s in stations.Select((s, i) => (s, i)) from v in views from scene in new[] { "dr2", "port" } select (s.i, s.s, v, scene)).ToList();
        var frames = new Raster[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, j =>
        {
            var (_, s, v, scene) = jobs[j];
            var (eye, target) = v.cam(s.at, s.fwd);
            var r = new Raster(size[0], size[1], Vector3.Transform(eye, toUe), Vector3.Transform(target, toUe)) { FarCm = farCm };
            foreach (var c in portGround) Draw(r, c);
            if (scene == "port") foreach (var c in portObjects) Draw(r, c);
            else foreach (var i in dr2) Draw(r, i);
            frames[j] = r;
        });
        Console.WriteLine($"  rendered {jobs.Count} views ({stations.Count} stations x {views.Length} views x 2 scenes) in {sw.Elapsed.TotalSeconds:0.0} s");

        // images, metrics
        Raster Frame(int station, string view, string scene) => frames[jobs.FindIndex(j => j.i == station && j.v.name == view && j.scene == scene)];
        var metrics = new JsonArray();
        var md = new StringBuilder($"# Preview {ir.Id}: {ir.Name}\n\nDR2 (left) against the port (right), chase view on top, aerial below. " +
                                   "Coverage = share of pixels; agreement = pixels with the same category in both.\n\n" +
                                   "| m | DR2 vegetation | port vegetation | DR2 objects | port objects | agreement (chase / aerial) | image |\n|---|---|---|---|---|---|---|\n");
        double sumAgree = 0, sumVegDr2 = 0, sumVegPort = 0, sumObjDr2 = 0, sumObjPort = 0; int nViews = 0;
        for (int si = 0; si < stations.Count; si++)
        {
            var row = new JsonObject { ["distance_m"] = Math.Round(stations[si].d) };
            var agreeText = new List<string>();
            double vegD = 0, vegP = 0, objD = 0, objP = 0;
            foreach (var v in views)
            {
                var a = Frame(si, v.name, "dr2"); var b = Frame(si, v.name, "port");
                var ca = Coverage(a); var cb = Coverage(b);
                double agree = Agreement(a, b);
                row[v.name] = new JsonObject
                {
                    ["dr2"] = Cov(ca), ["port"] = Cov(cb), ["agreement"] = Math.Round(agree, 3),
                };
                agreeText.Add($"{agree:0.00}");
                sumAgree += agree; nViews++;
                vegD += ca[Vegetation] + ca[Trunk]; vegP += cb[Vegetation] + cb[Trunk];
                objD += ca[Object]; objP += cb[Object];
            }
            vegD /= views.Length; vegP /= views.Length; objD /= views.Length; objP /= views.Length;
            sumVegDr2 += vegD; sumVegPort += vegP; sumObjDr2 += objD; sumObjPort += objP;
            metrics.Add(row);
            string name = $"station-{stations[si].d:00000}.png";
            Grid(Path.Combine(outDir, name), [[Frame(si, "chase", "dr2"), Frame(si, "chase", "port")], [Frame(si, "aerial", "dr2"), Frame(si, "aerial", "port")]], 1);
            md.Append($"| {stations[si].d:0} | {vegD:P0} | {vegP:P0} | {objD:P1} | {objP:P1} | {string.Join(" / ", agreeText)} | [{name}]({name}) |\n");
        }
        Grid(Path.Combine(outDir, "contact.png"), Enumerable.Range(0, stations.Count).Select(si => new[] { Frame(si, "chase", "dr2"), Frame(si, "chase", "port") }).ToArray(), 2);
        int n = Math.Max(1, stations.Count);
        var summary = new JsonObject
        {
            ["stage"] = ir.Id, ["stations"] = stations.Count, ["every_m"] = every,
            ["agreement_mean"] = Math.Round(sumAgree / Math.Max(1, nViews), 3),
            ["vegetation_cover_dr2"] = Math.Round(sumVegDr2 / n, 3), ["vegetation_cover_port"] = Math.Round(sumVegPort / n, 3),
            ["object_cover_dr2"] = Math.Round(sumObjDr2 / n, 3), ["object_cover_port"] = Math.Round(sumObjPort / n, 3),
            ["note"] = "dr2 and port share the port's ground sections, so differences come from objects and vegetation",
        };
        File.WriteAllText(Path.Combine(outDir, "metrics.json"), new JsonObject { ["summary"] = summary, ["stations"] = metrics }.ToJsonString(new() { WriteIndented = true }));
        md.Insert(md.ToString().IndexOf("| m |", StringComparison.Ordinal),
                  $"**Summary:** agreement {summary["agreement_mean"]}, vegetation cover DR2 {sumVegDr2 / n:P0} vs port {sumVegPort / n:P0}, " +
                  $"objects DR2 {sumObjDr2 / n:P1} vs port {sumObjPort / n:P1}. Overview: [contact.png](contact.png).\n\n");
        File.WriteAllText(Path.Combine(outDir, "index.md"), md.ToString());
        Console.WriteLine($"  agreement {summary["agreement_mean"]}, vegetation DR2 {sumVegDr2 / n:P0} vs port {sumVegPort / n:P0} -> {Path.GetFullPath(outDir)} ({sw.Elapsed.TotalSeconds:0.0} s)");
        return 0;
    }

    static JsonObject Cov(double[] c)
    {
        var o = new JsonObject();
        for (int i = 0; i < c.Length; i++) o[CatNames[i]] = Math.Round(c[i], 4);
        return o;
    }

    static double[] Coverage(Raster r)
    {
        var c = new double[CatNames.Length];
        foreach (var b in r.Cat) c[b]++;
        for (int i = 0; i < c.Length; i++) c[i] /= r.Cat.Length;
        return c;
    }

    static double Agreement(Raster a, Raster b)
    {
        int same = 0;
        for (int i = 0; i < a.Cat.Length; i++) if (a.Cat[i] == b.Cat[i]) same++;
        return (double)same / a.Cat.Length;
    }

    /// Writes rows of equally sized frames as one PNG, 4 px dark gaps, each frame downscaled by `div`.
    static void Grid(string path, Raster[][] rows, int div)
    {
        int fw = rows[0][0].W / div, fh = rows[0][0].H / div, gap = 4, cols = rows.Max(r => r.Length);
        int w = cols * fw + (cols - 1) * gap, h = rows.Length * fh + (rows.Length - 1) * gap;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 30; px[i + 1] = 30; px[i + 2] = 30; px[i + 3] = 255; }
        for (int ry = 0; ry < rows.Length; ry++)
            for (int cx = 0; cx < rows[ry].Length; cx++)
            {
                var f = rows[ry][cx];
                for (int y = 0; y < fh; y++)
                    for (int x = 0; x < fw; x++)
                    {
                        int s = ((y * div) * f.W + x * div) * 4, d = ((ry * (fh + gap) + y) * w + cx * (fw + gap) + x) * 4;
                        px[d] = f.Rgba[s]; px[d + 1] = f.Rgba[s + 1]; px[d + 2] = f.Rgba[s + 2]; px[d + 3] = 255;
                    }
            }
        Dds.WritePng(path, w, h, px);
    }

    static void Draw(Raster r, Chunk c)
    {
        if (!r.SphereVisible(c.C, c.R)) return;
        for (int t = 0; t + 2 < c.Idx.Length; t += 3)
        {
            uint a = c.Idx[t], b = c.Idx[t + 1], d = c.Idx[t + 2];
            r.Triangle(c.P[a], c.P[b], c.P[d], c.Uv[a], c.Uv[b], c.Uv[d], c.Tex, c.Colour, c.Alpha, c.Cat);
        }
    }

    [ThreadStatic] static Vector3[]? _scratch;

    static void Draw(Raster r, Instance i)
    {
        if (!r.SphereVisible(i.C, i.R)) return;
        foreach (var s in i.T.Subs)
        {
            if (_scratch is null || _scratch.Length < s.P.Length) _scratch = new Vector3[Math.Max(s.P.Length, 1024)];
            var w = _scratch;
            for (int k = 0; k < s.P.Length; k++) w[k] = Vector3.Transform(s.P[k], i.M);
            for (int t = 0; t + 2 < s.Idx.Length; t += 3)
            {
                uint a = s.Idx[t], b = s.Idx[t + 1], d = s.Idx[t + 2];
                r.Triangle(w[a], w[b], w[d], s.Uv[a], s.Uv[b], s.Uv[d], s.Tex, s.Colour, s.Alpha, s.Cat);
            }
        }
    }

    /// The DR2 -> UE placement as an affine matrix (row vectors): UE = 100 * swapYZ(R_y(theta) (p - c) + t).
    static Matrix4x4 UeMatrix(StageBuild.Placement pl)
    {
        double r = pl.ThetaDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        var m = new Matrix4x4(
            (float)(100 * c), (float)(-100 * s), 0, 0,
            0, 0, 100, 0,
            (float)(100 * s), (float)(100 * c), 0, 0,
            (float)(100 * (-c * pl.Cx - s * pl.Cz + pl.Tx)), (float)(100 * (s * pl.Cx - c * pl.Cz + pl.Tz)), (float)(100 * pl.Dy), 1);
        // self-check against the build's own mapping
        var probe = new[] { 123.4, 56.7, -890.1 };
        var want = pl.ToUe(probe);
        var got = Vector3.Transform(new Vector3((float)probe[0], (float)probe[1], (float)probe[2]), m);
        if (Math.Abs(got.X - want.X) > 1 || Math.Abs(got.Y - want.Y) > 1 || Math.Abs(got.Z - want.Z) > 1)
            throw new InvalidOperationException($"placement matrix mismatch: {got} vs ({want.X}, {want.Y}, {want.Z})");
        return m;
    }

    /// Point on the DR2 centreline (gate crossings) at distance d.
    static Vector3 Along(StageBuild.Ir ir, double d)
    {
        var g = ir.Gates;
        int i = 1;
        while (i < g.Count - 1 && g[i].distance < d) i++;
        var (d0, a, _, _) = g[i - 1]; var (d1, b, _, _) = g[i];
        double t = d1 > d0 ? Math.Clamp((d - d0) / (d1 - d0), 0, 1) : 0;
        return new Vector3((float)(a[0] + (b[0] - a[0]) * t), (float)(a[1] + (b[1] - a[1]) * t), (float)(a[2] + (b[2] - a[2]) * t));
    }

    // ---- port: the visuals mesh of the built cell ----------------------------------------------------------------------

    static readonly Dictionary<string, Vector3> WallColours = new()
    {
        ["MI_WallWoodTreeTrunks"] = new(0.33f, 0.24f, 0.16f), ["MI_WallWood"] = new(0.50f, 0.37f, 0.24f),
        ["MI_WallWoodFence"] = new(0.55f, 0.42f, 0.28f), ["MI_WallMetalPole"] = new(0.55f, 0.56f, 0.58f),
        ["MI_WallRock"] = new(0.47f, 0.45f, 0.41f), ["MI_WallConcrete"] = new(0.62f, 0.62f, 0.60f),
        ["MI_WallBasic"] = new(0.58f, 0.58f, 0.58f), ["MI_WallPlastic"] = new(0.80f, 0.25f, 0.18f),
        ["MI_WallPlasticBarrier"] = new(0.80f, 0.25f, 0.18f), ["MI_WallHayBale"] = new(0.80f, 0.70f, 0.40f),
        ["MI_WallCloth"] = new(0.25f, 0.35f, 0.65f), ["MI_SurfaceGrass"] = new(0.30f, 0.45f, 0.20f),
        ["MI_road"] = new(0.45f, 0.42f, 0.38f), ["MI_terrain"] = new(0.38f, 0.42f, 0.28f), ["MI_vista"] = new(0.38f, 0.42f, 0.30f),
    };

    static (List<Chunk> ground, List<Chunk> objects, List<string> unresolved) LoadPort(string cellPath, JsonNode report, StageBuild.Ir ir)
    {
        var cell = new LegacyPackage(cellPath);
        var pmcs = Enumerable.Range(0, cell.ExportCount).Where(i => cell.ExportClass(i) == "ProceduralMeshComponent").ToList();
        int e = pmcs.MaxBy(i => cell.GetExportData(i).Length);
        var c = ProcMesh.Parse(cell.GetExportData(e), e);
        int p = c.Slots[3].at; int n = BitConverter.ToInt32(c.Data, p); p += 4;
        var visuals = report["cell"]!["visuals"]!;
        var names = visuals["sections"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        var mats = visuals["materials"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        if (names.Count != n || mats.Count != n) throw new InvalidDataException($"{cellPath}: {n} sections, report lists {names.Count} names and {mats.Count} materials");

        // material -> base colour texture -> source texture (report provenance lines)
        var miTex = new Dictionary<string, string>(); var texSrc = new Dictionary<string, string>();
        foreach (var line in visuals["dr2"]?["packages"]?.AsArray().Select(x => x!.GetValue<string>()) ?? [])
        {
            var m = Regex.Match(line, @"/Mat/(MI_\w+): base colour (\S+)");
            if (m.Success) miTex[m.Groups[1].Value] = m.Groups[2].Value;
            m = Regex.Match(line, @"^(\S+/Tex/T_\w+): .* from (\S+)$");
            if (m.Success) texSrc[m.Groups[1].Value] = m.Groups[2].Value;
        }
        var unresolved = new SortedSet<string>();
        Tex? TexFor(string mi)
        {
            if (!miTex.TryGetValue(mi, out var t) || !texSrc.TryGetValue(t, out var src)) return null;
            foreach (var f in new[]
                     {
                         Path.Combine(ir.Dir, "textures", src + ".dds"),
                         Path.Combine(ir.Dir, "render", "ground_bake", src.Replace("ground_bake_", "") + ".dds"),
                         Path.Combine(ir.Dir, "render", "ground_bake", src + ".dds"),
                     })
                if (Tex.Load(f) is { } tex) return tex;
            unresolved.Add($"{mi} ({src})");
            return null;
        }

        var ground = new List<Chunk>(); var objects = new List<Chunk>();
        for (int i = 0; i < n; i++)
        {
            var s = ProcMesh.ReadSection(c.Data, ref p, out _);
            string name = names[i], mi = mats[i];
            if (name == "dummy") continue;
            byte cat = name.StartsWith("terrain") || name == "vista" ? Ground : name == "road" ? Road : name == "objects:trunks" ? Trunk : Object;
            var tex = TexFor(mi);
            var colour = tex is not null ? Vector3.One : WallColours.GetValueOrDefault(mi, new Vector3(0.6f));
            var uv = s.Uv.Count == s.Pos.Count ? s.Uv.ToArray() : new Vector2[s.Pos.Count];
            (cat is Ground or Road ? ground : objects).AddRange(Split(s.Pos.ToArray(), uv, s.Idx.ToArray(), tex, colour, false, cat));
        }
        return (ground, objects, unresolved.ToList());
    }

    /// Splits a section into spatially compact chunks (by triangle centroid on a 50 m grid) for culling.
    static IEnumerable<Chunk> Split(Vector3[] pos, Vector2[] uv, uint[] idx, Tex? tex, Vector3 colour, bool alpha, byte cat)
    {
        const float cell = 5000;
        var groups = new Dictionary<(int, int), List<uint>>();
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var ctr = (pos[idx[t]] + pos[idx[t + 1]] + pos[idx[t + 2]]) / 3;
            var key = ((int)MathF.Floor(ctr.X / cell), (int)MathF.Floor(ctr.Y / cell));
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<uint>();
            l.Add(idx[t]); l.Add(idx[t + 1]); l.Add(idx[t + 2]);
        }
        foreach (var l in groups.Values)
        {
            var map = new Dictionary<uint, uint>(); var p = new List<Vector3>(); var u = new List<Vector2>(); var ix = new uint[l.Count];
            for (int k = 0; k < l.Count; k++)
            {
                if (!map.TryGetValue(l[k], out var m)) { map[l[k]] = m = (uint)p.Count; p.Add(pos[l[k]]); u.Add(uv[l[k]]); }
                ix[k] = m;
            }
            var (ctr, rad) = Sphere(p);
            yield return new Chunk { P = p.ToArray(), Uv = u.ToArray(), Idx = ix, Tex = tex, Colour = colour, Alpha = alpha, Cat = cat, C = ctr, R = rad };
        }
    }

    static (Vector3 c, float r) Sphere(IReadOnlyCollection<Vector3> pts)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in pts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        var c = (mn + mx) / 2;
        return (c, (mx - mn).Length() / 2);
    }

    // ---- dr2: every object instance with its render mesh ---------------------------------------------------------------

    static List<Instance> LoadDr2(StageBuild.Ir ir, Matrix4x4 toUe)
    {
        var matInfo = new Dictionary<string, (string shader, string? diffuse)>();
        var mj = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "render", "objects.materials.json")))!.AsArray();
        foreach (var m in mj)
        {
            string? diffuse = m!["textures"]?.AsArray().Select(t => (param: t!["parameter"]!.GetValue<string>(), tex: t["texture"]!.GetValue<string>()))
                .Where(t => t.param.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)).Select(t => t.tex).FirstOrDefault();
            matInfo[m["name"]!.GetValue<string>()] = (m["shader_group"]?.GetValue<string>() ?? "", diffuse);
        }
        Tex? TexOf(string? reference)
        {
            if (reference is null) return null;
            var file = Path.GetFileNameWithoutExtension(reference.Split('#').Last());
            return Tex.Load(Path.Combine(ir.Dir, "textures", file + ".dds"));
        }

        var templates = new Dictionary<string, Template>();
        foreach (var g in new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives(withUv: true).GroupBy(x => x.Mesh))
        {
            var subs = new List<Sub>();
            var all = new List<Vector3>();
            foreach (var prim in g)
            {
                var (shader, diffuse) = matInfo.GetValueOrDefault(prim.Material, ("", null));
                string sh = shader.ToLowerInvariant();
                // DR2 draws these instead of (LOD billboards) or under (shadow / contact AO cards) the real mesh
                if (sh.Contains("billboard") || sh.Contains("shadow") || sh.Contains("contactao")) continue;
                byte cat = sh.Contains("foliage_trunk") ? Trunk : sh.Contains("foliage") ? Vegetation : Object;
                bool alpha = sh.Contains("foliage") || sh.Contains("alpha") || sh.Contains("blend");
                var pos = new Vector3[prim.Positions.Length / 3];
                for (int i = 0; i < pos.Length; i++) pos[i] = new Vector3(prim.Positions[i * 3], prim.Positions[i * 3 + 1], prim.Positions[i * 3 + 2]);
                var t0 = prim.Uv.Length > 0 ? prim.Uv[0] : null;
                var uv = new Vector2[pos.Length];
                if (t0 is not null) for (int i = 0; i < pos.Length; i++) uv[i] = new Vector2(t0[i * 2], t0[i * 2 + 1]);
                var tex = TexOf(diffuse);
                var colour = tex is not null ? Vector3.One : cat == Vegetation ? new Vector3(0.25f, 0.40f, 0.18f) : new Vector3(0.6f);
                subs.Add(new Sub(pos, uv, prim.Indices, tex, colour, alpha && tex is not null, cat));
                all.AddRange(pos);
            }
            if (subs.Count == 0) continue;
            var (c, r) = Sphere(all);
            templates[g.Key] = new Template(subs, c, r);
        }

        var instances = new List<Instance>();
        var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        foreach (var inst in stage["instances"]!.AsArray())
        {
            if (!templates.TryGetValue(inst!["template"]!.GetValue<string>(), out var t)) continue;
            var m = ObjectColliders.Trs(inst["transform"]) * toUe;
            float scale = MathF.Max(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13, MathF.Max(m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23, m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33));
            instances.Add(new Instance(t, m, Vector3.Transform(t.C, m), t.R * MathF.Sqrt(scale)));
        }
        return instances;
    }
}
