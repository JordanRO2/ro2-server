using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// A held quest's objective progress, kept across a relog. The kill counts and the talk / use-item closes are saved
/// in <see cref="CharacterQuest.ProgressJson"/>, written on every kill tick and every close, and restored into the
/// session counters at login. The same state is written into the 2310 quest vector, so the client shows it.
/// Collect and deliver progress is not stored: it is read from the bag at login.
/// </summary>
/// <remarks>
/// The 2310 quest vector element is <c>[u8 a][u8 b][u32 questId][u32 state][u32 0][20 * a bytes][20 * b bytes]</c>.
/// Each objective's progress is the u16 at <c>2 * i</c> of the a-array. The state is 1 when every objective is met
/// and 2 while one is not. Kill, talk and use-item closes are stored under the <c>"met"</c> key of the document.
/// Collect and deliver progress is derived from the bag. Session counters are u16, so kill targets above 255 count
/// correctly. A document written before the <c>"met"</c> key existed leaves the quest's run state unknown
/// (<see cref="PersistedStateKnown"/>), so its turn-in keeps the pay-as-before behaviour of QuestPrizeHandler.
/// </remarks>
internal static class QuestProgressStore
{
 /// <summary>Quest_Info Complete_Type of a kill objective (Complete_Type_Value1 = NPC, Value2 = count).</summary>
    internal const byte KillObjective = 2;

 /// <summary>Quest_Info Complete_Type of a row with no objective (the quest's non-objective rows); it is skipped.</summary>
    internal const byte NoObjective = 0;

 /// <summary>Element state of a quest with every objective met.</summary>
    internal const uint StateMet = 1;

 /// <summary>Element state of a quest still in progress.</summary>
    internal const uint StateRunning = 2;

 /// <summary>Bytes per objective sub-record of the 2310 a-array.</summary>
    internal const int SubRecordLength = 20;

 /// <summary>Element byte b, the count of second-array sub-records: always 0.</summary>
    private const byte SecondArrayCount = 0;

 /// <summary>Element dword at rec+8, always 0.</summary>
    private const uint ReservedWord = 0;

 /// <summary>Most uses a use-item row can need (the session counter is a byte).</summary>
    private const uint MaxUsesNeeded = 255;

 /// <summary>Per-objective <c>"met"</c> value and 2310 progress of a closed talk row.</summary>
    private const ushort TalkRowClosed = 1;

 /// <summary>
 /// Value of the session quest counter once the quest's objective is closed (talk and use-item quests, and a login-restored
 /// quest with every row met): the close has already been reported to the client.
 /// </summary>
    private const ushort ObjectiveClosedCounter = 1;

    private const string KillKey = "kill";

 /// <summary>The talk / use-item closes of each objective row (see the remarks).</summary>
    private const string MetKey = "met";

 /// <summary>Quest_Info Complete_Type of a talk objective (Complete_Type_Value1 = the NPC).</summary>
    internal const byte TalkObjective = 1;

 /// <summary>Quest_Info Complete_Type of a collect objective (Value1 = the item, Value2 = the count).</summary>
    internal const byte CollectObjective = 4;

 /// <summary>Quest_Info Complete_Type of a deliver objective (Value1 = the item, Value2 = the count).</summary>
    internal const byte DeliverObjective = 5;

 /// <summary>Quest_Info Complete_Type of a use-item objective (Value2 = the item, Value3 = the uses).</summary>
    internal const byte UseItemObjective = 6;

 /// <summary>The quests whose stored document carried the <c>"met"</c> key at this login's restore, per player.</summary>
    private static readonly ConditionalWeakTable<PlayerState, HashSet<uint>> KnownState = new();

 /// <summary>One write at a time per player, so concurrent saves cannot store their counts out of order.</summary>
    private static readonly ConditionalWeakTable<PlayerState, SemaphoreSlim> SaveGates = new();

 /// <summary>The quest's objective rows: the Quest_Info rows with Complete_Type != 0.</summary>
    internal static int ObjectiveRowCount(QuestData[] rows)
    {
        int n = 0;
        foreach (var row in rows)
            if (row.Complete_Type != NoObjective) n++;
        return n;
    }

