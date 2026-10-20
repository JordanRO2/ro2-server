using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handler for Chat RMI 23018 - ReqRoomChat (room/channel chat send). The same C2S message carries
/// lines for two kinds of room, and the server picks the answer by the room GUID in the body:
/// - a user-created private room (<see cref="PrivateChatRooms"/>): the body is relayed verbatim as
///   23529 NfyRoomChat_Private to the room's members, the sender included;
/// - a community channel (<see cref="CommunityChannels"/>): it is re-broadcast with the sender's name as
///   23528 NfyRoomChat_Public to every logged-in messenger session in that channel
///   (<see cref="CommunityChannelMembership"/>), the sender included, unless the
///   sender is over the flood limit, in which case only the sender gets 23536 NfyChannelChatError
///   [u16 22] (see <see cref="ChannelChatFloodGate"/>).
/// A line for any other GUID is dropped.
/// The sender handle in the C2S body is the player's own character GID, the value it got in 2310, and
/// not whatever 2733 handed out (see <see cref="MessengerUid"/>); the 2733 carries that same GID.
/// </summary>
public class ReqRoomChatHandler : ChatRmiHandlerBase<ReqRoomChatHandler>
{
    // C2S 23018 body offsets: [innerLen:u32][channelGuid:16][channelIdx:u16][senderHandle:u64][msgChars:u8][msg].
    // Exact shape: len == 31 + 2*msgChars, innerLen == len-4.
    private const int GuidOffset = 4;
    private const int IndexOffset = 20;
    private const int SenderOffset = 22;
    private const int MsgCharsOffset = 30;
    private const int MsgOffset = 31;

    public override ushort Opcode => ChatC2SOpcodes.ReqRoomChat; // 23018
    public override string Name => "RoomChat";
    public ReqRoomChatHandler(IServiceScopeFactory s, ILogger<ReqRoomChatHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < MsgOffset) return;
        var req = payload.Span;
        var channelGuid = req.Slice(GuidOffset, 16).ToArray();
        var channelIdx = BinaryPrimitives.ReadUInt16LittleEndian(req.Slice(IndexOffset, 2));
        var msgChars = req[MsgCharsOffset];
        if (msgChars == 0 || req.Length - MsgOffset < msgChars * 2) return;

        // Private room first: its GUID is one PrivateChatRooms created, so it can never be a channel GUID.
        // Empty when the GUID is no live private room (MemberSessions keeps only connected members, and a
        // connected sender that is a member always counts itself).
        var roomMembers = PrivateChatRooms.MemberSessions(channelGuid);
        if (roomMembers.Count > 0)
        {
            await RelayToPrivateRoomAsync(session, payload, channelGuid, channelIdx, msgChars, roomMembers);
            return;
        }

        // Only a known community channel gets the 23528 broadcast. Anything else (a private room lost to a
        // Chat restart, a close-range room, whose membership lives on the Zone, not here) is dropped rather than
        // going out to every session on 23528.
        var channel = CommunityChannels.ByGuid(channelGuid);
        if (channel is null)
        {
            Logger.LogInformation("[RoomChat] session {Id}: line for unknown room {Guid} (idx{Idx}) dropped: " +
                "neither a live private room nor a community channel",
                session.SessionId, Convert.ToHexString(channelGuid), channelIdx);
            return;
        }

        // The message goes back out VERBATIM, char count and last char included. The final char is not a
        // leaked terminator: it is the client's own end marker (U+F801), and the line is echoed on the sender's
        // own session with guid, index, msgChars and message bytes identical.
        var msgBytes = req.Slice(MsgOffset, msgChars * 2).ToArray();

        // A 23528 always carries a sender name, and the only source for it is the
        // character ChatAuthHandler resolved; drop the line instead of inventing a placeholder name.
        var senderName = session.Username;
        if (string.IsNullOrEmpty(senderName))
        {
            Logger.LogWarning("[RoomChat] session {Id}: no resolved character name, line on {Ch}(idx{Idx}) dropped",
                session.SessionId, channel.Name, channelIdx);
            return;
        }

