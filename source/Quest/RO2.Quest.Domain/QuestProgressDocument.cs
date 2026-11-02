using System.Text;
using System.Text.Json;

namespace RO2.Quests.Domain;

/// <summary>
/// The ProgressJson codec of a held quest: the literal <c>{"kill":[u16...],"met":[u16...]}</c>, one entry per objective
/// row in Quest_Info order. Byte-critical: persistence never re-serialises, so a relogged mid-quest player regresses on
/// any drift. The <c>"met"</c> key is ABSENT in a document written before it existed (persisted state unknown).
/// </summary>
public static class QuestProgressDocument
{
    private const string KillKey = "kill";

    /// <summary>The talk / use-item closes of each objective row.</summary>
    private const string MetKey = "met";

    /// <summary><c>{"kill":[...]}</c>: the kill counts, one per objective row (0 for a row that is not a kill row). This is
    /// the form documents had before the <c>"met"</c> key.</summary>
    public static string Encode(ReadOnlySpan<ushort> killCounts)
    {
        var sb = new StringBuilder("{");
        AppendArray(sb, KillKey, killCounts);
        return sb.Append('}').ToString();
    }

    /// <summary><c>{"kill":[...],"met":[...]}</c>: the kill counts and the talk / use-item closes of each objective row.
    /// The <c>"met"</c> key marks the document as carrying the quest's run state.</summary>
    public static string EncodeState(ReadOnlySpan<ushort> killCounts, ReadOnlySpan<ushort> metValues)
    {
        var sb = new StringBuilder("{");
        AppendArray(sb, KillKey, killCounts);
        sb.Append(',');
        AppendArray(sb, MetKey, metValues);
        return sb.Append('}').ToString();
    }

    private static void AppendArray(StringBuilder sb, string key, ReadOnlySpan<ushort> values)
    {
        sb.Append('"').Append(key).Append("\":[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(values[i]);
        }
        sb.Append(']');
    }

    /// <summary>
    /// Reads the per-row kill counts from a stored document, as <paramref name="rowCount"/> entries. Missing or malformed
    /// entries read as 0, and counts are capped at 65,535.
    /// </summary>
    public static ushort[] Decode(string? json, int rowCount) => DecodeArray(json, KillKey, rowCount, out _);

    /// <summary>Reads the per-row closes stored under <c>"met"</c>. All 0 when the key is missing.</summary>
    public static ushort[] DecodeMet(string? json, int rowCount) => DecodeArray(json, MetKey, rowCount, out _);

    /// <summary>Whether the document carries the <c>"met"</c> key, that is, was written with the quest's run state.</summary>
    public static bool HasMetKey(string? json)
    {
        DecodeArray(json, MetKey, 0, out var found);
        return found;
    }

    private static ushort[] DecodeArray(string? json, string key, int rowCount, out bool found)
    {
        found = false;
        var counts = new ushort[Math.Max(0, rowCount)];
        if (string.IsNullOrEmpty(json)) return counts;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var array)
                || array.ValueKind != JsonValueKind.Array)
                return counts;
            found = true;
            int i = 0;
            foreach (var value in array.EnumerateArray())
            {
                if (i >= counts.Length) break;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
                    counts[i] = (ushort)Math.Clamp(n, 0, ushort.MaxValue);
                i++;
            }
        }
        catch (JsonException)
        {
            // Not ours: read as no progress.
        }
        return counts;
    }
}
