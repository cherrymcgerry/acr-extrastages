using System.Data;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Language;

namespace Dr2Extract;

/// <summary>
/// Localisation tables (<c>language/language_eng.lng</c> in game_1.dat).
/// </summary>
internal static class LngCommands
{
    public static int Dump(string[] args)
    {
        var filter = Program.Option(args, "--filter") is { } f ? new Regex(f, RegexOptions.IgnoreCase) : null;
        foreach (var (key, value) in Load(Program.Positional(args)[0]).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (filter is null || filter.IsMatch(key) || filter.IsMatch(value))
            {
                Console.WriteLine($"{key}\t{value.ReplaceLineEndings(" ")}");
            }
        }

        return 0;
    }

    public static Dictionary<string, string> Load(string path)
    {
        using var stream = File.OpenRead(path);
        var table = new LngFile(stream).GetDataTable();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DataRow row in table.Rows)
        {
            result[(string)row[0]] = (string)row[1];
        }

        return result;
    }
}
