using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// The channel between the Zone and Chat servers for close-range ("local") chat rooms: the Zone publishes the
/// rooms it opens and tails the joins and exits; the Chat server publishes joins and exits and reads the Zone's
/// open signal of a room when a player asks to join it. Cross-process: both hosts resolve it over the same
/// database, so an implementation must deliver in insertion order across processes.
/// </summary>
public interface ICloseRangeRoomSignals
{
 /// <summary>Zone side: the room <paramref name="ownerCharacterId"/> just opened (1533 -> 2762), so the Chat
 /// server can answer a 23020 for it.</summary>
    Task PublishOpenAsync(ReadOnlyMemory<byte> roomGuid, uint ownerCharacterId, byte capacity,
        ReadOnlyMemory<byte> password, CancellationToken ct = default);

 /// <summary>Chat side: <paramref name="characterId"/> joined room <paramref name="roomGuid"/>, which now has
 /// <paramref name="memberCount"/> members.</summary>
    Task PublishJoinAsync(ReadOnlyMemory<byte> roomGuid, uint characterId, int memberCount,
        CancellationToken ct = default);

 /// <summary>Chat side: <paramref name="characterId"/> left room <paramref name="roomGuid"/>, which keeps
 /// <paramref name="memberCount"/> members (0 when the Chat server does not hold its member list, or when the
 /// leaver was its owner and the room closed). <paramref name="newMasterCharacterId"/> is the member made master
 /// when the owner left a room others are still in (0 for none); <paramref name="ownerGone"/> says the owner left
 /// by closing its Chat connection rather than with a 23022.</summary>
    Task PublishExitAsync(ReadOnlyMemory<byte> roomGuid, uint characterId, int memberCount,
        uint newMasterCharacterId, bool ownerGone, CancellationToken ct = default);

 /// <summary>Chat side: the Zone's open signal of the room while it is still open, with
 /// <see cref="CloseRangeRoomSignal.CharacterId"/> set to the room's owner NOW (the opener, or the member a
 /// master hand-off made owner since), or null when it is closed or there is no open signal.</summary>
    Task<CloseRangeRoomSignal?> FindOpenRoomAsync(ReadOnlyMemory<byte> roomGuid, CancellationToken ct = default);

 /// <summary>Chat side: the room's latest open signal and the signals after it that concern the room (joins,
 /// exits, and the opens by each owner it has had), in insertion order; null when there is no open signal.
 /// Unlike <see cref="FindOpenRoomAsync"/> it cannot hide "no open signal" behind "closed".</summary>
    Task<CloseRangeRoomHistory?> GetRoomHistoryAsync(ReadOnlyMemory<byte> roomGuid, CancellationToken ct = default);

 /// <summary>Zone side: calls <paramref name="onSignal"/> for every signal written at or after
 /// <paramref name="fromUtc"/>, in insertion order, until <paramref name="ct"/> is cancelled. When the
 /// underlying cursor dies the subscription re-opens after a short delay, resuming after the last signal it
 /// delivered; an exception, from the store or from <paramref name="onSignal"/>, goes to
 /// <paramref name="onError"/> first. A signal whose handler threw is not delivered again.</summary>
    Task SubscribeAsync(DateTime fromUtc, Func<CloseRangeRoomSignal, Task> onSignal, Action<Exception> onError,
        CancellationToken ct);
}
