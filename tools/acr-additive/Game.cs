using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace AcrAdditive;

/// CUE4Parse access to an ACR Paks folder (vanilla view or a check folder with extra containers).
internal static class Game
{
    public static DefaultFileProvider Open(Opts o)
    {
        var oodle = Path.Combine(AppContext.BaseDirectory, OodleHelper.OODLE_NAME_CURRENT);
        if (!File.Exists(oodle) && !OodleHelper.DownloadOodleDll(ref oodle))
            throw new InvalidOperationException("could not download the Oodle runtime");
        OodleHelper.Initialize(oodle);
        var paks = Path.Combine(o.Game, "acr", "Content", "Paks");
        var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly,
            new VersionContainer(EGame.GAME_UE5_6), StringComparer.OrdinalIgnoreCase);
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(o.Usmap, StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
        provider.PostMount();
        return provider;
    }

    /// "/Game/X/Y" for "acr/Content/X/Y.uasset".
    public static string PackageName(string filePath)
    {
        var p = filePath.Replace('\\', '/');
        int dot = p.LastIndexOf('.');
        if (dot > p.LastIndexOf('/')) p = p[..dot];
        const string content = "acr/Content/";
        return p.StartsWith(content, StringComparison.OrdinalIgnoreCase) ? "/Game/" + p[content.Length..] : "/" + p;
    }

    public sealed record Pkg(string Name, string Container, FPackageId[] Imports);

    /// Every package of every mounted IoStore container, from the container headers (no package loads).
    public static Dictionary<ulong, List<Pkg>> PackageIndex(DefaultFileProvider p)
    {
        var res = new Dictionary<ulong, List<Pkg>>();
        foreach (var vfs in p.MountedVfs.OfType<IoStoreReader>())
        {
            var h = vfs.ContainerHeader;
            if (h is null) continue;
            for (int i = 0; i < h.PackageIds.Length; i++)
            {
                var id = h.PackageIds[i];
                var name = vfs.PackageIdIndex.TryGetValue(id, out var f) ? PackageName(f.Path) : $"<{id.id:X16}>";
                if (!res.TryGetValue(id.id, out var l)) res[id.id] = l = new();
                l.Add(new Pkg(name, vfs.Name, h.StoreEntries[i].ImportedPackages ?? []));
            }
        }
        return res;
    }
}
