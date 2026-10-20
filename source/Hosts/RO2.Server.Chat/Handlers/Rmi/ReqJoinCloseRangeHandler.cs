using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Handlers;
using RO2.Network.Throttling;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// RMI 23020 ReqJoinCloseRangeChatRoom -> 23538 AnsJoinCloseRangeChatRoom (+ 23517 + 23516): a player joins
/// another player's close-range ("local") chat room by clicking the room's sign over the owner's head.
/// </summary>
/// <remarks>
/// <para>
/// Sender: the sign click, and for a room whose sign asks for a password the password box, both save the
/// clicked sign as the client's current room and send this request.
/// </para>
/// <para>
/// Body, U32 family: <c>[u32 len = 17 + 2n][16B room GUID][u8 n][n x UTF-16LE password]</c> (the
/// GUID, then the password length in chars as a byte, then its 2n bytes). A click with no password is 21 bytes
/// (n = 0). The GUID is the one the room's 2763 sign carries (the Zone's 2762 handed it out).
/// </para>
/// <para>
/// Answer (<see cref="CloseRangeChatRoomMembers"/> has the bodies): 23538 [0], 23517 to the
/// joiner and 23516 to every member, all together. The Zone then
/// shows the join on the map (2764 + 2770): that is published here as a join signal
/// (CloseRangeRoomSignals.PublishJoinAsync) for CloseRangeExitListener.
/// </para>
/// <para>
/// The room: the Chat server never sees the 1533 that opens it, so on the first join it reads the Zone's open
/// signal (CloseRangeRoomSignals.FindOpenRoomAsync: owner, capacity = the 1533 flag, password) and starts
/// the member list with the owner, whose Chat session gives the uid and name 23517 lists first. A GUID with no
/// open room, or whose owner is not connected to this Chat server, is answered 10 "Cannot find channel room."
/// </para>
/// <para>
/// Dropped without an answer: a body of any other shape, or a session before its 23001 (no uid or name to list
/// it under), as 23011 does.
/// </para>
/// <para>
/// OWNER DISCONNECT. Every session that enters a member list here (the owner when the list starts, each joiner
/// on success) gets its Disconnected event watched once (<see cref="WatchDisconnect"/>), because any member can
/// come to own the room through a hand-off. When an owner's Chat connection closes with members left, the room
/// passes to another member at that moment, not at the next join or exit (CloseRangeChatRoomMembers.OnSessionClosed),
/// and the hand-off is signalled to the Zone with OwnerGone set, for its 2766 + 2770 (new master, no 2765).
/// Subscribed per session rather than from ProudServer.SessionDisconnected
/// in Program.cs so that this handler owns it; the event is a multicast Func&lt;Task&gt; whose invoker awaits only
/// the last subscriber, so the handler catches everything itself.
/// </para>
/// </remarks>
public class ReqJoinCloseRangeHandler : ChatRmiHandlerBase<ReqJoinCloseRangeHandler>
{
    /// <summary>Size of the leading u32 length prefix; its value is the body size minus these 4 bytes.</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Size of the room GUID.</summary>
    public const int GuidSize = 16;

    /// <summary>Size of one UTF-16LE password character.</summary>
    public const int PasswordCharSize = 2;

    /// <summary>body+4: the 16-byte room GUID.</summary>
    public const int GuidOffset = LengthPrefixSize;

    /// <summary>body+20: the u8 password length in chars; the UTF-16LE password follows at +21.</summary>
    public const int PasswordCharsOffset = GuidOffset + GuidSize;

    public override ushort Opcode => ChatC2SOpcodes.ReqJoinCloseRangeChatRoom; // 23020
    public override string Name => "JoinCloseRange";

    /// <summary>The sessions whose Disconnected event already carries <see cref="OnMemberSessionClosedAsync"/>.</summary>
    private static readonly ConditionalWeakTable<ProudSession, object> Watched = new();

