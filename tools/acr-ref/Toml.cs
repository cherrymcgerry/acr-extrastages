using System.Globalization;

namespace AcrRef;

/// Minimal TOML subset for the repo's config files: [sections], bare or "quoted" keys, and values that are
/// strings, numbers, booleans or single-line arrays of those. Comments start with #.
internal sealed class Toml
{
    readonly Dictionary<string, Dictionary<string, object>> _t = new();

    public static Toml Load(string path)
    {
        var t = new Toml();
        var cur = t.Section("");
        int ln = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            ln++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new FormatException($"{path}:{ln}: bad section");
                cur = t.Section(line[1..^1].Trim());
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) throw new FormatException($"{path}:{ln}: expected key = value");
            var key = line[..eq].Trim().Trim('"');
            cur[key] = Value(line[(eq + 1)..].Trim(), $"{path}:{ln}");
        }
        return t;
    }

    static string StripComment(string s)
    {
        bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') q = !q;
            else if (s[i] == '#' && !q) return s[..i];
        }
        return s;
    }

    static object Value(string v, string where)
    {
        if (v.StartsWith('"') && v.EndsWith('"') && v.Length >= 2) return v[1..^1];
        if (v is "true" or "false") return v == "true";
        if (v.StartsWith('[') && v.EndsWith(']'))
            return v[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Value(x, where)).ToArray();
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new FormatException($"{where}: unsupported value {v}");
    }

    Dictionary<string, object> Section(string name)
    {
        if (!_t.TryGetValue(name, out var s)) _t[name] = s = new();
        return s;
    }

    public IReadOnlyDictionary<string, object> this[string section] => _t.TryGetValue(section, out var s) ? s : new();
    public string Str(string section, string key) => this[section].TryGetValue(key, out var v) && v is string s ? s : throw new KeyNotFoundException($"[{section}] {key}");
    public double Num(string section, string key) => this[section].TryGetValue(key, out var v) && v is double d ? d : throw new KeyNotFoundException($"[{section}] {key}");
    public object[] Arr(string section, string key) => this[section].TryGetValue(key, out var v) && v is object[] a ? a : throw new KeyNotFoundException($"[{section}] {key}");
}
