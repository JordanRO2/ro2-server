using System.Buffers.Binary;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Throttling;
using RO2.Characters.Domain;

namespace RO2.Server.Chat;

/// <summary>
/// Whether a 23022 ReqExitCloseRangeChatRoom is accepted (23541 result 0) or refused (17) when this process holds
/// no member list for the room, and the 23541 body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refusal case.</b> A repeated exit for a room the character already left is answered
/// <c>1100</c> = 17, which the client shows as string_ui 9521 "Option only available in channels you are
/// participating.". A lone owner's first exit closes the room, so the duplicate is refused.
/// </para>
/// <para>
/// <b>Why the member list is not enough.</b> CloseRangeChatRoomMembers learns a room only at its first join, so a room
/// its owner leaves alone is unknown here on BOTH exits (LeaveOutcome.UnknownRoom). The difference between the two is
/// in the Zone/Chat signal channel (<see cref="CloseRangeRoomSignals"/>): the open signal (1533), the joins and exits
/// published since, and who owns the room now (<see cref="CloseRangeRoomSignals.CurrentOwner"/>). <see cref="Decide"/>
/// reads them: the current owner leaving is accepted; a member whose last signal is a JOIN is accepted (a member list
/// a Chat restart lost); a closed room or someone never in it is refused; no open signal at all (pushed out of the
/// capped collection) is accepted, since nothing tells the cases apart then.
/// </para>
/// <para>
/// <b>Recent exits.</b> A duplicate exit can arrive within a few hundred milliseconds of the first. The first exit's
/// signal is written after its 23541 is sent, so a duplicate can be handled before that write is visible, or even
/// while the first is still reading the signals. So each exit of a room unknown here first claims (room, character)
/// with <see cref="TryClaim"/>, and every accepted exit is kept by <see cref="RecordAccepted"/>, for
/// <see cref="RecentExitMs"/>: a second exit inside that window is refused without waiting on the database. The
/// window is a bound on memory, not a game value: the database decides once the signal is in, and a re-join after an
/// exit goes through the in-memory member list (CloseRangeChatRoomMembers), which is checked before this.
/// </para>
/// </remarks>
public static class CloseRangeExitCheck
{
    /// <summary>23541 result 0: left.</summary>
    public const ushort ResultLeft = 0;

    /// <summary>23541 result 17: string_ui 9521 "Option only available in channels you are participating.".
    /// The room is closed or the character was never in it.</summary>
    public const ushort ResultNotParticipating = 17;

    /// <summary>The 16-byte room GUID 23022 carries and 23541 echoes.</summary>
    public const int RoomGuidLength = 16;

    /// <summary>How long an accepted exit is remembered in process (see the remarks): far above the gap between a
    /// duplicate pair of exits, short enough that the set stays small.</summary>
    public const long RecentExitMs = 30_000;

    /// <summary>Exits per session per <see cref="UnknownRoomExitWindowMs"/> that this process may look up for a room it
    /// holds no member list for. A server-side bound, sized far above what an honest client sends (a duplicate
    /// pair of exits).</summary>
    public const int UnknownRoomExitLimit = 5;

    /// <summary>Window of <see cref="UnknownRoomExitLimit"/>.</summary>
    public const long UnknownRoomExitWindowMs = 10_000;

    private static readonly SlidingWindowRateLimiter<uint> UnknownRoomExits = new(UnknownRoomExitLimit, UnknownRoomExitWindowMs);

    /// <summary>True, and counted, when <paramref name="sessionId"/> may send another 23022 for a room unknown here;
    /// false when it must be dropped (a flood of random GUIDs; each one would cost a signal-collection read).</summary>
    public static bool TryAdmitUnknownRoomExit(uint sessionId) => UnknownRoomExits.TryAcquire(sessionId);

    /// <summary>What the signals say about an exit from a room this process holds no member list for.</summary>
    public enum Verdict
    {
        /// <summary>The leaver is the room's owner, or a member by its last signal: answer 0 and signal the exit.</summary>
        Accept,
        /// <summary>The room is closed, or the leaver was never in it: answer 17, no exit signal.</summary>
        Refuse,
        /// <summary>No open signal for the room: nothing to decide from; answer 0 but publish NO exit signal (a
        /// signal per random GUID would evict real open signals from the capped bus). Not a refusal: a legit exit whose
        /// open signal aged out must still get 0.</summary>
        NoRecord,
    }

