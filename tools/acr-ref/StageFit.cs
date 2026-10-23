using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcrRef;

/// `stage-fit`: read-only. For every stage IR under --ir-root (default work/ir) computes the placement `stage-build --bounds 1`
/// would use (rotation, rotated route extent, physics world box) and what it costs relative to Hafren South's own box, and flags
/// stages that are too long, too tall or need an unusually large world. Writes work/acr/fit-report.json and a one-line-per-stage table.
internal static class StageFit
{
    // Stages above this length are deferred (docs/additive-plan.md); the world areas below are warnings, not limits: nothing in game
    // has been tried above Te Awanga's ~4.7 km yet.
    const double MaxLengthM = 9000, WarnAreaKm2 = 45, WarnSpanY = 1200;

    public static int Run(Options o)
    {
        var irRoot = o.Named.GetValueOrDefault("ir-root", Path.Combine("work", "ir"));
        var outPath = o.Out ?? Path.Combine("work", "acr", "fit-report.json");
        double margin = double.Parse(o.Named.GetValueOrDefault("margin", Bounds.DefaultMarginM.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        double maxLen = double.Parse(o.Named.GetValueOrDefault("max-length", MaxLengthM.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var only = o.Filter is null ? null : new System.Text.RegularExpressions.Regex(o.Filter);
        bool preferHost = o.Named.GetValueOrDefault("prefer-host", "1") is "1" or "true";
        var rows = new JsonArray();
        var host = StageBuild.World.Host;
        var sb = new StringBuilder();
        sb.AppendLine($"{"stage",-34} {"len km",6} {"theta",5} {"route w x h m",14} {"world km2",9} {"Y span",6} {"mode",-11} {"status"}");
        int ok = 0, longC = 0, warn = 0, fail = 0;
        foreach (var dir in Directory.GetDirectories(irRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            if (only is not null && !only.IsMatch(id)) continue;
            if (!File.Exists(Path.Combine(dir, "stage.json"))) continue;
            var row = new JsonObject { ["stage"] = id };
            try
            {
                var ir = StageBuild.Ir.Load(dir);
                var pl = StageBuild.Solve(ir, 0, margin, preferHost);
                var w = pl.World!;
                var e = StageBuild.RotatedExtent(ir, (int)pl.ThetaDeg, out _, out _);
                string mode = w.IsHost ? "host-region" : "enlarged";
                double span = ir.CollisionMax[1] - ir.CollisionMin[1];
                double routeY = ir.Gates.Max(g => g.crossing[1]) - ir.Gates.Min(g => g.crossing[1]);
                // the host-bounded search (v1 placement): does the route fit Hafren's own safe region?
                string hostFit;
                try { var hp = StageBuild.Solve(ir); hostFit = $"fits host region, slack {hp.Slack:0} m"; }
                catch (InvalidOperationException) { hostFit = "does not fit the host region"; }
                var notes = new List<string>();
                string status = "ok";
                if (ir.LengthM > maxLen) { status = "deferred (long)"; notes.Add($"length {ir.LengthM / 1000:0.0} km > {maxLen / 1000:0.#} km"); }
                if (w.Area / 1e6 > WarnAreaKm2) { if (status == "ok") status = "warn"; notes.Add($"world {w.Area / 1e6:0.#} km2 > {WarnAreaKm2} km2"); }
                if (w.MaxY - w.MinY > WarnSpanY) { if (status == "ok") status = "warn"; notes.Add($"world height {w.MaxY - w.MinY:0} m"); }
                row["name"] = ir.Name; row["length_m"] = ir.LengthM; row["gates"] = ir.Gates.Count;
                row["theta_deg"] = pl.ThetaDeg; row["route_w_m"] = Math.Round(e.w, 1); row["route_h_m"] = Math.Round(e.h, 1);
                row["route_y_span_m"] = Math.Round(routeY, 1); row["collision_y_span_m"] = Math.Round(span, 1);
                row["margin_m"] = margin; row["floor_y_m"] = StageBuild.BoundsFloorY; row["dy"] = Math.Round(pl.Dy, 2);
                row["world"] = w.ToJson(); row["world_km2"] = Math.Round(w.Area / 1e6, 2);
                row["world_vs_host"] = w.IsHost ? "host bounds unchanged" : $"enlarged: x{w.Area / host.Area:0.0} area, Y {w.MaxY - w.MinY:0} m vs {host.MaxY - host.MinY:0} m";
                row["host_region"] = hostFit; row["placement_mode"] = mode; row["status"] = status; row["notes"] = new JsonArray(notes.Select(n => (JsonNode?)n).ToArray());
                if (status == "ok") ok++; else if (status.StartsWith("deferred")) longC++; else warn++;
                sb.AppendLine($"{id,-34} {ir.LengthM / 1000,6:0.0} {pl.ThetaDeg,5:0} {e.w,6:0} x {e.h,5:0} {w.Area / 1e6,9:0.0} {w.MaxY - w.MinY,6:0} {mode,-11} {status}{(notes.Count > 0 ? " - " + string.Join("; ", notes) : "")}");
            }
            catch (Exception ex)
            {
                row["status"] = "error"; row["error"] = ex.Message; fail++;
                sb.AppendLine($"{id,-34} ERROR {ex.Message}");
            }
            rows.Add(row);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var doc = new JsonObject
        {
            ["margin_m"] = margin, ["max_length_m"] = maxLen,
            ["host_world"] = host.ToJson(), ["summary"] = new JsonObject { ["ok"] = ok, ["warn"] = warn, ["deferred_long"] = longC, ["error"] = fail },
            ["stages"] = rows,
        };
        File.WriteAllText(outPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.ChangeExtension(outPath, ".txt"), sb.ToString());
        Console.Write(sb.ToString());
        Console.WriteLine($"{rows.Count} stages: {ok} ok, {warn} warn, {longC} deferred (long), {fail} error -> {outPath}");
        return 0;
    }
}
