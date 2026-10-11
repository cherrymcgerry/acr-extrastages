using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml;

namespace Dr2Extract;

internal static class StageNames
{
    private static readonly Dictionary<string, string> LocationKeyAliases = new()
    {
        ["usa"] = "new_england",
        ["scotland"] = "scotland_gravel",
    };

    /// <summary>English stage name: <c>lng_&lt;location&gt;_route_&lt;n&gt;</c>.</summary>
    public static string? Stage(IReadOnlyDictionary<string, string> lng, string location, int route) =>
        lng.GetValueOrDefault($"lng_{location}_route_{route}")?.Trim();

    /// <summary>English location name: <c>lng_location_full_&lt;key&gt;</c>, key = location minus <c>_rally_NN</c>.</summary>
    public static string? Location(IReadOnlyDictionary<string, string> lng, string location, string country)
    {
        var key = Regex.Replace(location, @"_rally_\d+$", "");
        key = LocationKeyAliases.GetValueOrDefault(key, key);
        return (lng.GetValueOrDefault($"lng_location_full_{key}") ?? lng.GetValueOrDefault($"lng_location_full_{country}"))?.Trim();
    }
}

internal sealed record Gate(int Id, float Distance, Vector3 Left, Vector3 Right, Vector3 Crossing);

internal sealed record Split(int Id, string Type, int Gate);

/// <summary><c>route_N/progress_track.xml</c>: the stage definition (gates along the route plus split markers).</summary>
internal sealed record ProgressTrack(string Direction, List<Split> Splits, List<Gate> Gates, string TrackType, float TotalDistance)
{
    public Gate GateOf(string splitType) =>
        Gates[Splits.FirstOrDefault(s => s.Type == splitType)?.Gate
              ?? throw new InvalidDataException($"progress track has no {splitType} split")];

    /// <summary>Unit direction of travel at a gate (central difference of neighbouring crossings).</summary>
    public Vector3 Forward(int gate)
    {
        var a = Gates[Math.Max(gate - 1, 0)].Crossing;
        var b = Gates[Math.Min(gate + 1, Gates.Count - 1)].Crossing;
        return Vector3.Normalize(b - a);
    }

    public static ProgressTrack Load(string path)
    {
        var doc = XmlCommands.Load(path);
        var route = doc.SelectSingleNode("/progress_track_data/routes/route") as XmlElement
                    ?? throw new InvalidDataException($"{path}: no routes/route");
        var splits = route.SelectNodes("split")!.Cast<XmlElement>()
            .Select(s => new Split(Int(s, "id"), s.GetAttribute("type"), Int(s, "gate"))).ToList();
        var gates = doc.SelectNodes("/progress_track_data/gates/gate")!.Cast<XmlElement>()
            .Select(g => new Gate(Int(g, "id"), Float(g, "distance"), Vec(g, "left"), Vec(g, "right"), Vec(g, "crossing")))
            .ToList();
        for (var i = 0; i < gates.Count; i++)
        {
            if (gates[i].Id != i)
            {
                throw new InvalidDataException($"{path}: gate {i} has id {gates[i].Id}");
            }
        }

        var track = doc.SelectSingleNode("/progress_track_data/track") as XmlElement
                    ?? throw new InvalidDataException($"{path}: no track element");
        return new ProgressTrack(route.GetAttribute("direction"), splits, gates, track.GetAttribute("type"),
            Float(track, "total_distance"));
    }

    internal static int Int(XmlElement e, string name) => int.Parse(e.GetAttribute(name), CultureInfo.InvariantCulture);

    internal static float Float(XmlElement e, string name) => float.Parse(e.GetAttribute(name), CultureInfo.InvariantCulture);

    internal static Vector3 Vec(XmlElement e, string child)
    {
        var text = (e.SelectSingleNode(child)?.InnerText ?? throw new InvalidDataException($"gate missing {child}"))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new Vector3(float.Parse(text[0], CultureInfo.InvariantCulture), float.Parse(text[1], CultureInfo.InvariantCulture),
            float.Parse(text[2], CultureInfo.InvariantCulture));
    }
}

internal sealed record PacenoteSubcall(int Type, int Delay, int Angle, int ModifierA, int ModifierB, bool CanConcatModA,
    bool CanConcatModB, int DistanceLink);

internal sealed record Pacenote(float Distance, string Name, string AudioName, List<PacenoteSubcall> Subcalls);

