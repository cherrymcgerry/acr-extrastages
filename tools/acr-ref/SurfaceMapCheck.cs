namespace AcrRef;

/// `surface-map-check --map config/surface-map.toml --registry <DA_PhysMaterialsMappingCommon export .json>`:
/// every physical-material target in the surface map must be a registered Kunos surface (or "none").
internal static class SurfaceMapCheck
{
    public static int Run(Options o)
    {
        var map = o.Named.GetValueOrDefault("map") ?? throw new ArgumentException("--map is required");
        var registry = StageBuild.LoadSurfaceRegistry(o.Named.GetValueOrDefault("registry") ?? throw new ArgumentException("--registry is required"));
        var toml = Toml.Load(map);
        int checkedCount = 0;
        var bad = new List<string>();
        foreach (var section in new[] { "surfaces", "walls", "wall_templates" })
            foreach (var (key, value) in toml[section])
            {
                if (value is not string target || target == "none") continue;
                checkedCount++;
                if (!registry.Contains(StageBuild.PhysMatPrefix + target)) bad.Add($"[{section}] \"{key}\" = \"{target}\"");
            }
        if (checkedCount == 0) { Console.Error.WriteLine($"{map}: no surface targets found"); return 1; }
        foreach (var b in bad) Console.Error.WriteLine($"FAIL {b}: not in DA_PhysMaterialsMappingCommon");
        Console.WriteLine($"{(bad.Count == 0 ? "PASS" : "FAIL")} surface map: {checkedCount - bad.Count}/{checkedCount} targets are registered surfaces ({registry.Count} registered)");
        return bad.Count == 0 ? 0 : 1;
    }
}
