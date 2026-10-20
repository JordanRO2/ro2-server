namespace RO2.Domain.Entities;

/// <summary>
/// One close-range chat room signal passed between the Zone and Chat servers: which room (the GUID the
/// Zone's 2762 gave out), which character it concerns and, per kind, what else the other side needs. A
/// field a kind does not use keeps its default.
/// </summary>
public sealed class CloseRangeRoomSignal
{
 /// <summary>Insertion-order key within the signal channel. Opaque: only its ordinal order is meaningful (the
 /// store writes the 24-char lowercase hex of the Mongo ObjectId, whose ordinal order equals ObjectId order).
 /// The subscriber resumes after the last one seen.</summary>
    public string Id { get; init; } = "";

 /// <summary>What happened: <see cref="CloseRangeRoomSignals.OpenKind"/> (Zone to Chat),
 /// <see cref="CloseRangeRoomSignals.JoinKind"/> or <see cref="CloseRangeRoomSignals.ExitKind"/> (Chat to Zone).</summary>
    public string Kind { get; init; } = CloseRangeRoomSignals.ExitKind;

 /// <summary>The 16-byte room GUID, as 2762 +115 handed it out and 23020 / 23022 carry it back.</summary>
    public byte[] RoomGuid { get; init; } = [];

 /// <summary>The character (Character.Id) the signal concerns: for an open, the room's owner; for a join,
 /// the joiner; for an exit, the one who left.</summary>
    public uint CharacterId { get; init; }

 /// <summary>Open only: the room's capacity, the 1533 flag byte (10 in 27/27 captured requests), which
 /// the Chat server's 23517 carries as its u32 capacity (10 in 4/4 dump joins).</summary>
    public byte Capacity { get; init; }

 /// <summary>Open only: the room's password as the 1533 carried it (UTF-16LE, no terminator; empty for
 /// every captured room), which a 23020 must match.</summary>
    public byte[] Password { get; init; } = [];

 /// <summary>Join and exit: the room's member count after the change, owner included, as the Chat
 /// server's member list stands (what 2770 blob+13 carries); 0 when the Chat server does not hold the
 /// room's member list.</summary>
    public int MemberCount { get; init; }

 /// <summary>Exit only: the character (Character.Id) the Chat server made the room's master because the leaver
 /// was its master and members remained, the one the Zone's 2766 NfyNewMasterCloseRangeChatRoom hangs the sign
 /// on; 0 when the room has no new master (a member left, or the master left an empty room).</summary>
    public uint NewMasterCharacterId { get; init; }

 /// <summary>Exit only: the leaver was the master and did not send a 23022, its Chat connection closed. Retail's
 /// one captured case (the master's 2505 OnPC_Out, then 2766 + 2770 with no 2765) says the old master's sign is
 /// not taken down then, as its actor is already gone.</summary>
    public bool OwnerGone { get; init; }

 /// <summary>When it was written (UTC). The Zone ignores signals older than its own start.</summary>
    public DateTime CreatedUtc { get; init; }
}

/// <summary>
/// What the channel holds about one room: its latest open signal and, in insertion order, the signals written
/// after it that concern the room (its joins and exits, and the opens by each owner it has had).
/// </summary>
public sealed record CloseRangeRoomHistory(CloseRangeRoomSignal Open, IReadOnlyList<CloseRangeRoomSignal> Later);
