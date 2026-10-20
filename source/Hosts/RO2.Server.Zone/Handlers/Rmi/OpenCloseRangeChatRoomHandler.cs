using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles a request to open a close-range ("local") chat room (RMI 1533, ReqOpenCloseRangeChatRoom).
/// </summary>
/// <remarks>
/// <para>
/// Request body (4-byte length prefix family):
/// <c>[u32 innerLen = bodyLen - 4][u8 flag][u8 nameChars][name UTF-16LE][u8 pwChars][password UTF-16LE]</c>.
/// The smallest well-formed body is 7 bytes (empty name and password). The name is at most 50 characters plus its NUL
/// in a 51-character buffer, the password at most 4 plus NUL in a 5-character buffer.
/// </para>
/// <para>
/// The 2762 reply is a fixed 136-byte body: <c>+0 u16 result</c>, <c>+2 wchar[51] room name</c>,
/// <c>+104 wchar[5] password</c>, <c>+114 u8 flag</c>, <c>+115 16-byte room GUID</c>, <c>+131</c> five trailing bytes.
/// </para>
/// <para>
/// The reply (2762 AnsOpenCloseRangeChatRoom) echoes the request's flag, room name and password. On success the room sign
/// (2763 NfyOpenCloseRangeChatRoom) is sent to the opener and to the players on the map, and the room is registered
/// on this Zone and published to the Chat server so that joins can find it.
/// </para>
/// <para>
/// When the owner leaves the room, the sign is taken down with 2765 NfyExitCloseRangeChatRoom.
/// A request while the requester already owns an open room is refused with result 28 and no sign.
/// </para>
/// </remarks>
public class OpenCloseRangeChatRoomHandler : ZoneRmiHandlerBase<OpenCloseRangeChatRoomHandler>
{
 /// <summary>Capacity of the room name buffer, in wide characters.</summary>
    private const int NameBufferChars = 51;
 /// <summary>Capacity of the password buffer, in wide characters.</summary>
    private const int PasswordBufferChars = 5;
 /// <summary>Bytes per UTF-16LE character.</summary>
    private const int BytesPerChar = 2;

 /// <summary>Smallest well-formed 1533 body: length prefix (4) + flag + nameChars + pwChars, with an empty name and password.</summary>
    private const int MinRequestBytes = 7;
 /// <summary>Size of the u32 inner length at the start of the 1533 body; it counts the bytes that follow it.</summary>
    private const int LengthPrefixBytes = 4;
 /// <summary>1533 body offset of the u8 room flag.</summary>
    private const int FlagOffset = 4;
 /// <summary>1533 body offset of the u8 name length, in characters.</summary>
    private const int NameCharsOffset = 5;
 /// <summary>1533 body offset of the first name character.</summary>
    private const int NameOffset = 6;

 /// <summary>U+F801, the end-of-name marker the client appends to the room name; trimmed for logging only.</summary>
    private const char NameEndMarker = (char)0xF801;

 /// <summary>2762 result 0: the room was opened.</summary>
    private const ushort ResultOpened = 0;

 /// <summary>
 /// 2762 reply +131: the first byte of the five-byte trailer, a constant 0x10. The client's handler does not read
 /// the trailer; its meaning is not established.
 /// </summary>
    private const byte ReplyTrailerMarker = 0x10;

 /// <summary>
 /// 2762 reply +132 to +135: the four zero bytes that follow <see cref="ReplyTrailerMarker"/>. Each is written as
 /// this value.
 /// </summary>
    private const byte ReplyTrailerPad = 0x00;

 /// <summary>
 /// Trailing five bytes of the 2762 reply (+131 to +135): <c>+131 u8 <see cref="ReplyTrailerMarker"/></c> then
 /// <c>+132..+135</c> four <see cref="ReplyTrailerPad"/> bytes. A refusal (result 28) zeroes the whole body,
 /// marker included.
 /// </summary>
    private static readonly byte[] ReplyTrailer =
        { ReplyTrailerMarker, ReplyTrailerPad, ReplyTrailerPad, ReplyTrailerPad, ReplyTrailerPad };

 /// <summary>Fixed body length of the 2762 reply: 136 bytes, on success and on refusal.</summary>
    internal const int AnswerBytes = 136;

 /// <summary>Result code 28 of 2762: the requester already owns an open close-range room.</summary>
    internal const ushort ResultAlreadyOwnsOpenRoom = 28;

