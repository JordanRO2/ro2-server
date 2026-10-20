using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Services;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>Handles RMI 23007 ReqDelBlockList: removes one entry from the sender's block list and replies with 23511 AnsDelBlock.</summary>
/// <remarks>
/// Request body: the bare u64 uid of the entry. Reply: [u16 0][the same uid]. The entry is removed from the
/// stored list returned by 23008.
/// </remarks>
public class ReqDelBlockHandler : ChatRmiHandlerBase<ReqDelBlockHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqDelBlockList; 
    public override string Name => "DelBlock";
    public ReqDelBlockHandler(IServiceScopeFactory s, ILogger<ReqDelBlockHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name);

        if (session.CharacterId is not uint ownerId)
        {
            Logger.LogWarning("Session {Id}: {Name} before messenger login - ignored", session.SessionId, Name);
            return;
        }

        
        if (!services.GetRequiredService<ChatSocialThrottle>().TryAct(ownerId))
        {
            Logger.LogWarning("Session {Id}: {Name} over the social rate limit for character {Owner}, dropped",
                session.SessionId, Name, ownerId);
            return;
        }

        if (payload.Length < 8)
        {
            Logger.LogWarning("Session {Id}: {Name} body is {Size} bytes, expected 8", session.SessionId, Name, payload.Length);
            return;
        }

        var uid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        await services.GetRequiredService<BlockListRepository>().RemoveAsync(ownerId, uid, ct);
        Logger.LogInformation("Session {Id}: character {Owner} unblocked uid {Uid}", session.SessionId, ownerId, uid);

        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsDelBlock, BlockListPackets.BuildDelBlockBody(uid));
    }
}
