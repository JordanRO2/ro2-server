using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handles RMI 23011 ReqPrivateChatRoomJoin: joins a user-created chat room by name, creating it if it does not exist.
/// </summary>
/// <remarks>
/// On success the reply is 23515, 23516 and 23517 in one tick (see <see cref="PrivateChatRooms"/>). On failure a lone
/// 23515 is sent with a refusal code (<see cref="PrivateChatRooms.BuildJoinRefusal"/>): 23 when the joiner is already
/// listed in the room, 26 or 19 for a missing or wrong password, and 29 for a full room.
/// Request body (U32 family): <c>[u32 innerLen][u8 nameChars][room name UTF-16LE][u8 passwordChars][password UTF-16LE]</c>.
/// </remarks>
public class ReqPrivateChatRoomJoinHandler : ChatRmiHandlerBase<ReqPrivateChatRoomJoinHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqPrivateChatRoomJoin; 
    public override string Name => "PrivateRoomJoin";
    public ReqPrivateChatRoomJoinHandler(IServiceScopeFactory s, ILogger<ReqPrivateChatRoomJoinHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        if (session.CharacterId is not uint characterId || !MessengerUid.TryGet(session, out var uid)
            || string.IsNullOrEmpty(session.Username))
        {
            Logger.LogWarning("Session {Id}: 23011 without a resolved messenger character, ignored", session.SessionId);
            return;
        }

        var req = payload.Span;
        if (req.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(req) != (uint)(req.Length - 4))
        {
            Logger.LogWarning("Session {Id}: malformed 23011 body ({Len} bytes)", session.SessionId, req.Length);
            return;
        }
        int pos = 4;
        int nameChars = req[pos++];
        if (pos + 2 * nameChars + 1 > req.Length) return;
        var roomName = Encoding.Unicode.GetString(req.Slice(pos, 2 * nameChars));
        pos += 2 * nameChars;
        int pwChars = req[pos++];
        if (pos + 2 * pwChars != req.Length) return;
        var password = Encoding.Unicode.GetString(req.Slice(pos, 2 * pwChars));
        if (roomName.Length == 0) return;

        var sends = new List<PrivateChatRooms.Send>();
        var outcome = PrivateChatRooms.Join(session, characterId, uid, session.Username, roomName, password, sends);
        if (outcome == PrivateChatRooms.JoinOutcome.Joined)
            Logger.LogInformation("Session {Id}: {Who} joined private chat room \"{Room}\"", session.SessionId, session.Username, roomName);
        else
            Logger.LogInformation("Session {Id}: {Who} refused from private chat room \"{Room}\": {Why} (a lone 23515 with the refusal code)",
                session.SessionId, session.Username, roomName, outcome);
        
        await PrivateChatRooms.DeliverAsync(sends);
    }
}
