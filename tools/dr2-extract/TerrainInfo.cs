using System.Buffers.Binary;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;

namespace Dr2Extract;

/// <summary>
/// <c>terrain-info &lt;tracksplit.pssg&gt; [--node SHADOWCASTING_] [--vertices 12]</c>: diagnostic dump of how terrain
/// is shaded: shader group input definitions and passes, every non-texture shader input value, and the vertex
/// streams (layout plus sample values and per-byte histograms of packed streams) of the first matching node.
/// </summary>
internal static class TerrainInfo
{
    public static int Run(string[] args)
    {
        var pos = Program.Positional(args);
        var pssg = PssgCommands.Open(pos[0]);
        if (args.Contains("--stats"))
        {
            Stats(pssg);
            return 0;
        }

        var nodeFilter = Program.Option(args, "--node") ?? "SHADOWCASTING_";
        var sampleCount = int.Parse(Program.Option(args, "--vertices") ?? "12");

        var groups = new HashSet<string>();
        foreach (var shader in pssg.Elements<PssgShaderInstance>())
        {
            var group = shader.TryGetShaderGroup();
            var defs = group?.InputDefinitions.ToList() ?? [];
            if (group is not null && groups.Add(group.Id))
            {
                Console.WriteLine($"SHADERGROUP {group.Id}: {defs.Count} inputs, params {group.ParameterCount} saved {group.ParameterSavedCount} streams {group.ParameterStreamCount}");
                for (var i = 0; i < defs.Count; i++)
                {
                    Console.WriteLine($"  [{i}] {defs[i].InputName} {defs[i].Type} {defs[i].Format}");
                }

                foreach (var pass in group.ChildElements.OfType<PssgShaderGroupPass>())
                {
                    Console.WriteLine($"  pass vp={pass.VertexProgram} fp={pass.FragmentProgram} blend={pass.BlendEnable}");
                }
            }

            Console.WriteLine($"SHADER {shader.Id} ({shader.ShaderGroup})");
            foreach (var input in shader.Inputs)
            {
                var name = input.ParameterId < defs.Count ? defs[(int)input.ParameterId].InputName : $"param{input.ParameterId}";
                if (input.Type == "texture")
                {
                    Console.WriteLine($"  {name} = texture {input.Texture}");
                    continue;
                }

                Console.WriteLine($"  {name} = {input.Type} {input.Format}: {Decode(input.Value, input.Format)}");
            }
        }

        var library = pssg.Elements<PssgLibrary>().First(x => x.Type == "NODE");
        var node = library.Elements<PssgVisibleRenderNode>().FirstOrDefault(n => n.Id.Contains(nodeFilter, StringComparison.Ordinal));
        if (node is null)
        {
            Console.WriteLine($"no render node matching {nodeFilter}");
            return 1;
        }

        Console.WriteLine($"NODE {node.Id}");
        foreach (var stream in node.RenderInstances.OfType<PssgRenderStreamInstance>().Take(2))
        {
            var rds = stream.GetRenderDataSource();
            Console.WriteLine($" instance shader={stream.Shader} rds={rds.Id} index count={rds.IndexSource?.Count}");
            foreach (var s in rds.Streams)
            {
                var block = s.GetDataBlock();
                var desc = block.Streams.ElementAt((int)s.SubStream);
                var count = block.Data.Value.Length / Math.Max(1, (int)desc.Stride);
                Console.WriteLine($"  stream block={block.Id} sub={s.SubStream} {desc.RenderType} {desc.DataType} offset={desc.Offset} stride={desc.Stride} elements={count}");
                for (var v = 0; v < Math.Min(sampleCount, count); v++)
                {
                    var span = block.Data.Value.AsSpan((int)(desc.Stride * v + desc.Offset));
                    Console.WriteLine($"    v{v}: {Element(span, desc.DataType)}");
                }

                if (desc.DataType == "uint_color_argb")
                {
                    for (var b = 0; b < 4; b++)
                    {
                        var hist = new int[256];
                        for (var v = 0; v < count; v++)
                        {
                            hist[block.Data.Value[desc.Stride * v + desc.Offset + b]]++;
                        }

                        var top = hist.Select((n, i) => (n, i)).Where(x => x.n > 0).OrderByDescending(x => x.n).Take(24);
                        Console.WriteLine($"    byte{b}: {hist.Count(n => n > 0)} distinct; " +
                                          string.Join(" ", top.Select(x => $"{x.i}:{x.n}")));
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>Statistics of the packed terrain_array vertex data over every SHADOWCASTING_ node.</summary>
    private static void Stats(PssgFile pssg)
    {
        var layouts = new Dictionary<string, int>();
        var weights = new Dictionary<(float, float), int>();
        var colorAlpha = new int[256];
        var triples = new Dictionary<string, int>();
        var triangleWeights = new Dictionary<string, int>();
        var shared = 0;
        var mismatchUv = 0;
        long vertices = 0, triangles = 0;
        var uv0Min = new System.Numerics.Vector2(float.MaxValue);
        var uv0Max = new System.Numerics.Vector2(float.MinValue);
        var uvVsWorld = new List<(double U, double V, double X, double Z, string Shader)>();
        var meshUv = new List<(double U0, double V0, double U1, double V1, double X, double Z, string Node, bool Road)>();
        var scale = new SortedDictionary<string, List<double>>();
        var corners = new Dictionary<(int, int, int), List<(int A, int B, int C, int State)>>();
        var library = pssg.Elements<PssgLibrary>().First(x => x.Type == "NODE");
        foreach (var node in library.Elements<PssgVisibleRenderNode>().Where(n => n.Id.Contains("SHADOWCASTING_", StringComparison.Ordinal)))
        {
            foreach (var stream in node.RenderInstances.OfType<PssgRenderStreamInstance>())
            {
                var shader = stream.TryGetShaderInstance();
                if (shader?.ShaderGroup.TrimStart('#') != "terrain_array.fx")
                {
                    continue;
                }

                var rds = new RdsReader(stream.GetRenderDataSource());
                var layout = string.Join(",", rds.Layout.Select(l => $"{l.RenderType}:{l.DataType}"));
                layouts[layout] = layouts.GetValueOrDefault(layout) + 1;
                var seen = new HashSet<uint>();
                foreach (var (a, b, c) in rds.Triangles())
                {
                    triangles++;
                    var key = new List<string>();
                    var tri = new List<string>();
                    foreach (var v in new[] { a, b, c })
                    {
                        var col = rds.Raw("Color", 0, v);
                        var st0 = rds.Raw("ST", 0, v);
                        var st1 = rds.Raw("ST", 1, v);
                        key.Add($"{col.Y},{col.Z},{col.W}");
                        tri.Add($"{st1.Z}{st1.W}");
                        if (!seen.Add(v))
                        {
                            shared++;
                            continue;
                        }

                        vertices++;
                        weights[(st1.Z, st1.W)] = weights.GetValueOrDefault((st1.Z, st1.W)) + 1;
                        if (corners.Count < 3_000_000)
                        {
                            var pw = rds.Position(v);
                            var pk = ((int)MathF.Round(pw.X * 100), (int)MathF.Round(pw.Y * 100), (int)MathF.Round(pw.Z * 100));
                            if (!corners.TryGetValue(pk, out var cl)) corners[pk] = cl = [];
                            cl.Add(((int)col.Y / 8, (int)col.Z / 8, (int)col.W / 8, (int)st1.Z * 2 + (int)st1.W));
                        }
                        colorAlpha[(int)col.X]++;
                        if (st0.Z != st1.X || st0.W != st1.Y)
                        {
                            mismatchUv++;
                        }

                        uv0Min = System.Numerics.Vector2.Min(uv0Min, new(st0.X, st0.Y));
                        uv0Max = System.Numerics.Vector2.Max(uv0Max, new(st0.X, st0.Y));
                        if (uvVsWorld.Count < 200000 && vertices % 7 == 0)
                        {
                            var p = rds.Position(v);
                            uvVsWorld.Add((st0.X, st0.Y, p.X, p.Z, shader.Id));
                            var road = new[] { col.Y, col.Z, col.W }.Any(x => (int)x / 8 <= 2);
                            meshUv.Add((st0.Z, st0.W, st1.X, st1.Y, p.X, p.Z, node.Id, road));
                        }
                    }

                    if (triangles % 13 == 0)
                    {
                        var road = key[0].Split(',').Any(x => (int)float.Parse(x) / 8 <= 2);
                        var pa = rds.Position(a);
                        var pb = rds.Position(b);
                        var pc = rds.Position(c);
                        var area3 = System.Numerics.Vector3.Cross(pb - pa, pc - pa).Length() / 2;
                        if (area3 > 0.05f)
                        {
                            foreach (var (name, sel) in new (string, Func<uint, System.Numerics.Vector2>)[]
                                     {
                                         ("ST0.zw", v => { var r = rds.Raw("ST", 0, v); return new(r.Z, r.W); }),
                                         ("ST1.xy", v => { var r = rds.Raw("ST", 1, v); return new(r.X, r.Y); }),
                                         ("ST0.xy*4096", v => { var r = rds.Raw("ST", 0, v); return new(r.X * 4096, r.Y * 4096); }),
                                     })
                            {
                                var (ua, ub, uc) = (sel(a), sel(b), sel(c));
                                var e1 = ub - ua;
                                var e2 = uc - ua;
                                var areaUv = (e1.X * e2.Y - e1.Y * e2.X) / 2;
                                var sk = $"{name} road={road}";
                                if (!scale.TryGetValue(sk, out var list)) scale[sk] = list = [];
                                list.Add(Math.Sign(areaUv) * Math.Sqrt(Math.Abs(areaUv) / area3));
                            }
                        }
                    }

                    var distinct = key.Distinct().Count();
                    var k = distinct == 1 ? "same triple at all 3 corners" : $"{distinct} different triples";
                    triples[k] = triples.GetValueOrDefault(k) + 1;
                    var t = string.Join(" ", tri);
                    triangleWeights[t] = triangleWeights.GetValueOrDefault(t) + 1;
                }
            }
        }

        Console.WriteLine($"terrain_array: {triangles} triangles, {vertices} distinct vertices (per instance), shared corner refs {shared}");
        foreach (var (l, n) in layouts)
        {
            Console.WriteLine($"  layout x{n}: {l}");
        }

        Console.WriteLine("ST1.zw (per vertex): " + string.Join(" ", weights.OrderByDescending(x => x.Value).Take(20).Select(x => $"({x.Key.Item1},{x.Key.Item2}):{x.Value}")));
        Console.WriteLine($"distinct ST1.zw pairs: {weights.Count}");
        Console.WriteLine("Color alpha: " + string.Join(" ", colorAlpha.Select((n, i) => (n, i)).Where(x => x.n > 0).OrderByDescending(x => x.n).Take(16).Select(x => $"{x.i}:{x.n}")));
        Console.WriteLine("triples per triangle: " + string.Join(" ", triples.Select(x => $"{x.Key}:{x.Value}")));
        Console.WriteLine("triangle corner weights (zw zw zw): " + string.Join(" ", triangleWeights.OrderByDescending(x => x.Value).Take(16).Select(x => $"[{x.Key}]:{x.Value}")));
        Console.WriteLine($"ST0.zw != ST1.xy: {mismatchUv}; ST0.xy range {uv0Min} .. {uv0Max}");

        // Which slot (0 = r/A, 1 = g/B, 2 = b/C) does each (z, w) state select? Score every mapping by how often the
        // duplicated vertices of a position (different triples) then select the same layer.
        var groups = corners.Values.Where(l => l.Select(x => (x.A, x.B, x.C)).Distinct().Count() > 1).ToList();
        var results = new List<(string Map, double Agree)>();
        for (var m = 0; m < 81; m++)
        {
            var map = new[] { m % 3, m / 3 % 3, m / 9 % 3, m / 27 % 3 };
            long agree = 0, total = 0;
            foreach (var g in groups)
            {
                var layers = g.Select(x => map[x.State] switch { 0 => x.A, 1 => x.B, _ => x.C }).ToList();
                total++;
                if (layers.Distinct().Count() == 1) agree++;
            }

            results.Add(($"00->{"ABC"[map[0]]} 01->{"ABC"[map[1]]} 10->{"ABC"[map[2]]} 11->{"ABC"[map[3]]}", total == 0 ? 0 : (double)agree / total));
        }

        Console.WriteLine($"seam positions with different triples: {groups.Count}; best (z,w)->slot mappings: " +
                          string.Join("; ", results.OrderByDescending(r => r.Agree).Take(6).Select(r => $"{r.Map} {r.Agree:P1}")));

        foreach (var (k, list) in scale)
        {
            var s = list.Order().ToList();
            double Q(double q) => s[(int)(q * (s.Count - 1))];
            Console.WriteLine($"  sqrt(uv area / world area) {k}: n={s.Count} negative={s.Count(x => x < 0)} " +
                              $"p5 {Q(0.05):G3} p25 {Q(0.25):G3} median {Q(0.5):G3} p75 {Q(0.75):G3} p95 {Q(0.95):G3}");
        }

        foreach (var g in meshUv.GroupBy(x => (x.Node, x.Road)).Where(g => g.Count() > 50).OrderBy(g => g.Key.Road).Take(12))
        {
            var rows = g.ToList();
            var eq = rows.Count(r => r.U0 == r.U1 && r.V0 == r.V1);
            var (fu0, ru0) = PlaneFit(rows.Select(r => (r.X, r.Z, r.U0)).ToList());
            var (fv0, rv0) = PlaneFit(rows.Select(r => (r.X, r.Z, r.V0)).ToList());
            var (fu1, ru1) = PlaneFit(rows.Select(r => (r.X, r.Z, r.U1)).ToList());
            var (fv1, rv1) = PlaneFit(rows.Select(r => (r.X, r.Z, r.V1)).ToList());
            Console.WriteLine($"  {g.Key.Node} road={g.Key.Road} n={rows.Count} equal={eq}: " +
                              $"ST0.zw u={fu0.A:G3}x{fu0.B:+0.###;-0.###}z rms {ru0:G3}, v={fv0.A:G3}x{fv0.B:+0.###;-0.###}z rms {rv0:G3} | " +
                              $"ST1.xy u={fu1.A:G3}x{fu1.B:+0.###;-0.###}z rms {ru1:G3}, v={fv1.A:G3}x{fv1.B:+0.###;-0.###}z rms {rv1:G3}; " +
                              $"sample {rows[0].U0:G4},{rows[0].V0:G4} / {rows[0].U1:G4},{rows[0].V1:G4} @ {rows[0].X:F1},{rows[0].Z:F1}");
        }

        // ST0.xy against world XZ per shader: least squares u = a x + b z + c (planar if the residual is small)
        foreach (var g in uvVsWorld.GroupBy(x => x.Shader).Take(6))
        {
            var rows = g.ToList();
            foreach (var axis in new[] { "u", "v" })
            {
                var (fit, rms) = PlaneFit(rows.Select(r => (r.X, r.Z, axis == "u" ? r.U : r.V)).ToList());
                Console.WriteLine($"  {g.Key} {axis} = {fit.A:G4} x + {fit.B:G4} z + {fit.C:G4}, rms {rms:G3} ({rows.Count} samples)");
            }
        }
    }

    private static ((double A, double B, double C) Fit, double Rms) PlaneFit(List<(double X, double Z, double Y)> p)
    {
        double sxx = 0, sxz = 0, szz = 0, sx = 0, sz = 0, n = p.Count, sxy = 0, szy = 0, sy = 0;
        foreach (var (x, z, y) in p)
        {
            sxx += x * x; sxz += x * z; szz += z * z; sx += x; sz += z; sxy += x * y; szy += z * y; sy += y;
        }

        var m = new double[,] { { sxx, sxz, sx }, { sxz, szz, sz }, { sx, sz, n } };
        var r = new[] { sxy, szy, sy };
        var det = Det(m);
        double Solve(int col)
        {
            var c = (double[,])m.Clone();
            for (var i = 0; i < 3; i++) c[i, col] = r[i];
            return Det(c) / det;
        }

        var (a, b, cc) = (Solve(0), Solve(1), Solve(2));
        var rms = Math.Sqrt(p.Average(q => Math.Pow(a * q.X + b * q.Z + cc - q.Y, 2)));
        return ((a, b, cc), rms);
    }

    private static double Det(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
        m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    private static string Element(ReadOnlySpan<byte> s, string type) => type switch
    {
        "float2" => $"{F(s, 0)} {F(s, 4)}",
        "float3" => $"{F(s, 0)} {F(s, 4)} {F(s, 8)}",
        "float4" => $"{F(s, 0)} {F(s, 4)} {F(s, 8)} {F(s, 12)}",
        "half2" => $"{H(s, 0)} {H(s, 2)}",
        "half4" => $"{H(s, 0)} {H(s, 2)} {H(s, 4)} {H(s, 6)}",
        "uint_color_argb" => $"{s[0]} {s[1]} {s[2]} {s[3]}",
        _ => Convert.ToHexString(s[..Math.Min(16, s.Length)]),
    };

    private static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleBigEndian(s[o..]);

    private static float H(ReadOnlySpan<byte> s, int o) => (float)BinaryPrimitives.ReadHalfBigEndian(s[o..]);

    private static string Decode(byte[] value, string format)
    {
        if (value.Length % 4 == 0 && format.StartsWith("float", StringComparison.Ordinal))
        {
            return string.Join(" ", Enumerable.Range(0, value.Length / 4).Select(i => F(value, i * 4).ToString("G6")));
        }

        return Convert.ToHexString(value.AsSpan(0, Math.Min(64, value.Length)));
    }
}
