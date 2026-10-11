using System.Runtime.InteropServices;
using EgoEngineLibrary.Archive.Jpk;
using EgoEngineLibrary.Formats.TrackQuadTree;
using EgoEngineLibrary.Formats.TrackQuadTree.Static;
using SharpGLTF.Schema2;

namespace Dr2Extract;

/// <summary>
/// The DR2 physics ground: a quad tree of triangles, each with a surface material name.
/// </summary>
internal static class TrackCommands
{
    public static int ToGltf(string[] args)
    {
        var pos = Program.Positional(args);
        if (pos[0].EndsWith(".jpk", StringComparison.OrdinalIgnoreCase))
        {
            var (min, max, cells) = Dr2Ground.ReadJpk(pos[0]);
            var stats = GroundGltf.Write(cells.SelectMany(c => c.Triangles), pos[1]);
            Console.WriteLine($"wrote {pos[1]}: {cells.Count} cells, {stats.Triangles} triangles");
            Console.WriteLine($"  qt.info bounds {min} .. {max}");
            Console.WriteLine($"  triangle bounds {stats.Min} .. {stats.Max}");
            foreach (var (name, n) in stats.TrianglesPerMaterial)
            {
                Console.WriteLine($"  material {name}\t{n}");
            }

            return 0;
        }

        var gltf = Load(pos[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pos[1]))!);
        gltf.Save(pos[1]);
        var materials = gltf.LogicalMaterials.Select(m => m.Name).ToList();
        var triangles = gltf.LogicalMeshes.SelectMany(m => m.Primitives)
            .Sum(p => p.GetIndices().Count / 3);
        Console.WriteLine($"wrote {pos[1]}: {triangles} triangles, {materials.Count} materials");
        foreach (var name in materials.Order())
        {
            Console.WriteLine($"  material {name}");
        }

        return 0;
    }

    public static int Height(string[] args)
    {
        var pos = Program.Positional(args);
        var (_, _, cells) = Dr2Ground.ReadJpk(pos[0]);
        for (var i = 1; i + 1 < pos.Length; i += 2)
        {
            var x = float.Parse(pos[i], System.Globalization.CultureInfo.InvariantCulture);
            var z = float.Parse(pos[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"{x} {z}\t{Dr2Ground.HeightAt(cells, x, z)?.ToString() ?? "none"}");
        }

        return 0;
    }

    public static int Info(string[] args)
    {
        var path = Program.Positional(args)[0];
        using var stream = File.OpenRead(path);
        var jpk = new JpkFile();
        jpk.Read(stream);
        Console.WriteLine($"{jpk.Entries.Count} entries");
        if (Program.Option(args, "--dump") is { } dumpDir)
        {
            Directory.CreateDirectory(dumpDir);
            foreach (var entry in jpk.Entries.Take(int.Parse(Program.Option(args, "--max") ?? "8")))
            {
                File.WriteAllBytes(Path.Combine(dumpDir, entry.Name), entry.Data);
            }
        }

        foreach (var entry in jpk.Entries.Take(int.Parse(Program.Option(args, "--max") ?? "8")))
        {
            var d = entry.Data;
            var f = MemoryMarshal.Cast<byte, float>(d.AsSpan(0, Math.Min(24, d.Length & ~3)));
            var i = MemoryMarshal.Cast<byte, int>(d.AsSpan(0, Math.Min(64, d.Length & ~3)));
            Console.WriteLine($"{entry.Name} len={d.Length} min=({string.Join(",", f[..Math.Min(3, f.Length)].ToArray())}) " +
                              $"max=({(f.Length >= 6 ? string.Join(",", f[3..6].ToArray()) : "")}) " +
                              $"ints[6..]={(i.Length > 6 ? string.Join(",", i[6..].ToArray()) : "")}");
        }

        return 0;
    }

    internal static ModelRoot Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (path.EndsWith(".vcqtc", StringComparison.OrdinalIgnoreCase))
        {
            return TrackGroundGltfConverter.Convert(new VcQuadTreeFile(data, VcQuadTreeFile.Identify(data)));
        }

        if (path.EndsWith(".cqtc", StringComparison.OrdinalIgnoreCase))
        {
            return TrackGroundGltfConverter.Convert(new CQuadTreeFile(data));
        }

        throw new NotSupportedException($"unsupported track file {path}");
    }
}
