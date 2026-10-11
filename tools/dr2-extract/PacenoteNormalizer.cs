namespace Dr2Extract;

/// <summary>
/// Codriver call -> normalized pacenote tokens. Semantics and their evidence are documented in docs/dr2-formats.md
/// ("Codriver calls"); anything not decoded is passed through with <c>kind = "unknown"</c> and the raw values.
/// </summary>
internal static class PacenoteNormalizer
{
    /// <summary>distanceLink codes whose spoken distance was matched against transcriptions.</summary>
    public static readonly IReadOnlyDictionary<int, int> DistanceCodes = new Dictionary<int, int>
    {
        [60] = 20, [61] = 30, [15] = 50, [3] = 60, [16] = 70, [4] = 80, [5] = 100, [19] = 120, [6] = 150,
    };

    /// <summary>Corner severity (DR2 1..6 scale, 6 = fastest) from the subcall angle.</summary>
    public static string? Severity(int angle) => angle switch
    {
        >= 14 and <= 16 => "6",
        >= 20 and <= 30 => "5",
        >= 40 and <= 45 => "4",
        >= 50 and <= 55 => "3",
        >= 60 and <= 70 => "2",
        >= 75 and <= 85 => "1",
        >= 86 and <= 95 => "square",
        >= 96 and <= 165 => "hairpin",
        _ => null,
    };

    public static List<Dictionary<string, object?>> Normalize(Pacenote call, int index)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var (s, i) in call.Subcalls.Select((s, i) => (s, i)))
        {
            if (s.Type == 0)
            {
                continue;
            }

            var token = new Dictionary<string, object?>
            {
                ["call"] = index,
                ["subcall"] = i,
                ["distance_m"] = call.Distance,
                ["delay_ms"] = s.Delay,
            };
            switch (s.Type)
            {
                case 1 or 2:
                    token["kind"] = "corner";
                    token["direction"] = s.Type == 1 ? "left" : "right";
                    token["severity"] = Severity(s.Angle);
                    token["angle"] = s.Angle;
                    break;
                case 3:
                    token["kind"] = "distance";
                    token["distance_call_m"] = DistanceCodes.TryGetValue(s.DistanceLink, out var d) ? d : null;
                    break;
                case 6:
                    token["kind"] = "finish";
                    break;
                default:
                    token["kind"] = "unknown";
                    token["type"] = s.Type;
                    break;
            }

            var modifiers = new List<string>();
            if (s.ModifierA != 0)
            {
                modifiers.Add(s.ModifierA == 3 ? "dont_cut" : $"modifier_a_{s.ModifierA}");
            }

            if (s.ModifierB != 0)
            {
                modifiers.Add($"modifier_b_{s.ModifierB}");
            }

            token["modifiers"] = modifiers;
            if (s.Type != 3 && s.DistanceLink != 0)
            {
                if (DistanceCodes.TryGetValue(s.DistanceLink, out var after))
                {
                    token["then_distance_m"] = after;
                }
                else
                {
                    token["link_code"] = s.DistanceLink;
                }
            }
            else if (s.Type == 3 && !DistanceCodes.ContainsKey(s.DistanceLink))
            {
                token["link_code"] = s.DistanceLink;
            }

            result.Add(token);
        }

        return result;
    }
}
