using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Services;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handles RMI 23005 ReqDelFriend: removes a friend from the sender's list and replies with 23509 AnsDelFriend.
/// </summary>
/// <remarks>
/// Request body: the bare u64 friend uid, which is the friend's GID (see <c>ReqFriendListHandler.WithGids</c>).
/// Reply: [u16 0][that uid]. The reply is sent even when the uid was not in the list.
/// </remarks>
public class ReqDelFriendHandler : ChatRmiHandlerBase<ReqDelFriendHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqDelFriend; 
    public override string Name => "DelFriend";
    public ReqDelFriendHandler(IServiceScopeFactory s, ILogger<ReqDelFriendHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (session.CharacterId is not uint ownerId)
        {
            Logger.LogWarning("Session {Id}: 23005 before messenger login, ignored", session.SessionId);
            return;
        }
        
        if (!services.GetRequiredService<ChatSocialThrottle>().TryAct(ownerId))
        {
            Logger.LogWarning("Session {Id}: 23005 over the social rate limit for character {Owner}, dropped",
                session.SessionId, ownerId);
            return;
        }
        if (payload.Length != 8)
        {
            Logger.LogWarning("Session {Id}: 23005 body is {Len} bytes (retail: 8)", session.SessionId, payload.Length);
            return;
        }

        ulong uid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        
        
        
        await services.GetRequiredService<FriendStore>().RemoveAsync(ownerId,
            CharacterGid.CharacterIdOf(uid));

        Logger.LogInformation("Session {Id}: {Owner} removed friend uid {Uid}", session.SessionId, session.Username ?? "?", uid);
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsDelFriend, FriendStore.BuildDelFriendAnswer(uid));
    }
}
