using CUE4Parse.UE4.Assets.Exports.Engine;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AcrAdditive;

/// `usmap-check --usmap <file> [--game <dir>]`: CUE4Parse must load the five DB tables and the Hafren South packages
/// with the mappings file, without exceptions and without "did not read correctly" / missing-mapping warnings
/// (tools/acr-mappings calls this after a dump).
internal static class UsmapCheck
{
    const string Db = "acr/Content/Data/Database/Main/";

    static readonly string[] Tables =
    [
        "Common/DT_Levels",
        "TrackSelection/DT_Tracks",
        "TrackSelection/DT_TracksVariants",
        "TrackSelection/DT_TracksLocations",
        "TrackSelection/DT_TracksTextures",
    ];

    /// The host stage the converter builds on: persistent level, the generated cell stage-build edits, physics, pacenotes.
    static readonly string[] Packages =
    [
        "acr/Content/Levels/WalesS4HafrenSouth.umap",
        "acr/Content/Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP.umap",
        "acr/Content/Environments/WalesS4HafrenSouth/DataAssets/DA_WalesS4HafrenSouthBakedData.uasset",
        "acr/Content/Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward.uasset",
    ];

    sealed class Collect : ILogEventSink
    {
        public readonly List<string> Messages = new();
        public void Emit(LogEvent e) { lock (Messages) Messages.Add($"{e.Level}: {e.RenderMessage()}"); }
    }

    public static int Run(Opts o)
    {
        var sink = new Collect();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(sink).CreateLogger();
        using var p = Game.Open(o);
        int bad = 0;

        void Check(string label, Func<string> load)
        {
            int before = sink.Messages.Count;
            string detail;
            bool ok;
            try { detail = load(); ok = true; }
            catch (Exception e) { detail = $"{e.GetType().Name}: {e.Message}"; ok = false; }
            var warnings = sink.Messages.Skip(before).ToList();
            ok &= warnings.Count == 0;
            if (!ok) bad++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {label}: {detail}{(warnings.Count > 0 ? $", {warnings.Count} warnings" : "")}");
            foreach (var w in warnings.Take(10)) Console.WriteLine($"       {w}");
        }

        foreach (var t in Tables)
            Check(t, () =>
            {
                var dt = p.LoadPackage(Db + t + ".uasset").GetExports().OfType<UDataTable>().Single();
                int props = dt.RowMap.Values.Sum(r => r.Properties.Count);
                if (dt.RowMap.Count == 0 || props == 0) throw new InvalidDataException($"{dt.RowMap.Count} rows, {props} values");
                return $"{dt.RowMap.Count} rows, {props} values";
            });
        foreach (var pkg in Packages)
            Check(pkg, () =>
            {
                var exports = p.LoadPackage(pkg).GetExports().ToList();
                return $"{exports.Count} exports";
            });

        Console.WriteLine(bad == 0 ? "usmap-check: all packages loaded" : $"usmap-check: {bad} FAILED");
        return bad == 0 ? 0 : 3;
    }
}