    /// <summary>
    /// The 23541 body <c>[u16 result][16B room GUID]</c> (U16 followed by a 16-byte blob; for example
    /// <c>0000</c>+GUID for accepted and <c>1100</c>+GUID for refused).
    /// </summary>
    public static byte[] BuildAnswer(ushort result, ReadOnlySpan<byte> roomGuid)
    {
        if (roomGuid.Length != RoomGuidLength)
            throw new ArgumentException("A close-range room GUID is 16 bytes.", nameof(roomGuid));
        var body = new byte[2 + RoomGuidLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        roomGuid.CopyTo(body.AsSpan(2));
        return body;
    }

    /// <summary>
    /// The verdict for <paramref name="leaver"/> (Character.Id) leaving the room of <paramref name="open"/> (its
    /// latest open signal, null when there is none), given <paramref name="later"/>, the signals written after it that
    /// concern the room: its joins and exits, and the opens by anyone who owned it (as
    /// <see cref="ICloseRangeRoomSignals.FindOpenRoomAsync"/> gathers them), in insertion order.
    /// </summary>
    public static Verdict Decide(CloseRangeRoomSignal? open, IReadOnlyList<CloseRangeRoomSignal> later, uint leaver)
    {
        if (open is null) return Verdict.NoRecord;
        if (CloseRangeRoomSignals.CurrentOwner(open, later) is not uint owner) return Verdict.Refuse;
        if (owner == leaver) return Verdict.Accept;

        // A member: its last join or exit of this room after the open.
        string? last = null;
        foreach (var s in later)
        {
            if (string.CompareOrdinal(s.Id, open.Id) <= 0 || s.CharacterId != leaver || !s.RoomGuid.AsSpan().SequenceEqual(open.RoomGuid)) continue;
            if (s.Kind is CloseRangeRoomSignals.JoinKind or CloseRangeRoomSignals.ExitKind) last = s.Kind;
        }
        return last == CloseRangeRoomSignals.JoinKind ? Verdict.Accept : Verdict.Refuse;
    }

    /// <summary>
    /// <see cref="Decide"/> over the signal collection: the room's latest open signal, then its joins and exits after
    /// it and the opens by each owner it has had (the queries of <see cref="ICloseRangeRoomSignals.FindOpenRoomAsync"/>,
    /// which itself cannot tell "no open signal" from "closed").
    /// </summary>
    public static async Task<Verdict> ResolveAsync(ICloseRangeRoomSignals signals, ReadOnlyMemory<byte> roomGuid, uint leaver,
        CancellationToken ct = default)
    {
        var history = await signals.GetRoomHistoryAsync(roomGuid, ct);
        return history is null ? Verdict.NoRecord : Decide(history.Open, history.Later, leaver);
    }

    // ---------------------------------------------------------------- recent accepted exits (in process)

    private static readonly Lock Gate = new();
    private static readonly Dictionary<(Guid Room, uint CharacterId), long> Recent = new();

    /// <summary>Remembers that <paramref name="characterId"/>'s exit from <paramref name="roomGuid"/> was answered 0 at
    /// <paramref name="nowMs"/> (Environment.TickCount64).</summary>
    public static void RecordAccepted(ReadOnlySpan<byte> roomGuid, uint characterId, long nowMs)
    {
        if (roomGuid.Length != RoomGuidLength) return;
        lock (Gate)
        {
            SweepLocked(nowMs);
            Recent[(new Guid(roomGuid), characterId)] = nowMs;
        }
    }

    /// <summary>
    /// Claims <paramref name="characterId"/>'s exit from <paramref name="roomGuid"/> at <paramref name="nowMs"/>: false
    /// when an exit of the same (room, character) was claimed or accepted less than <see cref="RecentExitMs"/> before,
    /// which makes this one a duplicate; otherwise records it and returns true. Atomic, so of two exits handled at
    /// once only one gets past it.
    /// </summary>
    public static bool TryClaim(ReadOnlySpan<byte> roomGuid, uint characterId, long nowMs)
    {
        if (roomGuid.Length != RoomGuidLength) return true;
        var key = (new Guid(roomGuid), characterId);
        lock (Gate)
        {
            SweepLocked(nowMs);
            if (Recent.TryGetValue(key, out var at) && nowMs - at < RecentExitMs) return false;
            Recent[key] = nowMs;
            return true;
        }
    }

    /// <summary>Drops entries older than <see cref="RecentExitMs"/>; the caller holds <c>Gate</c>.</summary>
    private static void SweepLocked(long nowMs)
    {
        foreach (var key in Recent.Where(kv => nowMs - kv.Value >= RecentExitMs).Select(kv => kv.Key).ToList())
            Recent.Remove(key);
    }
}