/// <summary><c>route_N/&lt;loc&gt;_codriver_1_pro_N_data.xml</c>: codriver calls keyed by distance along the route.</summary>
internal sealed record Codriver(float? ShakedownCutoff, float? FinishCallDistance, List<Pacenote> Calls)
{
    public static Codriver Load(string path)
    {
        var doc = XmlCommands.Load(path);
        var shake = doc.SelectSingleNode("/root/shakedown_info") as XmlElement;
        var calls = doc.SelectNodes("/root/codriver_calls/call")!.Cast<XmlElement>()
            .Select(c => new Pacenote(
                ProgressTrack.Float(c, "distanceAlongTrack"),
                c.GetAttribute("name").Trim(),
                c.GetAttribute("audioName"),
                c.SelectNodes("subcall")!.Cast<XmlElement>().Select(s => new PacenoteSubcall(
                    ProgressTrack.Int(s, "type"), ProgressTrack.Int(s, "delay"), ProgressTrack.Int(s, "angle"),
                    ProgressTrack.Int(s, "modifierA"), ProgressTrack.Int(s, "modifierB"),
                    bool.Parse(s.GetAttribute("canConcatModA")), bool.Parse(s.GetAttribute("canConcatModB")),
                    ProgressTrack.Int(s, "distanceLink"))).ToList()))
            .ToList();
        return new Codriver(
            shake is null ? null : ProgressTrack.Float(shake, "cutoffDistance"),
            shake is null ? null : ProgressTrack.Float(shake, "finishCallDistance"),
            calls);
    }
}

/// <summary>Box-shaped triggers from <c>route_N/triggers_game_object.xml</c>, converted to world transforms.</summary>
internal sealed record TriggerBox(string Type, string Name, Vector3 Position, Vector3 Forward, Vector3 Size)
{
    public static List<TriggerBox> Load(string path)
    {
        var result = new List<TriggerBox>();
        var doc = XmlCommands.Load(path);
        foreach (XmlElement trigger in doc.SelectNodes("/triggers/trigger")!)
        {
            var type = trigger.GetAttribute("type");
            var name = (trigger.SelectSingleNode("base_params") as XmlElement)?.GetAttribute("name") ?? "";
            if (trigger.SelectSingleNode("params") is not XmlElement p)
            {
                continue;
            }

            string? inverse = null;
            Vector3 size;
            if (p.HasAttribute("inverse_transform"))
            {
                inverse = p.GetAttribute("inverse_transform");
                size = new Vector3(ProgressTrack.Float(p, "width"), ProgressTrack.Float(p, "height"), ProgressTrack.Float(p, "length"));
            }
            else
            {
                string? Value(string n) => (p.SelectSingleNode($"value[@name='{n}']") as XmlElement)?.GetAttribute("value");
                inverse = Value("inv_transform");
                if (inverse is null)
                {
                    continue;
                }

                size = new Vector3(F(Value("width")), F(Value("height")), F(Value("length")));
            }

            if (!Matrix4x4.Invert(Matrix(inverse), out var world))
            {
                throw new InvalidDataException($"{path}: trigger {name} transform is singular");
            }

            if (result.Any(r => r.Name == name && r.Type == type && Vector3.Distance(r.Position, world.Translation) < 0.01f))
            {
                continue;
            }

            result.Add(new TriggerBox(type, name, world.Translation, Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33)), size));
        }

        return result;

        static float F(string? s) => s is null ? 0 : float.Parse(s, CultureInfo.InvariantCulture);
    }

    /// <summary>Row-major 4x4 with translation in the last row (System.Numerics convention).</summary>
    private static Matrix4x4 Matrix(string text)
    {
        var v = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (v.Length != 16)
        {
            throw new InvalidDataException($"expected 16 matrix values, got {v.Length}");
        }

        return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
    }
}

internal sealed record SurfaceMaterial(
    string Code,
    List<string> BaseChain,
    float? Friction,
    string? Mechanics,
    string? Audio,
    string? Particle,
    string? Mud);

/// <summary><c>surface_materials.xml</c> (game_1.dat): 4-char surface codes with single inheritance via <c>base_name</c>.</summary>
internal static class SurfaceMaterials
{
    public static Dictionary<string, SurfaceMaterial> Load(string path)
    {
        var doc = XmlCommands.Load(path);
        var raw = doc.SelectNodes("/MATERIALS_V2/MATERIAL")!.Cast<XmlElement>()
            .GroupBy(m => m.GetAttribute("name")).ToDictionary(g => g.Key, g => g.Last());
        var result = new Dictionary<string, SurfaceMaterial>();
        foreach (var code in raw.Keys)
        {
            var chain = new List<XmlElement>();
            for (var e = raw[code]; e is not null && chain.Count < 32;
                 e = e.HasAttribute("base_name") && raw.TryGetValue(e.GetAttribute("base_name"), out var b) ? b : null)
            {
                chain.Add(e);
            }

            string? Inherit(Func<XmlElement, string?> get) => chain.Select(get).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var friction = Inherit(e => e.HasAttribute("friction") ? e.GetAttribute("friction") : null);
            result[code] = new SurfaceMaterial(
                code,
                chain.Skip(1).Select(e => e.GetAttribute("name")).ToList(),
                friction is null ? null : float.Parse(friction, CultureInfo.InvariantCulture),
                Inherit(e => (e.SelectSingleNode("MECHANICS") as XmlElement)?.GetAttribute("name")),
                Inherit(e => (e.SelectSingleNode("AUDIO") as XmlElement)?.GetAttribute("name")),
                Inherit(e => (e.SelectSingleNode("PARTICLE") as XmlElement)?.GetAttribute("name")),
                Inherit(e => (e.SelectSingleNode("MUD2") as XmlElement)?.GetAttribute("name")));
        }

        return result;
    }
}
