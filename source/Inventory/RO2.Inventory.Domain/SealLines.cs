using System.Text.Json;

namespace RO2.Inventory.Domain;

/// <summary>One imprinted guardian-seal stat line: a trait <see cref="Kind"/> (1 flat / 2 percent), the trait
/// <see cref="Type"/> (same enumeration as TraitInfo.Trait_Type: 0-5 primaries, 23 AP, 25 MP, secondaries, …) and a
/// SIGNED <see cref="Value"/>. Unlike a honed grinding line this is an absolute value, not a basis-point roll, and it
/// can be negative (a seal roll may be a penalty). The value does not depend on the accessory it is imprinted on.</summary>
public readonly record struct SealLine(byte Kind, byte Type, int Value);

/// <summary>
/// Reads and writes an accessory's imprinted seal lines, stored on <see cref="InventoryItem.SealJson"/> as a flat
/// JSON int array <c>[kind0, type0, value0, kind1, type1, value1, …]</c> (3 ints per line). Mirrors
/// <see cref="GrindingLines"/>/<see cref="ItemSockets"/> so it threads through the same per-instance paths.
/// </summary>
public static class SealLines
{
    /// <summary>Maximum seal lines an accessory holds (one seal slot in b303; kept small for forward room).</summary>
    public const int MaxLines = 2;

    /// <summary>Parses the stored lines (empty when null/blank/malformed).</summary>
    public static IReadOnlyList<SealLine> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return System.Array.Empty<SealLine>();
        try
        {
            var flat = JsonSerializer.Deserialize<int[]>(json);
            if (flat is null || flat.Length < 3) return System.Array.Empty<SealLine>();
            var lines = new List<SealLine>(MaxLines);
            for (int i = 0; i + 2 < flat.Length && lines.Count < MaxLines; i += 3)
            {
                int kind = flat[i], type = flat[i + 1];
                if (kind is < 0 or > byte.MaxValue || type is < 0 or > byte.MaxValue) continue;
                lines.Add(new SealLine((byte)kind, (byte)type, flat[i + 2]));
            }
            return lines;
        }
        catch (JsonException)
        {
            return System.Array.Empty<SealLine>();
        }
    }

    /// <summary>Serializes lines back to the flat JSON array, or null when there are none.</summary>
    public static string? Serialize(IReadOnlyList<SealLine> lines)
    {
        if (lines is null || lines.Count == 0) return null;
        int n = System.Math.Min(lines.Count, MaxLines);
        var flat = new int[n * 3];
        for (int i = 0; i < n; i++)
        {
            flat[i * 3] = lines[i].Kind;
            flat[i * 3 + 1] = lines[i].Type;
            flat[i * 3 + 2] = lines[i].Value;
        }
        return JsonSerializer.Serialize(flat);
    }
}
