using System.IO.Abstractions;
using VictorBush.Ego.NefsLib;
using VictorBush.Ego.NefsLib.IO;
using VictorBush.Ego.NefsLib.Item;
using VictorBush.Ego.NefsLib.Progress;

namespace Dr2Extract;

/// <summary>
/// Layered read access to a DR2 install, mirroring how the game resolves files: patch archives
/// (<c>game\game_2_1.dat</c>, <c>game\game_2.dat</c>) override the location archive
/// (<c>locations\&lt;country&gt;__&lt;loc&gt;.nefs</c>), which sits above <c>game\game_1.dat</c> and <c>game\game.dat</c>.
/// Fetched files are cached under <c>&lt;cache&gt;/&lt;layer&gt;/&lt;path&gt;</c>.
/// </summary>
internal sealed class Dr2Install
{
    private static readonly string[] PatchLayers = ["game_2_1.dat", "game_2.dat"];
    private static readonly string[] BaseLayers = ["game_1.dat", "game.dat"];

    private readonly Dictionary<string, Layer> _layers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cache;

    private sealed record Layer(string Name, Dictionary<string, NefsItem> Files);

    public string Root { get; }

    /// <summary>Archive-relative path -> layer it was served from, for provenance.</summary>
    public SortedDictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

    private Dr2Install(string root, string cache)
    {
        Root = root;
        _cache = cache;
    }

    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("DR2_DIR") ?? @"C:\Games\DiRT Rally 2.0";

    public static async Task<Dr2Install> OpenAsync(string root, string cache)
    {
        var install = new Dr2Install(root, cache);
        var fs = new FileSystem();
        var progress = new NefsProgress(CancellationToken.None);
        var dataDir = Path.Combine(root, "game");
        var sources = await new NefsExeHeaderFinder(fs).FindHeadersAsync(Path.Combine(root, "dirtrally2.exe"), dataDir, false, progress);
        var reader = new NefsReader(fs);
        foreach (var name in PatchLayers.Concat(BaseLayers))
        {
            var source = sources.FirstOrDefault(s => string.Equals(Path.GetFileName(s.FilePath), name, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException($"no NeFS header for game\\{name} in dirtrally2.exe");
            install.Add(name, await reader.ReadArchiveAsync(source, progress));
        }

        return install;
    }

    /// <summary>Path of the location archive for a location folder name such as <c>new_zealand_rally_01</c>.</summary>
    public string LocationArchive(string location)
    {
        var matches = Directory.GetFiles(Path.Combine(Root, "locations"), $"*__{location}.nefs");
        return matches.Length == 1
            ? matches[0]
            : throw new FileNotFoundException($"expected one locations\\*__{location}.nefs, found {matches.Length}");
    }

    public IEnumerable<string> LocationArchives() =>
        Directory.EnumerateFiles(Path.Combine(Root, "locations"), "*.nefs").Order(StringComparer.OrdinalIgnoreCase);

    public async Task OpenLocationAsync(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        if (!_layers.ContainsKey(name))
        {
            Add(name, await Program.Open(archivePath));
        }
    }

    private void Add(string name, NefsArchive archive)
    {
        var files = new Dictionary<string, NefsItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, path) in Program.Files(archive, null))
        {
            files[path] = item;
        }

        _layers[name] = new Layer(name, files);
    }

    private IEnumerable<Layer> Order(string path)
    {
        foreach (var name in PatchLayers)
        {
            yield return _layers[name];
        }

        // tracks/locations/<country>/<loc>/... lives in locations/<country>__<loc>.nefs
        var parts = path.Split('/');
        if (parts.Length > 3 && parts[0] == "tracks" && parts[1] == "locations" &&
            _layers.TryGetValue($"{parts[2]}__{parts[3]}.nefs", out var location))
        {
            yield return location;
        }

        foreach (var name in BaseLayers)
        {
            yield return _layers[name];
        }
    }

    public bool Exists(string path) => Order(path).Any(l => l.Files.ContainsKey(path));

    public IEnumerable<string> List(string prefix) =>
        _layers.Values.SelectMany(l => l.Files.Keys).Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);

    /// <summary>
    /// Extracts (or reuses the cached copy of) the highest-priority version of <paramref name="path"/>. With
    /// <paramref name="temporary"/> the file goes to a fresh temp path that the caller must delete.
    /// </summary>
    public async Task<string> FetchAsync(string path, bool temporary = false)
    {
        var layer = Order(path).FirstOrDefault(l => l.Files.ContainsKey(path))
                    ?? throw new FileNotFoundException($"{path} not found in any loaded DR2 archive");
        var item = layer.Files[path];
        var target = temporary
            ? Path.Combine(Path.GetTempPath(), $"dr2-{Guid.NewGuid():N}{Path.GetExtension(path)}")
            : Path.Combine(_cache, Path.GetFileNameWithoutExtension(layer.Name), path);
        Sources[path] = layer.Name;
        if (!temporary && File.Exists(target) && new FileInfo(target).Length == item.ExtractedSize)
        {
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var transformer = new NefsTransformer(new FileSystem());
        await transformer.DetransformAsync(item.DataSource, target, 0, new NefsProgress(CancellationToken.None));
        return target;
    }
}