    /// <summary>Join attempts per (character, room) per <see cref="JoinAttemptWindowMs"/>; a successful join clears the
    /// count. Keyed by character id, not session, so reconnecting cannot reset the guess budget. Bounds password
    /// guessing; not a protocol value.</summary>
    public const int JoinAttemptLimit = 5;

    /// <summary>Window of <see cref="JoinAttemptLimit"/>.</summary>
    public const long JoinAttemptWindowMs = 60_000;

    private static readonly SlidingWindowRateLimiter<(uint CharacterId, Guid Room)> JoinAttempts =
        new(JoinAttemptLimit, JoinAttemptWindowMs);

    public ReqJoinCloseRangeHandler(IServiceScopeFactory s, ILogger<ReqJoinCloseRangeHandler> l) : base(s, l) { }

    /// <summary>
    /// Reads 23020: the GUID and the password bytes, when <paramref name="body"/> is exactly
    /// <c>[u32 len-4][guid16][u8 n][2n bytes]</c>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out byte[] roomGuid, out byte[] password)
    {
        roomGuid = [];
        password = [];
        if (body.Length < PasswordCharsOffset + 1 || BinaryPrimitives.ReadUInt32LittleEndian(body) != (uint)(body.Length - LengthPrefixSize))
            return false;
        int chars = body[PasswordCharsOffset];
        if (body.Length != PasswordCharsOffset + 1 + PasswordCharSize * chars) return false;
        roomGuid = body.Slice(GuidOffset, GuidSize).ToArray();
        password = body.Slice(PasswordCharsOffset + 1, PasswordCharSize * chars).ToArray();
        return true;
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (session.CharacterId is not uint characterId || !MessengerUid.TryGet(session, out var uid)
            || string.IsNullOrEmpty(session.Username))
        {
            Logger.LogWarning("Session {Id}: 23020 without a resolved messenger character, ignored", session.SessionId);
            return;
        }
        if (!TryParse(payload.Span, out var roomGuid, out var password))
        {
            Logger.LogWarning("Session {Id}: malformed 23020 body ({Len} bytes: {Body})",
                session.SessionId, payload.Length, Convert.ToHexString(payload.Span));
            return;
        }

        // Password guessing. Every attempt on a (character, room) is counted before the password is looked at, so
        // once K are used even the right password is refused (23538 19) until the window passes; a join clears it.
        // Keyed by character id so a reconnect (fresh session) cannot reset the budget.
        var attemptKey = (characterId, new Guid(roomGuid));
        if (!JoinAttempts.TryAcquire(attemptKey))
        {
            Logger.LogWarning("Session {Id}: too many joins of close-range room {Room}; refused", session.SessionId,
                Convert.ToHexString(roomGuid));
            await session.SendInternalRmiAsync(ChatS2COpcodes.AnsJoinCloseRangeChatRoom,
                CloseRangeChatRoomMembers.BuildJoinAnswer23538(CloseRangeChatRoomMembers.ResultWrongPassword), ct);
            return;
        }
        // The joiner's distance to the sign is not checked: the Chat server holds no positions and there is no
        // join radius to enforce. Log and allow.
        Logger.LogDebug("Session {Id}: close-range join spatial check skipped (no measured join radius)", session.SessionId);

        var signals = services.GetRequiredService<ICloseRangeRoomSignals>();
        if (!CloseRangeChatRoomMembers.IsOpen(roomGuid))
            await OpenFromZoneAsync(signals, services.GetRequiredService<ChatBroadcastService>(), roomGuid, ct);

        var sends = new List<CloseRangeChatRoomMembers.Send>();
        var joiner = new CloseRangeChatRoomMembers.Member(session, characterId, uid, session.Username);
        var (result, memberCount) = CloseRangeChatRoomMembers.Join(roomGuid, joiner, password, sends);
        await CloseRangeChatRoomMembers.DeliverAsync(sends);

        if (result != CloseRangeChatRoomMembers.ResultJoined)
        {
            Logger.LogInformation("Session {Id}: {Who} refused from close-range room {Room}: 23538 result {Result}",
                session.SessionId, session.Username, Convert.ToHexString(roomGuid), result);
            return;
        }
        JoinAttempts.Reset(attemptKey);
        Logger.LogInformation("Session {Id}: {Who} joined close-range room {Room} ({Count} members)",
            session.SessionId, session.Username, Convert.ToHexString(roomGuid), memberCount);
        WatchDisconnect(session, signals, Logger);

        // The Zone's 2764 + 2770 for the map. The answer above does not wait on it; a failed write costs only
        // the sign's member count and the joiner's marker.
        try
        {
            await signals.PublishJoinAsync(roomGuid, characterId, memberCount, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Session {Id}: could not signal the close-range room join to the Zone", session.SessionId);
        }
    }

    /// <summary>
    /// Starts this server's member list of <paramref name="roomGuid"/> from the Zone's open signal, when the
    /// room is still open and its owner is connected here. Leaves it absent otherwise (the join is then
    /// answered 10).
    /// </summary>
    private async Task OpenFromZoneAsync(ICloseRangeRoomSignals signals, ChatBroadcastService chat, byte[] roomGuid, CancellationToken ct)
    {
        CloseRangeRoomSignal? opened;
        try
        {
            opened = await signals.FindOpenRoomAsync(roomGuid, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not read the Zone's open signal of close-range room {Room}", Convert.ToHexString(roomGuid));
            return;
        }
        if (opened is null) return;

        foreach (var s in chat.Sessions)
        {
            if (s.CharacterId != opened.CharacterId || !s.IsConnected || !MessengerUid.TryGet(s, out var ownerUid)
                || string.IsNullOrEmpty(s.Username))
                continue;
            CloseRangeChatRoomMembers.Open(
                new CloseRangeChatRoomMembers.OpenedRoom(opened.RoomGuid, opened.Capacity, opened.Password),
                new CloseRangeChatRoomMembers.Member(s, opened.CharacterId, ownerUid, s.Username));
            WatchDisconnect(s, signals, Logger);
            return;
        }
        Logger.LogDebug("Close-range room {Room}: its owner {Char} is not connected to the Chat server",
            Convert.ToHexString(roomGuid), opened.CharacterId);
    }

    /// <summary>Hooks <see cref="OnMemberSessionClosedAsync"/> to <paramref name="session"/>'s Disconnected event,
    /// once per session however many rooms it enters.</summary>
    internal static void WatchDisconnect(ProudSession session, ICloseRangeRoomSignals signals, ILogger logger)
    {
        lock (Watched)
        {
            if (Watched.TryGetValue(session, out _)) return;
            Watched.Add(session, new object());
        }
        session.Disconnected += closing => OnMemberSessionClosedAsync(closing, signals, logger);
    }

    /// <summary>
    /// A room member's Chat connection closed: takes it out of every room (CloseRangeChatRoomMembers.OnSessionClosed),
    /// tells those who stay (23520) and signals each master hand-off to the Zone (exit with the new master, OwnerGone).
    /// Never throws: it runs inside ProudSession's Disconnected event.
    /// </summary>
    internal static async Task OnMemberSessionClosedAsync(ProudSession closing, ICloseRangeRoomSignals signals, ILogger logger)
    {
        try
        {
            var sends = new List<CloseRangeChatRoomMembers.Send>();
            var handOffs = CloseRangeChatRoomMembers.OnSessionClosed(closing, sends);
            await CloseRangeChatRoomMembers.DeliverAsync(sends);
            foreach (var h in handOffs)
            {
                logger.LogInformation("Session {Id}: owner {Old} of close-range room {Room} disconnected; master is now {New} ({Count} member(s))",
                    closing.SessionId, h.OldMasterCharacterId, Convert.ToHexString(h.RoomGuid), h.NewMasterCharacterId, h.MemberCount);
                await signals.PublishExitAsync(h.RoomGuid, h.OldMasterCharacterId, h.MemberCount,
                    h.NewMasterCharacterId, ownerGone: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session {Id}: close-range room departure on disconnect failed", closing.SessionId);
        }
    }
}
