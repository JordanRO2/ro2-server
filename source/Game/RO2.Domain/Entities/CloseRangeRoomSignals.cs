namespace RO2.Domain.Entities;

/// <summary>
/// The kinds of <see cref="CloseRangeRoomSignal"/> and the rule for who owns a room after a run of them.
/// The channel itself is <c>ICloseRangeRoomSignals</c> (RO2.Application).
/// </summary>
public static class CloseRangeRoomSignals
{
 /// <summary><see cref="CloseRangeRoomSignal.Kind"/> of an exit (23022 ReqExitCloseRangeChatRoom).</summary>
    public const string ExitKind = "exit";

 /// <summary><see cref="CloseRangeRoomSignal.Kind"/> of a room the Zone opened (1533 -> 2762).</summary>
    public const string OpenKind = "open";

 /// <summary><see cref="CloseRangeRoomSignal.Kind"/> of a join the Chat server accepted (23020 -> 23538 0).</summary>
    public const string JoinKind = "join";

 /// <summary>
 /// Who owns the room of <paramref name="open"/> after the signals <paramref name="later"/> (insertion order;
 /// others are skipped), or null once it is closed. Walking them with the owner at each point: an exit of this
 /// room by the owner with a <see cref="CloseRangeRoomSignal.NewMasterCharacterId"/> hands it to that member, the
 /// room staying open (retail keeps the GUID across a hand-off); one without closes it; and an open of any room by the owner closes it too, since the
 /// Zone keeps one room per owner (CloseRangeChatRooms.Register) as the client keeps one sign per actor. An exit
 /// or open by anyone who is not the owner at that moment changes nothing: the old master rejoining and leaving
 /// as a member does not close the new master's room.
 /// </summary>
    public static uint? CurrentOwner(CloseRangeRoomSignal open, IEnumerable<CloseRangeRoomSignal> later)
    {
        uint owner = open.CharacterId;
        foreach (var s in later)
        {
            if (string.CompareOrdinal(s.Id, open.Id) <= 0 || s.CharacterId != owner) continue;
            if (s.Kind == OpenKind) return null;
            if (s.Kind != ExitKind || !s.RoomGuid.AsSpan().SequenceEqual(open.RoomGuid)) continue;
            if (s.NewMasterCharacterId == 0) return null;
            owner = s.NewMasterCharacterId;
        }
        return owner;
    }
}
