using System.Text.RegularExpressions;

namespace AcrRef;

/// Offline checks that only measure how closely the drawn ground follows DR2's collision mesh (or how tidy a decoration is) are
/// tuned on New Zealand and fail on rocky stages (cliffs and rocks have collision but are drawn as objects). config/visuals.toml
/// [checks] advisory lists regexes over the check text; a failing check that matches prints WARN instead of FAIL and does not fail
/// the build. Everything else (package round trips, indices, sections, tables, collision physics) stays a hard FAIL.
internal static class Advisory
{
    static Regex[] _patterns = [];

    public static void Load(string? visualsToml)
    {
        if (visualsToml is null || !File.Exists(visualsToml)) return;
        var cfg = Toml.Load(visualsToml);
        _patterns = cfg["checks"].ContainsKey("advisory") ? cfg.Arr("checks", "advisory").Cast<string>().Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray() : [];
    }

    /// "PASS" / "WARN" / "FAIL" for a check result; true when it counts as a failure.
    public static string Verdict(bool ok, string what, out bool failure)
    {
        failure = !ok;
        if (ok) return "PASS";
        if (_patterns.Any(p => p.IsMatch(what))) { failure = false; return "WARN"; }
        return "FAIL";
    }
}