        // A line with NUL/control/PUA characters is dropped silently (there is no refusal frame for it).
        if (!ChatText.IsCleanBody(msgBytes))
        {
            Logger.LogWarning("[RoomChat] {Ch}: line with control/private-use characters dropped", channel.Name);
            return;
        }

        // The tab index is the channel's own; a client-sent idx that differs is replaced, never echoed.
        if (channelIdx != channel.Index)
        {
            Logger.LogWarning("[RoomChat] session {Id}: idx{Idx} on {Ch} normalized to idx{Real}",
                session.SessionId, channelIdx, channel.Name, channel.Index);
            channelIdx = channel.Index;
        }

        // Flood limit: a 5th line within 5 s of the sender's 4th-last delivered one gets 23536
        // [u16 22] ("Chat scrolling is too fast.") on the sender's session instead of the 23528 broadcast,
        // so not even the sender's own echo goes out. Rule in ChannelChatFloodGate.
        if (!ChannelChatFloodGate.TryAdmit(session))
        {
            Logger.LogInformation("[RoomChat] {Ch}(idx{Idx}) {Sender}: line refused, {Max} lines already in the last {Window} ms",
                channel.Name, channelIdx, senderName, ChannelChatFloodGate.MaxLinesPerWindow, ChannelChatFloodGate.WindowMs);
            await session.SendInternalRmiAsync(ChatS2COpcodes.NfyChannelChatError, ChannelChatFloodGate.RefusalBody());
            return;
        }

        // U+F801 is the client's private-use end marker; strip it (and any NUL) for the log only.
        Logger.LogInformation("[RoomChat] {Ch}(idx{Idx}) {Sender}: {Msg}",
            channel.Name, channelIdx, senderName,
            Encoding.Unicode.GetString(msgBytes).TrimEnd('', '\0'));

        // S2C 23528: [innerLen:u32 = len-4][channelGuid:16][channelIdx:u16][nameChars:u8][name UTF-16LE, no
        // terminator][msgChars:u8][msg UTF-16LE].
        var nameW = Encoding.Unicode.GetBytes(senderName);
        int innerLen = 16 + 2 + 1 + nameW.Length + 1 + msgBytes.Length;

        using var w = new PacketWriter();
        w.WriteUInt32((uint)innerLen);
        w.WriteBytes(channelGuid);
        w.WriteUInt16(channelIdx);
        w.WriteByte((byte)(nameW.Length / 2));
        w.WriteBytes(nameW);
        w.WriteByte(msgChars);
        w.WriteBytes(msgBytes);
        var body = w.GetWrittenSpan().ToArray();

