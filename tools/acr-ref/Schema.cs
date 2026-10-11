using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;

namespace AcrRef;

/// Prints the unversioned serialization schema of usmap types: own properties first, then each super's,
/// which is the index order used by FUnversionedHeader.
internal static class Schema
{
    public static int Run(Options o)
    {
        var mappings = new FileUsmapTypeMappingsProvider(o.Usmap, StringComparer.OrdinalIgnoreCase).MappingsForGame!;
        foreach (var name in o.Positional)
        {
            if (!mappings.Types.TryGetValue(name, out var s)) { Console.WriteLine($"{name}: not in mappings"); continue; }
            Console.WriteLine($"== {name}");
            int baseIndex = 0;
            for (var cur = s; cur is not null; cur = cur.Super?.Value)
            {
                foreach (var (index, p) in cur.Properties.OrderBy(kv => kv.Key))
                    Console.WriteLine($"  {baseIndex + index,4}  {cur.Name}.{p.Name}{(p.ArraySize > 1 ? $"[{p.ArraySize}]" : "")} : {Describe(p.MappingType)}");
                baseIndex += cur.PropertyCount;
            }
        }
        return 0;
    }

    static string Describe(PropertyType t) => t.Type switch
    {
        "StructProperty" => $"Struct<{t.StructType}>",
        "ArrayProperty" or "SetProperty" => $"{t.Type[..^8]}<{Describe(t.InnerType)}>",
        "MapProperty" => $"Map<{Describe(t.InnerType)}, {Describe(t.ValueType)}>",
        "EnumProperty" => $"Enum<{t.EnumName}, {(t.InnerType is null ? "?" : Describe(t.InnerType))}>",
        "ByteProperty" when t.EnumName is not null => $"ByteEnum<{t.EnumName}>",
        _ => t.Type,
    };
}
