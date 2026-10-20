using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handles RMI 23014 ReqPrivateChatRoomChangePass: the room master sets the room password.
/// Replies with 23521 to the requester only. The new password is checked by the next 23011 join into the room.
/// </summary>
/// <remarks>
/// Request body (U32 family): <c>[u32 innerLen][16-byte room GUID][u8 nChars][password UTF-16LE]</c>.
/// Passwords longer than 4 characters are refused with code 12 (see <see cref="PrivateChatRooms.ChangePassword"/>).
/// </remarks>
public class ReqPrivateRoomChangePassHandler : ChatRmiHandlerBase<ReqPrivateRoomChangePassHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqPrivateChatRoomChangePass; 
    public override string Name => "RoomChangePass";
    public ReqPrivateRoomChangePassHandler(IServiceScopeFactory s, ILogger<ReqPrivateRoomChangePassHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (!MessengerUid.TryGet(session, out var uid))
        {
            Logger.LogWarning("Session {Id}: 23014 before messenger login, ignored", session.SessionId);
            return;
        }
        if (!TryParse(payload.Span, out var roomGuid, out var password))
        {
            Logger.LogWarning("Session {Id}: malformed 23014 body ({Len} bytes)", session.SessionId, payload.Length);
            return;
        }

        var sends = new List<PrivateChatRooms.Send>();
        var result = PrivateChatRooms.ChangePassword(roomGuid, session, uid, password, sends);
        
        Logger.LogInformation("Session {Id}: {Who} changes the password of private chat room {Guid}: result {Result}",
            session.SessionId, session.Username ?? "?", Convert.ToHexString(roomGuid), result);
        await PrivateChatRooms.DeliverAsync(sends);
    }

 /// <summary>
 /// Parses a change-password body. An over-long password is kept as sent, so the room refuses it rather than truncating it.
 /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out byte[] roomGuid, out string password)
    {
        roomGuid = [];
        password = "";
        if (body.Length < 21 || BinaryPrimitives.ReadUInt32LittleEndian(body) != (uint)(body.Length - 4))
            return false;
        int chars = body[20];
        if (21 + 2 * chars != body.Length) return false;
        roomGuid = body.Slice(4, 16).ToArray();
        password = Encoding.Unicode.GetString(body.Slice(21, 2 * chars));
        return true;
    }
}
