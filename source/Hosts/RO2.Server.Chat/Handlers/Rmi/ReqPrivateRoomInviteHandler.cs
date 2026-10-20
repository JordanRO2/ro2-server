using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handles RMI 23016 ReqPrivateChatRoomInvate: the room master invites an online character by name.
/// Replies with 23525 [u16 result] to the inviter. On success the invitee joins the room and receives 23532,
/// and the members already in the room receive 23526 (see <see cref="PrivateChatRooms.Invite"/>).
/// </summary>
/// <remarks>
/// Request body (U32 family): <c>[u32 innerLen][u16 room type][16-byte room GUID][u8 nChars][name UTF-16LE]</c>.
/// The room and master checks are repeated on the server, which does not rely on the client having made them.
/// </remarks>
public class ReqPrivateRoomInviteHandler : ChatRmiHandlerBase<ReqPrivateRoomInviteHandler>
{
 /// <summary>Maximum number of characters kept from the invitee name.</summary>
    public const int NameMaxChars = 20;

    public override ushort Opcode => ChatC2SOpcodes.ReqPrivateChatRoomInvate; 
    public override string Name => "RoomInvite";
    public ReqPrivateRoomInviteHandler(IServiceScopeFactory s, ILogger<ReqPrivateRoomInviteHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        
        
        if (!MessengerUid.TryGet(session, out var inviterUid))
        {
            Logger.LogWarning("Session {Id}: 23016 before messenger login, ignored", session.SessionId);
            return;
        }
        if (!TryParse(payload.Span, out var roomGuid, out var inviteeName))
        {
            Logger.LogWarning("Session {Id}: malformed 23016 body ({Len} bytes)", session.SessionId, payload.Length);
            return;
        }

        var broadcast = services.GetRequiredService<ChatBroadcastService>();
        var invitee = FindOnline(broadcast.Sessions, inviteeName);

        var sends = new List<PrivateChatRooms.Send>();
        var result = PrivateChatRooms.Invite(roomGuid, session, inviterUid, invitee, sends);
        Logger.LogInformation("Session {Id}: {Who} invites \"{Name}\" into private chat room {Guid}: result {Result}",
            session.SessionId, session.Username ?? "?", inviteeName, Convert.ToHexString(roomGuid), result);
        await PrivateChatRooms.DeliverAsync(sends);
    }

 /// <summary>
 /// Parses an invite request body into the room GUID and invitee name. The name is limited to
 /// <see cref="NameMaxChars"/> characters.
 /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out byte[] roomGuid, out string name)
    {
        roomGuid = [];
        name = "";
        if (body.Length < 23 || BinaryPrimitives.ReadUInt32LittleEndian(body) != (uint)(body.Length - 4))
            return false;
        int chars = body[22];
        if (23 + 2 * chars != body.Length) return false;
        roomGuid = body.Slice(6, 16).ToArray();
        name = Encoding.Unicode.GetString(body.Slice(23, 2 * Math.Min(chars, NameMaxChars))).TrimEnd('\0');
        return true;
    }

 /// <summary>
 /// Finds the connected messenger session of the named character (case-insensitive), or null if that character
 /// is not online with its messenger login completed. If several sessions match, the newest is returned.
 /// </summary>
    public static PrivateChatRooms.Invitee? FindOnline(IEnumerable<ProudSession> sessions, string name)
    {
        if (name.Length == 0) return null;
        PrivateChatRooms.Invitee? found = null;
        uint newest = 0;
        foreach (var s in sessions)
        {
            if (!s.IsConnected || s.CharacterId is not uint characterId || !MessengerUid.TryGet(s, out var uid)
                || s.Username is not { Length: > 0 } username
                || !string.Equals(username, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found is null || s.SessionId > newest)
            {
                found = new PrivateChatRooms.Invitee(s, characterId, uid, username);
                newest = s.SessionId;
            }
        }
        return found;
    }
}
