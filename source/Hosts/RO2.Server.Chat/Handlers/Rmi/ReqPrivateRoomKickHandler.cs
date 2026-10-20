using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handles RMI 23015 ReqPrivateChatRoomInUserKick: the room master expels a member.
/// Replies with 23522 [u16 result] to the master. On success the target receives 23524 and the remaining members
/// receive 23523 (see <see cref="PrivateChatRooms.Kick"/>).
/// </summary>
/// <remarks>
/// Request body (fixed 24 bytes): <c>[16-byte room GUID][u64 target member uid]</c>. The uid is the member's full
/// messenger uid, the key used by the room's member list.
/// </remarks>
public class ReqPrivateRoomKickHandler : ChatRmiHandlerBase<ReqPrivateRoomKickHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqPrivateChatRoomInUserKick; 
    public override string Name => "RoomKick";
    public ReqPrivateRoomKickHandler(IServiceScopeFactory s, ILogger<ReqPrivateRoomKickHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (!MessengerUid.TryGet(session, out var kickerUid))
        {
            Logger.LogWarning("Session {Id}: 23015 before messenger login, ignored", session.SessionId);
            return;
        }
        if (!TryParse(payload.Span, out var roomGuid, out var targetUid))
        {
            Logger.LogWarning("Session {Id}: 23015 body is {Len} bytes (client reader: 24)", session.SessionId, payload.Length);
            return;
        }

        var sends = new List<PrivateChatRooms.Send>();
        var result = PrivateChatRooms.Kick(roomGuid, session, kickerUid, targetUid, sends);
        Logger.LogInformation("Session {Id}: {Who} kicks 0x{Target:X16} from private chat room {Guid}: result {Result}",
            session.SessionId, session.Username ?? "?", targetUid, Convert.ToHexString(roomGuid), result);
        await PrivateChatRooms.DeliverAsync(sends);
    }

 /// <summary>Parses a kick body: the 16-byte room GUID and the target member uid. The body must be exactly 24 bytes.</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out byte[] roomGuid, out ulong targetUid)
    {
        roomGuid = [];
        targetUid = 0;
        if (body.Length != 24) return false;
        roomGuid = body[..16].ToArray();
        targetUid = BinaryPrimitives.ReadUInt64LittleEndian(body[16..]);
        return true;
    }
}
