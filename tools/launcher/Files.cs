using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AcrExtraStages;

internal static partial class Files
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string link, string target, IntPtr security);

    public static void HardLink(string link, string target)
    {
        if (!CreateHardLinkW(link, target, IntPtr.Zero))
            throw new IOException($"hard link {link} -> {target} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
    }

    public static void DeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, true); }
        catch (UnauthorizedAccessException)
        {
            // read-only files only (attributes are shared by every hard link, so never touch the others: they may be game files)
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                if (File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly);
            Directory.Delete(dir, true);
        }
    }

    /// Recursive copy, overwriting (robocopy /E in mods/additive/build.ps1): the second call wins.
    public static void CopyTree(string src, string dst)
    {
        if (!Directory.Exists(src)) throw new StepFailed($"missing {src}");
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dst, Path.GetRelativePath(src, f)), true);
    }

    public static void DeleteMatching(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, pattern)) File.Delete(f);
    }

    public static string HashText(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24];

    public static string ContentHash(string p)
    {
        if (!File.Exists(p)) return "none";
        using var fs = File.OpenRead(p);
        return Convert.ToHexString(SHA256.HashData(fs))[..16];
    }

    public static string FileSig(string p) => File.Exists(p) ? $"{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}" : "none";

    public static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts));
        File.Move(tmp, path, true);
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static double GB(long bytes) => bytes / (1024.0 * 1024 * 1024);
}

/// mods/common/vanilla.ps1: builds read a hard-link view of the shipped containers only, never ~mods or other mods.
internal static class Vanilla
{
    static readonly Regex Container = new(@"^(global|pakchunk\d+(optional)?-Windows)$", RegexOptions.IgnoreCase);

    public static string NewView(string game, string dest, TextWriter? log)
    {
        var paks = Path.Combine(game, "acr", "Content", "Paks");
        if (!File.Exists(Path.Combine(paks, "global.utoc"))) throw new StepFailed($"no ACR Paks at {paks}");
        var view = Path.Combine(dest, "acr", "Content", "Paks");
        Files.DeleteDir(dest);
        Directory.CreateDirectory(view);
        var skipped = new List<string>();
        foreach (var f in Directory.EnumerateFiles(paks))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".utoc" or ".ucas" or ".pak" or ".sig")) continue;
            if (!Container.IsMatch(Path.GetFileNameWithoutExtension(f))) { skipped.Add(Path.GetFileName(f)); continue; }
            Files.HardLink(Path.Combine(view, Path.GetFileName(f)), f);
        }
        AssertPaks(view);
        var dirs = Directory.GetDirectories(paks).Select(Path.GetFileName).ToList();
        if (skipped.Count > 0 || dirs.Count > 0)
            log?.WriteLine($"vanilla view: ignoring {skipped.Count} non-vanilla files at top level ({string.Join(", ", skipped)}) and folders: {string.Join(", ", dirs)}");
        return dest;
    }

    public static void AssertPaks(string paks)
    {
        var bad = Directory.EnumerateFiles(paks).Select(Path.GetFileName).Where(n => !Container.IsMatch(Path.GetFileNameWithoutExtension(n)!))
            .Concat(Directory.EnumerateDirectories(paks).Select(d => Path.GetFileName(d) + "\\")).ToList();
        if (bad.Count > 0) throw new StepFailed($"non-vanilla build input in {paks}: {string.Join(", ", bad)}");
        if (!File.Exists(Path.Combine(paks, "global.utoc"))) throw new StepFailed($"no global.utoc in {paks}");
    }

    public static List<string> Containers(string viewPaks) =>
        Directory.EnumerateFiles(viewPaks, "*.utoc").Select(f => $"{Path.GetFileNameWithoutExtension(f)} {new FileInfo(f).Length}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    /// The container list a legacy cache was extracted from, or null when it has no (vanilla) stamp.
    public static List<string>? Stamp(string legacy)
    {
        var s = Path.Combine(legacy, ".source.json");
        if (!File.Exists(s)) return null;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(s));
            if (!d.RootElement.TryGetProperty("vanilla", out var v) || v.ValueKind != JsonValueKind.True) return null;
            return d.RootElement.TryGetProperty("containers", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();
        }
        catch (JsonException) { return null; }
    }

    public static void WriteStamp(string legacy, string viewPaks)
    {
        Directory.CreateDirectory(legacy);
        Files.WriteJson(Path.Combine(legacy, ".source.json"),
            new { vanilla = true, containers = Containers(viewPaks), created = DateTime.Now.ToString("s") });
    }

    /// Drops a legacy cache that is unstamped or was extracted from other containers (a game update).
    public static void InvalidateIfStale(string legacy, string viewPaks)
    {
        var stamped = Stamp(legacy);
        var current = Containers(viewPaks);
        if (stamped is null || stamped.Count == 0 || !new HashSet<string>(stamped, StringComparer.OrdinalIgnoreCase).SetEquals(current))
            Files.DeleteDir(legacy);
    }
}
