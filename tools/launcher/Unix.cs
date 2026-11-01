using System.Runtime.InteropServices;

namespace AcrExtraStages;

/// Linux counterparts of the Windows-only bits (drive roots, process names of a Proton game).
internal static partial class Unix
{
    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr p);

    /// The path with symlinks resolved (/home -> /var/home on ostree systems), through its deepest existing ancestor.
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var rest = new Stack<string>();
        for (var d = full; d is not null; d = Path.GetDirectoryName(d))
        {
            var r = RealPath(d, IntPtr.Zero);
            if (r != IntPtr.Zero)
            {
                var s = Marshal.PtrToStringUTF8(r)!;
                Free(r);
                return rest.Aggregate(s, Path.Combine);
            }
            rest.Push(Path.GetFileName(d));
        }
        return full;
    }

    /// Mount point of the filesystem holding `path` (longest matching entry of /proc/self/mountinfo).
    public static string MountOf(string path)
    {
        var p = Resolve(path);
        var best = "/";
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var f = line.Split(' ');
            if (f.Length < 5) continue;
            // octal escapes of space, tab, newline and backslash (proc(5))
            var mp = System.Text.RegularExpressions.Regex.Replace(f[4], @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
            if (mp.Length > best.Length && (p == mp || p.StartsWith(mp.TrimEnd('/') + "/", StringComparison.Ordinal))) best = mp;
        }
        return best;
    }

    /// True when a Wine/Proton process runs one of these Windows executables (matched on the basename of argv[0]).
    public static bool WineProcessRunning(params string[] exeNames)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
            string argv0;
            try { argv0 = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0')[0]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var name = argv0[(argv0.LastIndexOfAny(['\\', '/']) + 1)..];
            if (exeNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }
}
