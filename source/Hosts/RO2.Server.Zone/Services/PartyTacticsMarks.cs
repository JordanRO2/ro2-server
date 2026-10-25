using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The party's tactics marks: each mark a member has set on a target with RMI 1529 (ReqPartyTacticsTarget).
/// The list is sent in RMI 2751 (AnsPartyTacticsTarget) when a member finishes loading a map.
/// </summary>
public static class PartyTacticsMarks
{
 /// <summary>One mark on the party's list, with the time it was set.</summary>
    internal readonly record struct Entry(ushort Mark, uint Handle, DateTime SetAtUtc);

    private static readonly ConditionalWeakTable<PartyState, List<Entry>> Marks = new();

 /// <summary>Character UID mapped to the time of its last accepted channel move.</summary>
    private static readonly ConcurrentDictionary<ulong, DateTime> ChannelMoves = new();

 /// <summary>
 /// Records a mark on a target. A numbered mark already on the list moves to the new target; the target
 /// leaves any other entry.
 /// </summary>
    public static void Set(PartyState party, uint targetHandle, ushort mark)
        => Set(party, targetHandle, mark, IsNumbered(GameDataStore.Loaded?.TacticsTarget, mark), DateTime.UtcNow);

 /// <summary>Records a mark with the numbered flag and the time supplied.</summary>
    internal static void Set(PartyState party, uint targetHandle, ushort mark, bool numbered, DateTime setAtUtc)
    {
        var list = Marks.GetValue(party, _ => new List<Entry>());
        lock (list)
        {
            int slot = numbered ? list.FindIndex(e => e.Mark == mark) : -1;
            if (slot >= 0)
            {
                list[slot] = new Entry(mark, targetHandle, setAtUtc);
                for (int i = list.Count - 1; i >= 0; i--)
                    if (i != slot && list[i].Handle == targetHandle)
                        list.RemoveAt(i);
                return;
            }
            list.RemoveAll(e => e.Handle == targetHandle);
            if (list.Count < byte.MaxValue) 
                list.Add(new Entry(mark, targetHandle, setAtUtc));
        }
    }

 /// <summary>Whether a mark is numbered, that is, its tactics target icon carries a number. False when there is no table.</summary>
    internal static bool IsNumbered(IReadOnlyList<TacticsTarget>? rows, ushort mark)
    {
        if (rows is null || mark == 0) return false;
        foreach (var row in rows)
        {
            if (row.Tactics_List != mark) continue;
            var file = row.Route.Replace('\\', '/');
            file = file[(file.LastIndexOf('/') + 1)..];
            const string prefix = "Tactic_icon_", suffix = ".nif";
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            var number = file[prefix.Length..^suffix.Length];
            return number.Length > 0 && number.All(char.IsAsciiDigit);
        }
        return false;
    }

 /// <summary>
 /// Records that a character has changed channel. Its later map-load lists omit the marks set before the move.
 /// </summary>
    public static void NoteChannelMove(ulong characterUid) => NoteChannelMove(characterUid, DateTime.UtcNow);

 /// <summary>Records a channel move at the given time.</summary>
    internal static void NoteChannelMove(ulong characterUid, DateTime movedAtUtc)
    {
        if (characterUid != 0) ChannelMoves[characterUid] = movedAtUtc;
    }

 /// <summary>
 /// Builds a 2751 (AnsPartyTacticsTarget) answer for a character that has just loaded a map. It lists the
 /// party's marks set since that character's last channel move, in list order.
 /// </summary>
    public static byte[]? BuildMapEntry2751(PartyState party, uint mapId, ulong loaderCharacterUid)
    {
        if (!Marks.TryGetValue(party, out var list)) return null;
        Entry[] entries;
        lock (list) entries = list.ToArray();
        if (ChannelMoves.TryGetValue(loaderCharacterUid, out var movedAt))
            entries = Array.FindAll(entries, e => e.SetAtUtc > movedAt);
        if (entries.Length == 0) return null;

        using var pw = new PacketWriter(15 + 6 * entries.Length);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(PartyAnswerPackets.MsgTagAnsPartyTacticsTarget);
            p.WriteUInt32(0);
            p.WriteUInt32(mapId);
            p.WriteByte((byte)entries.Length);
            foreach (var e in entries)
            {
                p.WriteUInt16(e.Mark);
                p.WriteUInt32(e.Handle);
            }
        });
        return pw.ToArray();
    }
}