        // The receivers are the channel's members: every session joins all three channels at its 23001
        // (ChatAuthHandler, 23534 x3), a 23013 on the channel's GUID takes it out and 23012 puts it back. The
        // client prints any line whose GUID finds a tab and never checks the joined flag, so a left channel goes
        // quiet only if the server stops sending it. A connection still in the ProudNet handshake has joined
        // nothing. The sender itself is not checked.
        var broadcast = services.GetRequiredService<ChatBroadcastService>();
        foreach (var target in broadcast.Sessions)
        {
            if (target.IsConnected && target.CharacterId.HasValue
                && CommunityChannelMembership.IsMember(target, channel.Index))
                await target.SendInternalRmiAsync(ChatS2COpcodes.NfyRoomChat_Public, body);
        }
    }

    /// <summary>
    /// S2C 23529 NfyRoomChat_Private: the C2S 23018 body, byte for byte, to every member of the room.
    /// - Layout, the same as 23018: [u32 innerLen = len-4][guid16 room][u16 tabIdx][u64 senderUid]
    ///   [u8 nChars][msg UTF-16LE, the client's end marker included]. len == 31+2n and innerLen == len-4.
    /// - Trigger: a 23018 on a known private-room GUID is answered on the sender's own session by a
    ///   byte-identical 23529.
    /// - Receivers: every member of the room, which the client already holds from 23515/23516/23517, and the
    ///   senderUid is listed in that receiver's member list for the room.
    /// Unlike 23528 there is no sender name: receivers resolve senderUid through the member list, so this
    /// path needs no session.Username. The client writes its own 2310 GID as senderUid, which equals the
    /// 2733/23001 uid (see <see cref="MessengerUid"/>). So the member uid is the full 23001 uid (MessengerUid),
    /// and the verbatim body names the sender by the same uid our 23516/23517 list it under.
    /// No failure frame exists for a refused line, so a refusal is logged and sends nothing.
    /// </summary>
    private async Task RelayToPrivateRoomAsync(ProudSession session, ReadOnlyMemory<byte> payload,
        byte[] roomGuid, ushort tabIdx, byte msgChars, IReadOnlyList<ProudSession> roomMembers)
    {
        var req = payload.Span;
        var roomHex = Convert.ToHexString(roomGuid);

        // Relayed as is, so it must have exactly the expected shape:
        // trailing bytes or a wrong innerLen would otherwise reach every member.
        if (req.Length != MsgOffset + msgChars * 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(req) != (uint)(req.Length - 4))
        {
            Logger.LogWarning("[RoomChat] session {Id}: malformed private-room line ({Len} bytes, {N} chars) for room {Room} dropped",
                session.SessionId, req.Length, msgChars, roomHex);
            return;
        }

        // Only a member speaks in the room.
        if (!roomMembers.Contains(session))
        {
            Logger.LogWarning("[RoomChat] session {Id}: not a member of private room {Room}, line dropped",
                session.SessionId, roomHex);
            return;
        }

        // Receivers name the line by senderUid, so it must be this member's own uid; otherwise one member
        // could post as another. The member uid is the full 64-bit 23001 uid PrivateChatRooms lists it
        // under, never the truncated CharacterId, which an honest client's senderUid (its GID) never equals.
        var senderUid = BinaryPrimitives.ReadUInt64LittleEndian(req.Slice(SenderOffset, 8));
        if (!MessengerUid.TryGet(session, out var memberUid) || senderUid != memberUid)
        {
            Logger.LogWarning("[RoomChat] session {Id}: private-room line for {Room} carries senderUid 0x{Uid:X16}, not the member's 0x{Member:X16}; dropped",
                session.SessionId, roomHex, senderUid, memberUid);
            return;
        }

        // Dirty text is dropped silently, then the per-character flood gate (shared with the channel path).
        if (!ChatText.IsCleanBody(req.Slice(MsgOffset, msgChars * 2)))
        {
            Logger.LogWarning("[RoomChat] session {Id}: private-room line with control/private-use characters dropped", session.SessionId);
            return;
        }
        if (!ChannelChatFloodGate.TryAdmit(session))
        {
            Logger.LogInformation("[RoomChat] private room {Room}: line dropped, flood limit", roomHex);
            return;
        }

        Logger.LogInformation("[RoomChat] private room {Room}(idx{Idx}) {Sender}: {Msg} -> {Count} member(s)",
            roomHex, tabIdx, session.Username ?? senderUid.ToString(),
            Encoding.Unicode.GetString(req.Slice(MsgOffset, msgChars * 2)).TrimEnd('\uF801', '\uF842', '\0'),
            roomMembers.Count);

        var body = payload.ToArray();
        var sends = new List<PrivateChatRooms.Send>(roomMembers.Count);
        foreach (var member in roomMembers)
            sends.Add(new PrivateChatRooms.Send(member, ChatS2COpcodes.NfyRoomChat_Private, body));
        // Skips members whose connection went away since the lookup.
        await PrivateChatRooms.DeliverAsync(sends);
    }
}
