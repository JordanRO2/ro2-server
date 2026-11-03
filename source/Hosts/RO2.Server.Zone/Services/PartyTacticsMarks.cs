using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RO2.Network.Packets;
using RO2.Characters.Domain;
using RO2.TacticsMarks.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The party's tactics marks: each mark a member has set on a target with RMI 1529 (ReqPartyTacticsTarget).
/// The list is sent in RMI 2751 (AnsPartyTacticsTarget) when a member finishes loading a map.
/// </summary>
public static class PartyTacticsMarks
{
    private static readonly ConditionalWeakTable<PartyState, TacticsMarkList> Marks = new();

 /// <summary>Character UID mapped to the time of its last accepted channel move.</summary>
    private static readonly ConcurrentDictionary<ulong, DateTime> ChannelMoves = new();

 /// <summary>
 /// Records a mark on a target with the numbered flag (the caller reads it from the loaded Tactics_Target table
 /// through <see cref="TacticsMarkList.IsNumbered"/>) and the time supplied.
 /// </summary>
    internal static void Set(PartyState party, uint targetHandle, ushort mark, bool numbered, DateTime setAtUtc)
        => Marks.GetValue(party, _ => new TacticsMarkList()).Set(targetHandle, mark, numbered, setAtUtc);

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
        var entries = list.Snapshot(ChannelMoves.TryGetValue(loaderCharacterUid, out var movedAt) ? movedAt : null);
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
