using System.Collections.Concurrent;

namespace RO2.Server.Zone.Services;

/// <summary>An open close-range chat room.</summary>
/// <param name="Id">The room GUID.</param>
/// <param name="OwnerCharacterId">Character id of the room owner. It changes when ownership passes to another member.</param>
/// <param name="OwnerHandle">Actor handle the room sign is attached to, or 0 when the new owner is not on this Zone.</param>
/// <param name="Capacity">Maximum number of members.</param>
/// <param name="NameBuffer">Room name as a zero-padded UTF-16 buffer. Null only for rooms built without one.</param>
/// <param name="HasPassword">Whether the room is protected by a password.</param>
public sealed record CloseRangeRoom(Guid Id, uint OwnerCharacterId, uint OwnerHandle, byte Capacity,
    byte[]? NameBuffer = null, bool HasPassword = false);

/// <summary>The owner left its room and no other members remain, so the room sign is removed.</summary>
public sealed record CloseRangeOwnerExited(CloseRangeRoom Room);

/// <summary>
/// The owner left a room that still has members, and the chat server made another member the owner. The room keeps its identity under the new owner.
/// </summary>
/// <param name="OldRoom">The room before the hand-off.</param>
/// <param name="NewRoom">The room as it is now in the registry.</param>
public sealed record CloseRangeMasterHandedOff(CloseRangeRoom OldRoom, CloseRangeRoom NewRoom);

/// <summary>A member left the room, so the member count is reduced.</summary>
public sealed record CloseRangeMemberExited(CloseRangeRoom Room, uint CharacterId);

/// <summary>No open room has the given id. It was opened before this Zone started or is already closed.</summary>
public sealed record CloseRangeUnknownRoom(Guid Id);

/// <summary>
/// The outcome of a chat room exit. The possible outcomes are the owner leaving an empty room, a member leaving, or a master hand-off.
/// </summary>
public union CloseRangeExitOutcome(CloseRangeOwnerExited, CloseRangeMemberExited, CloseRangeMasterHandedOff,
    CloseRangeUnknownRoom);

/// <summary>
/// Registry of open close-range chat rooms, keyed by room id. The registry is process-wide and is not registered in DI.
/// </summary>
/// <remarks>
/// Rooms exist only while this process runs. A room whose owner leaves while members remain passes to a member chosen by the chat server. Each character owns at most one room at a time; a new room from a later login replaces an old one left behind by an earlier login.
/// </remarks>
public static class CloseRangeChatRooms
{
    private static readonly ConcurrentDictionary<Guid, CloseRangeRoom> Rooms = new();

 /// <summary>Returns the room id for a 16-byte wire identifier.</summary>
    public static Guid IdOf(ReadOnlySpan<byte> roomGuid) => new(roomGuid);

 /// <summary>Records a newly opened room. It replaces any earlier room of the same owner.</summary>
    public static void Register(CloseRangeRoom room)
    {
        foreach (var (id, other) in Rooms)
        {
            if (other.OwnerCharacterId == room.OwnerCharacterId && id != room.Id)
                Rooms.TryRemove(id, out _);
        }
        Rooms[room.Id] = room;
    }

 /// <summary>Returns the open room with the given 16-byte id, or null.</summary>
    public static CloseRangeRoom? Find(ReadOnlySpan<byte> roomGuid)
        => roomGuid.Length == 16 && Rooms.TryGetValue(IdOf(roomGuid), out var room) ? room : null;

 /// <summary>
 /// Returns the open room that the given character owns with its sign on the given actor, or null when there is none. A character can own only one room at a time.
 /// </summary>
    public static CloseRangeRoom? FindOwnedBy(uint characterId, uint handle)
    {
        foreach (var room in Rooms.Values)
        {
            if (room.OwnerCharacterId == characterId && room.OwnerHandle == handle)
                return room;
        }
        return null;
    }

 /// <summary>
 /// Resolves a member's exit from a room, or the owner's exit when the room has no new master. The owner's exit closes the room.
 /// </summary>
    public static CloseRangeExitOutcome ResolveExit(ReadOnlySpan<byte> roomGuid, uint characterId)
        => ResolveExit(roomGuid, characterId, newMasterCharacterId: 0, newMasterHandle: 0);

 /// <summary>
 /// Resolves an exit from a room. When the owner leaves with a new master, the room stays open under that member. Otherwise the owner's exit closes the room and removes it from the registry. Each exit is applied only once, even when two signals race.
 /// </summary>
    public static CloseRangeExitOutcome ResolveExit(ReadOnlySpan<byte> roomGuid, uint characterId,
        uint newMasterCharacterId, uint newMasterHandle)
    {
        if (roomGuid.Length != 16)
            return new CloseRangeUnknownRoom(Guid.Empty);
        var id = IdOf(roomGuid);
        if (!Rooms.TryGetValue(id, out var room))
            return new CloseRangeUnknownRoom(id);
        if (room.OwnerCharacterId != characterId)
            return new CloseRangeMemberExited(room, characterId);
        if (newMasterCharacterId != 0 && newMasterCharacterId != characterId)
        {
            var handedOff = room with { OwnerCharacterId = newMasterCharacterId, OwnerHandle = newMasterHandle };
            return Rooms.TryUpdate(id, handedOff, room)
                ? new CloseRangeMasterHandedOff(room, handedOff)
                : new CloseRangeUnknownRoom(id);
        }
        return Rooms.TryRemove(new KeyValuePair<Guid, CloseRangeRoom>(id, room))
            ? new CloseRangeOwnerExited(room)
            : new CloseRangeUnknownRoom(id);
    }
}
