using System.Text.Json;

namespace RO2.Inventory.Domain;

/// <summary>One honed grinding line: the <c>Grinding_Abili.Abili_ID</c> (stat*1000+tier) and the signed roll
/// in basis points against that tier's Normal_Val. The drawn value the client shows is
/// <c>ceil(Normal_Val * (10000 + Roll) / 10000)</c>.</summary>
public readonly record struct GrindingLine(ushort AbiliId, short Roll)
{
    /// <summary>Stat index (Abili_ID / 1000).</summary>
    public int Stat => AbiliId / 1000;
    /// <summary>Grinding tier (Abili_ID % 1000).</summary>
    public int Tier => AbiliId % 1000;
}

/// <summary>
/// Reads and writes an item's honed grinding lines, stored on <see cref="InventoryItem.GrindingJson"/> as a flat
/// JSON int array <c>[aid0, roll0, aid1, roll1, ...]</c> (2 ints per line, up to 5 lines; a honed item usually
/// carries 4-5 lines, see the honing roll). Backs the grinding block at item record +0x4B (5 slots of
/// {u16 Abili_ID, i16 roll}). Positions 0-1 are the PREFIX pair, 2-4 the BONUS triple. Mirrors the
/// <see cref="ItemSockets"/> parse/serialize shape so it threads through the same per-instance code paths.
/// </summary>
public static class GrindingLines
{
    /// <summary>Maximum lines a record holds.</summary>
    public const int MaxLines = 5;

    /// <summary>Parses the stored lines (empty when null/blank/malformed).</summary>
    public static IReadOnlyList<GrindingLine> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return System.Array.Empty<GrindingLine>();
        try
        {
            var flat = JsonSerializer.Deserialize<int[]>(json);
            if (flat is null || flat.Length < 2) return System.Array.Empty<GrindingLine>();
            var lines = new List<GrindingLine>(MaxLines);
            for (int i = 0; i + 1 < flat.Length && lines.Count < MaxLines; i += 2)
            {
                int aid = flat[i];
                if (aid <= 0 || aid > ushort.MaxValue) continue;
                lines.Add(new GrindingLine((ushort)aid, (short)flat[i + 1]));
            }
            return lines;
        }
        catch (JsonException)
        {
            return System.Array.Empty<GrindingLine>();
        }
    }

    /// <summary>Serializes lines back to the flat JSON array, or null when there are none.</summary>
    public static string? Serialize(IReadOnlyList<GrindingLine> lines)
    {
        if (lines is null || lines.Count == 0) return null;
        var flat = new int[Math.Min(lines.Count, MaxLines) * 2];
        for (int i = 0; i < flat.Length / 2; i++)
        {
            flat[i * 2] = lines[i].AbiliId;
            flat[i * 2 + 1] = lines[i].Roll;
        }
        return JsonSerializer.Serialize(flat);
    }
}