 /// <summary>
 /// A kill row's needed total as the session counters hold it: Complete_Type_Value2, with 0 read as 1 and the value
 /// capped at the u16 maximum.
 /// </summary>
    internal static ushort NeededKills(QuestData row)
        => row.Complete_Type_Value2 == 0 ? (ushort)1 : (ushort)Math.Min(row.Complete_Type_Value2, ushort.MaxValue);

 /// <summary>A kill row that a kill can advance: Complete_Type 2 with a target (213010440 has none).</summary>
    internal static bool IsKillRow(QuestData row) => row.Complete_Type == KillObjective && row.Complete_Type_Value1 != 0;

 /// <summary><c>{"kill":[...]}</c>: the kill counts, one per objective row in Quest_Info order (0 for a row that is not
 /// a kill row). This is the form documents had before the <c>"met"</c> key.</summary>
    internal static string Encode(ReadOnlySpan<ushort> killCounts)
    {
        var sb = new StringBuilder("{");
        AppendArray(sb, KillKey, killCounts);
        return sb.Append('}').ToString();
    }

 /// <summary><c>{"kill":[...],"met":[...]}</c>: the kill counts and the talk / use-item closes of each objective row.
 /// The <c>"met"</c> key marks the document as carrying the quest's run state.</summary>
    internal static string EncodeState(ReadOnlySpan<ushort> killCounts, ReadOnlySpan<ushort> metValues)
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
    internal static ushort[] Decode(string? json, int rowCount) => DecodeArray(json, KillKey, rowCount, out _);

 /// <summary>Reads the per-row closes stored under <c>"met"</c>. All 0 when the key is missing.</summary>
    internal static ushort[] DecodeMet(string? json, int rowCount) => DecodeArray(json, MetKey, rowCount, out _);

 /// <summary>Whether the document carries the <c>"met"</c> key, that is, was written with the quest's run state.</summary>
    internal static bool HasMetKey(string? json)
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