    public override ushort Opcode => ZoneC2SOpcodes.ReqOpenCloseRangeChatRoom;
    public override string Name => "OpenCloseRangeChatRoom";
    public OpenCloseRangeChatRoomHandler(IServiceScopeFactory s, ILogger<OpenCloseRangeChatRoomHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var req = payload.Span;
        if (req.Length < MinRequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(req) != (uint)(req.Length - LengthPrefixBytes))
        {
            Logger.LogWarning("{Name}: malformed 1533 body ({Len} bytes), no room opened", player.Name, req.Length);
            return;
        }

        byte flag = req[FlagOffset];
        int nameChars = req[NameCharsOffset];
        int pos = NameOffset;
        if (pos + BytesPerChar * nameChars + 1 > req.Length) return;
        var name = req.Slice(pos, BytesPerChar * nameChars).ToArray();
        pos += BytesPerChar * nameChars;
        int pwChars = req[pos++];
        if (pos + BytesPerChar * pwChars != req.Length) return;
        var password = req.Slice(pos, BytesPerChar * pwChars).ToArray();

        // The requester's own room is still open: refused with result 28 over a zeroed body and no 2763.
        // Nothing is registered or published, so the open room stays the only one.
        if (OwnsOpenRoom(player))
        {
            Logger.LogInformation("{Name} asked for a second close-range chat room while its own is open: 2762 result {Code}",
                player.Name, ResultAlreadyOwnsOpenRoom);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenCloseRangeChatRoom, BuildRefusal2762(ResultAlreadyOwnsOpenRoom));
            return;
        }

        var roomGuid = Guid.NewGuid().ToByteArray();
        Logger.LogInformation("{Name} opens close-range chat room \"{Room}\" (flag {Flag}, password {HasPw})",
            player.Name, Encoding.Unicode.GetString(name).TrimEnd(NameEndMarker, '\0'), flag, password.Length > 0);

        // The name as the client's wchar[51]: the same 102 bytes go into 2762 +2 and 2763 blob+8.
        var nameBuffer = FixedWide(name, NameBufferChars);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenCloseRangeChatRoom, writer =>
        {
            writer.WriteUInt16(ResultOpened);                        // +0   result
            writer.WriteBytes(nameBuffer);                           // +2   wchar[51] room name
            WriteFixedWide(writer, password, PasswordBufferChars);   // +104 wchar[5] password
            writer.WriteByte(flag);                                  // +114 echoed flag
            writer.WriteBytes(roomGuid);                             // +115 room GUID
            writer.WriteBytes(ReplyTrailer);                         // +131 constant trailer
        });

        // Recorded before the sign goes up, so an exit signal can never find the sign without its room. The name
        // buffer and the password flag are kept for a master hand-off's 2766, which carries both as the 2763 does.
        CloseRangeChatRooms.Register(new CloseRangeRoom(CloseRangeChatRooms.IdOf(roomGuid),
            player.CharacterId, player.EntityId, flag, nameBuffer, HasPassword: password.Length > 0));

        // 2763, the sign: the opener first (its own copy, right after its 2762), then everyone else on the map.
        var sign = CloseRangeChatPackets.BuildNfyOpenCloseRangeChatRoom2763(player.EntityId, nameBuffer, flag, roomGuid,
            hasPassword: password.Length > 0);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyOpenCloseRangeChatRoom, sign);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyOpenCloseRangeChatRoom,
            writer => writer.WriteBytes(sign));

        // After the sign: a join needs a click on the sign first. A failed publish costs only the joins (logged),
        // not the room.
        try
        {
            await services.GetRequiredService<ICloseRangeRoomSignals>().PublishOpenAsync(
                roomGuid, player.CharacterId, flag, password, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: could not publish close-range room {Room} to the Chat server; it cannot be joined",
                player.Name, CloseRangeChatRooms.IdOf(roomGuid));
        }
    }

 /// <summary>Whether <paramref name="player"/> already owns an open close-range room on this Zone.</summary>
    internal static bool OwnsOpenRoom(PlayerState player)
        => CloseRangeChatRooms.FindOwnedBy(player.CharacterId, player.EntityId) is not null;

 /// <summary>Builds the 2762 refusal reply carrying <paramref name="result"/> and no room data.</summary>
    internal static byte[] BuildRefusal2762(ushort result)
    {
        var body = new byte[AnswerBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

 /// <summary>Converts UTF-16LE characters to a zero-padded wchar[capacity] buffer.</summary>
    private static byte[] FixedWide(byte[] utf16, int capacity)
    {
        var buffer = new byte[capacity * BytesPerChar];
        utf16.AsSpan(0, Math.Min(utf16.Length, (capacity - 1) * BytesPerChar)).CopyTo(buffer);
        return buffer;
    }

 /// <summary>Writes UTF-16LE characters into a zero-padded wchar[capacity] buffer, always leaving a terminating NUL.</summary>
    private static void WriteFixedWide(PacketWriter writer, byte[] utf16, int capacity)
    {
        int bytes = Math.Min(utf16.Length, (capacity - 1) * BytesPerChar);
        writer.WriteBytes(utf16.AsSpan(0, bytes));
        writer.WriteZeros(capacity * BytesPerChar - bytes);
    }
}
