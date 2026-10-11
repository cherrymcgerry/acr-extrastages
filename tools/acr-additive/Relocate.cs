using System.Reflection;
using AcrRef;

namespace AcrAdditive;

/// Moves cooked legacy packages to new package paths so they coexist with the originals (new package ids, since
/// retoc derives the zen package id from the summary's package name).
///
/// A package is renamed by rewriting its summary PackageName and every name-map entry that names a relocated
/// package: an exact match, or a path below it ("old/..."; World Partition cells live under the level's folder).
/// Object renames (e.g. the World object, so that it keeps matching its package's short name) are exact name-map
/// matches. Soft object paths and level-streaming package names are FNames in cooked UE 5.6 packages, so the name map
/// covers every reference; actor labels and debug cell names are FStrings and stay as they are.
internal sealed class Relocation
{
    public readonly List<(string from, string to)> Packages = new();
    public readonly Dictionary<string, string> Objects = new();

    public string? MapPackage(string name)
    {
        foreach (var (from, to) in Packages)
        {
            if (name.Equals(from, StringComparison.OrdinalIgnoreCase)) return to;
            if (name.Length > from.Length && name[from.Length] == '/' && name.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + name[from.Length..];
        }
        return null;
    }

    public string? MapName(string name) => MapPackage(name) ?? (Objects.TryGetValue(name, out var o) ? o : null);

    static readonly FieldInfo NamesField = typeof(LegacyPackage).GetField("_names", BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly FieldInfo HashesField = typeof(LegacyPackage).GetField("_nameHashes", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// Renames the name-map entries of a loaded package in place; returns (index, old, new) of each change.
    public List<(int index, string from, string to)> RenameNames(LegacyPackage pkg)
    {
        var names = (List<string>)NamesField.GetValue(pkg)!;
        var hashes = (List<int>)HashesField.GetValue(pkg)!;
        var changes = new List<(int, string, string)>();
        for (int i = 0; i < names.Count; i++)
        {
            var to = MapName(names[i]);
            if (to is null || to == names[i]) continue;
            changes.Add((i, names[i], to));
            names[i] = to;
            hashes[i] = 0;   // legacy name hashes are not used by the zen conversion (new names are written as 0 too)
        }
        var dup = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"{pkg.PackageName}: rename makes name '{dup.Key}' appear twice");
        return changes;
    }

    /// Loads, renames and writes one package (plus its .ubulk/.uptnl, unchanged) under outRoot. Returns the new name.
    public string RelocateFile(string uassetPath, string outRoot, TextWriter log, Action<LegacyPackage>? edit = null)
    {
        var pkg = new LegacyPackage(uassetPath);
        var newName = MapPackage(pkg.PackageName) ?? throw new InvalidOperationException($"{pkg.PackageName} is not relocated by any rule");
        var changes = RenameNames(pkg);
        edit?.Invoke(pkg);
        var (h, u) = pkg.Save();
        h = SummaryPackageName.Rewrite(h, newName);
        var ext = Path.GetExtension(uassetPath);
        var dest = Path.Combine(outRoot, ContentPath(newName) + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, h);
        File.WriteAllBytes(Path.ChangeExtension(dest, ".uexp"), u);
        foreach (var side in new[] { ".ubulk", ".uptnl" })
        {
            var src = Path.ChangeExtension(uassetPath, side);
            if (File.Exists(src)) File.Copy(src, Path.ChangeExtension(dest, side), true);
        }
        log.WriteLine($"{pkg.PackageName} -> {newName}: {changes.Count} names renamed");
        return newName;
    }

    /// "/Game/A/B" -> "acr/Content/A/B" (legacy file layout written by retoc to-legacy and read by to-zen).
    public static string ContentPath(string packageName)
    {
        if (!packageName.StartsWith("/Game/")) throw new ArgumentException($"only /Game packages are supported: {packageName}");
        return Path.Combine("acr", "Content", packageName["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}