 /// <summary>The player's current kill count on each objective row (0 for a row that is not a kill row).</summary>
    internal static ushort[] CurrentKillCounts(PlayerState player, uint questId, QuestData[] rows)
    {
        var counts = new ushort[ObjectiveRowCount(rows)];
        int objective = 0;
        for (int i = 0; i < rows.Length && objective < counts.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == NoObjective) continue;
            if (IsKillRow(row))
            {
                ushort have;
                if (i == 0) player.QuestKillProgress.TryGetValue(questId, out have);
                else have = LootService.ExtraKillCount(player, questId, row.Complete_Type_Value1);
                counts[objective] = have;
            }
            objective++;
        }
        return counts;
    }

 /// <summary>Uses a use-item row needs: Complete_Type_Value3, with 0 read as 1 and the value capped at 255.</summary>
    internal static ushort UsesNeeded(QuestData row) => (ushort)Math.Clamp(row.Complete_Type_Value3, 1u, MaxUsesNeeded);

 /// <summary>
 /// The player's talk and use-item closes on each objective row, as the <c>"met"</c> key stores them: 1 for a met talk
 /// row, the use count for a use-item row, and 0 for any other row.
 /// </summary>
    internal static ushort[] CurrentMetValues(PlayerState player, uint questId, QuestData[] rows)
    {
        var values = new ushort[ObjectiveRowCount(rows)];
        if (values.Length == 0) return values;
        player.QuestKillProgress.TryGetValue(questId, out var counter);
        bool closed = counter >= ObjectiveClosedCounter && rows[0].Complete_Type is TalkObjective or UseItemObjective;
        var metTalk = QuestTalkProgress.MetRows(player, questId);
        int objective = 0;
        for (int i = 0; i < rows.Length && objective < values.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == NoObjective) continue;
            if (row.Complete_Type == TalkObjective)
                values[objective] = closed || metTalk.Contains(i) ? TalkRowClosed : (ushort)0;
            else if (row.Complete_Type == UseItemObjective)
                values[objective] = (ushort)Math.Clamp(
                    Math.Max(QuestUseItemProgress.Count(player, questId, i), closed ? UsesNeeded(row) : 0), 0, ushort.MaxValue);
            objective++;
        }
        return values;
    }

 /// <summary>
 /// Stores the quest's kill counts and closes on its in-progress document, after each kill tick. An accept creates a
 /// fresh document, a give-up deletes it, and a completion closes it.
 /// </summary>
    internal static Task SaveKillProgressAsync(IQuestRepository repository, PlayerState player, uint questId,
        QuestData[] rows)
        => SaveStateAsync(repository, player, questId, rows);

 /// <summary>
 /// Stores the quest's run state on its in-progress document, after a close or a partial meet or use. A quest missing
 /// from Quest_Info is not stored. A failed write is logged, and the next close writes again.
 /// </summary>
    internal static async Task SaveObjectiveStateAsync(IQuestRepository repository, PlayerState player, uint questId,
        GameDataStore data, ILogger logger)
    {
        if (!data.QuestRows.TryGetValue(questId, out var rows)) return;
        try
        {
            await SaveStateAsync(repository, player, questId, rows);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name} quest {QuestId}: the objective state could not be stored", player.Name, questId);
        }
    }

    private static async Task SaveStateAsync(IQuestRepository repository, PlayerState player, uint questId, QuestData[] rows)
    {
        var gate = SaveGates.GetValue(player, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var json = EncodeState(CurrentKillCounts(player, questId, rows), CurrentMetValues(player, questId, rows));
            await repository.SaveProgressAsync(player.CharacterId, questId, json);
        }
        finally
        {
            gate.Release();
        }
    }

 /// <summary>
 /// Puts a held quest's stored kill counts back into the session counters at login, so the next kill counts on from
 /// what the 2310 showed. Quests missing from Quest_Info, or with no stored count, are left as they are.
 /// </summary>
    internal static void RestoreKillProgress(PlayerState player, CharacterQuest quest, GameDataStore data)
    {
        if (!data.QuestRows.TryGetValue(quest.QuestId, out var rows)) return;
        var counts = Decode(quest.ProgressJson, ObjectiveRowCount(rows));
        if (Array.TrueForAll(counts, c => c == 0)) return;
        LootService.RestoreKillProgress(player, quest.QuestId, rows, counts);
    }

 /// <summary>
 /// Restores a held quest's other objective progress into the session state at login, from what the 2310 shows for
 /// it. Call it after <see cref="RestoreKillProgress"/>.
 /// </summary>
 /// <remarks>
 /// A collect quest with an objective-close item that entered the bag outside the collect tracker loads closed, and its
 /// turn-in then pays no objective-close item.
 /// </remarks>
    internal static void RestoreObjectiveState(PlayerState player, CharacterQuest quest, GameDataStore data,
        IReadOnlyList<InventoryItem>? bag)
    {
        if (!data.QuestRows.TryGetValue(quest.QuestId, out var rows) || rows.Length == 0) return;
        var questId = quest.QuestId;
        if (HasMetKey(quest.ProgressJson))
        {
            var known = KnownState.GetOrCreateValue(player);
            lock (known) known.Add(questId);
        }

        var (values, allMet) = LoginProgress(rows, quest.ProgressJson, bag);
        var met = DecodeMet(quest.ProgressJson, values.Length);
        var talkRows = new List<int>();
        int objective = 0;
        for (int i = 0; i < rows.Length && objective < met.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == NoObjective) continue;
            if (row.Complete_Type == TalkObjective && met[objective] != 0)
                talkRows.Add(i);
            else if (row.Complete_Type == UseItemObjective && met[objective] != 0)
                QuestUseItemProgress.Restore(player, questId, rows.Length, i, Math.Min(met[objective], UsesNeeded(row)));
            objective++;
        }
        if (talkRows.Count > 0)
            QuestTalkProgress.Restore(player, questId, rows, talkRows);

        var first = rows[0];
        if (first.Complete_Type is CollectObjective or DeliverObjective)
        {
            if (first.Complete_Type_Value1 != 0 && values.Length > 0 && values[0] > 0)
                player.QuestKillProgress[questId] = values[0];
        }
        else if (first.Complete_Type is TalkObjective or UseItemObjective && allMet)
        {
            player.QuestKillProgress[questId] = ObjectiveClosedCounter;
        }
    }

 /// <summary>
 /// Whether the quest's run state from an earlier login is known. False for a quest accepted in this login, and for a
 /// document written before the <c>"met"</c> key existed.
 /// </summary>
    internal static bool PersistedStateKnown(PlayerState player, uint questId)
    {
        if (!KnownState.TryGetValue(player, out var known)) return false;
        lock (known) return known.Contains(questId);
    }

 /// <summary>
 /// What the 2310 shows for a held quest: each objective row's progress, and whether every objective row is met. Kill
 /// rows use their stored count. Collect and deliver rows use min(bag held, need). Talk rows are 1 when stored met. Use-item
 /// rows use their stored use count. Other row types are never met.
 /// </summary>
    internal static (ushort[] Values, bool AllMet) LoginProgress(QuestData[] rows, string? json,
        IReadOnlyList<InventoryItem>? bag)
    {
        var values = new ushort[ObjectiveRowCount(rows)];
        var kills = Decode(json, values.Length);
        var met = DecodeMet(json, values.Length);
        var allMet = values.Length > 0;
        int objective = 0;
        for (int i = 0; i < rows.Length && objective < values.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == NoObjective) continue;
            bool ok;
            switch (row.Complete_Type)
            {
                case KillObjective:
                    values[objective] = kills[objective];
                    ok = IsKillRow(row) && kills[objective] >= NeededKills(row);
                    break;
                case CollectObjective or DeliverObjective:
                    var need = NeededKills(row);
                    values[objective] = row.Complete_Type_Value1 != 0 && bag is not null
                        ? (ushort)Math.Min(QuestPrizeHandler.CountInBag(bag, row.Complete_Type_Value1), need)
                        : (ushort)0;
                    ok = row.Complete_Type_Value1 != 0 && values[objective] >= need;
                    break;
                case TalkObjective:
                    values[objective] = met[objective] != 0 ? TalkRowClosed : (ushort)0;
                    ok = values[objective] == TalkRowClosed;
                    break;
                case UseItemObjective:
                    values[objective] = Math.Min(met[objective], UsesNeeded(row));
                    ok = values[objective] >= UsesNeeded(row);
                    break;
                default:
                    ok = false;
                    break;
            }
            allMet &= ok;
            objective++;
        }
        return (values, allMet);
    }

 /// <summary>
 /// Writes one element of the 2310 quest vector: the objective row count, the quest id, the state, and each objective
 /// row's progress as a u16 in the a-array.
 /// </summary>
 /// <remarks>
 /// The state is 1 when every objective row is met, otherwise 2. <paramref name="bag"/> is the character's items; null
 /// counts nothing held. A quest missing from Quest_Info is written with one objective and no progress.
 /// </remarks>
    internal static void WriteLoginElement(PacketWriter writer, CharacterQuest quest, GameDataStore? data,
        IReadOnlyList<InventoryItem>? bag)
    {
        QuestData[]? rows = null;
        data?.QuestRows.TryGetValue(quest.QuestId, out rows);
        int objectives = rows is null ? 1 : Math.Max(1, ObjectiveRowCount(rows));
        var (values, met) = rows is null ? (Array.Empty<ushort>(), false) : LoginProgress(rows, quest.ProgressJson, bag);

        writer.WriteByte((byte)objectives);              // a: one 20-byte sub-record per objective row
        writer.WriteByte(SecondArrayCount);               // b: always 0
        writer.WriteUInt32(quest.QuestId);                // rec+0
        writer.WriteUInt32(met ? StateMet : StateRunning); // rec+4 (never 0: the client leaves such a quest unset)
        writer.WriteUInt32(ReservedWord);                 // rec+8, always 0
        for (int i = 0; i < objectives; i++)
            writer.WriteUInt16(i < values.Length ? values[i] : (ushort)0); // a-array byte 2i: objective i's progress
        writer.WriteZeros(objectives * (SubRecordLength - 2));
    }
}
