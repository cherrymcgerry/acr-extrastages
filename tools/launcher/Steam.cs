using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AcrExtraStages;

/// Steam install discovery: registry (SteamPath / InstallPath) + steamapps\libraryfolders.vdf + appmanifest_<id>.acf.
internal static class Steam
{
    public const int AcrAppId = 3917090;
    public const int Dr2AppId = 690790;
    public const string AcrDefault = @"C:\Program Files (x86)\Steam\steamapps\common\Assetto Corsa Rally";
    public const string Dr2Default = @"C:\Games\DiRT Rally 2.0";

    public static string? SteamRoot()
    {
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            try
            {
                if (hive.OpenSubKey(key)?.GetValue(value) is string s && Directory.Exists(s)) return Path.GetFullPath(s);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
        var def = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(def) ? def : null;
    }

    /// Library folders from libraryfolders.vdf, each with the app ids it lists (the steam root first).
    public static List<(string Path, HashSet<string> Apps)> Libraries(string steamRoot)
    {
        var libs = new List<(string, HashSet<string>)>();
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            // minimal KeyValues reader: "path" "<dir>" and the "apps" block of each numbered entry
            string? path = null;
            HashSet<string>? apps = null;
            bool inApps = false;
            foreach (var raw in File.ReadLines(vdf))
            {
                var line = raw.Trim();
                var kv = Regex.Match(line, "^\"([^\"]*)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"$");
                if (line == "\"apps\"") { inApps = true; continue; }
                if (inApps && line == "}") { inApps = false; continue; }
                if (kv.Success && kv.Groups[1].Value == "path")
                {
                    if (path is not null) libs.Add((path, apps ?? new()));
                    path = kv.Groups[2].Value.Replace(@"\\", @"\");
                    apps = new();
                }
                else if (kv.Success && inApps) apps?.Add(kv.Groups[1].Value);
            }
            if (path is not null) libs.Add((path, apps ?? new()));
        }
        if (!libs.Any(l => string.Equals(Path.GetFullPath(l.Item1), steamRoot, StringComparison.OrdinalIgnoreCase)))
            libs.Insert(0, (steamRoot, new()));
        return libs;
    }

    /// Install folder of a Steam app: the library listing it (or any library with its manifest), steamapps\common\<installdir>.
    public static string? FindApp(int appId)
    {
        var root = SteamRoot();
        if (root is null) return null;
        var libs = Libraries(root);
        foreach (var (lib, apps) in libs.OrderByDescending(l => l.Apps.Contains(appId.ToString())))
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;
            var dir = AcfValue(acf, "installdir");
            if (dir is null) continue;
            var full = Path.Combine(lib, "steamapps", "common", dir);
            if (Directory.Exists(full)) return full;
        }
        return null;
    }

    public static string? AcfValue(string acf, string key)
    {
        foreach (var line in File.ReadLines(acf))
        {
            var m = Regex.Match(line.Trim(), $"^\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// Build id of a Steam install from steamapps\appmanifest_<id>.acf two levels up (common\<game> -> steamapps).
    public static string? BuildId(string gameDir, int appId)
    {
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(gameDir).TrimEnd('\\')));
        if (steamapps is null) return null;
        var acf = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
        return File.Exists(acf) ? AcfValue(acf, "buildid") : null;
    }

    public static bool IsAcr(string? dir) => dir is not null && File.Exists(Path.Combine(dir, "acr", "Content", "Paks", "global.utoc"));

    public static bool IsDr2(string? dir) => dir is not null && File.Exists(Path.Combine(dir, "dirtrally2.exe")) &&
                                             File.Exists(Path.Combine(dir, "game", "game.dat"));

    public static string? DetectAcr()
    {
        foreach (var c in new[] { Environment.GetEnvironmentVariable("ACR_GAME_DIR"), FindApp(AcrAppId), AcrDefault })
            if (IsAcr(c)) return Path.GetFullPath(c!);
        return null;
    }

    public static string? DetectDr2()
    {
        foreach (var c in new[] { Environment.GetEnvironmentVariable("DR2_DIR"), FindApp(Dr2AppId), Dr2Default })
            if (IsDr2(c)) return Path.GetFullPath(c!);
        return null;
    }
}
