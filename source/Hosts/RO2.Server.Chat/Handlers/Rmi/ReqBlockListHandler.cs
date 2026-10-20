using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Services;

using BaseBlock = RO2.Network.Handlers.ChatRmiHandlerBase<RO2.Server.Chat.Handlers.Rmi.ReqBlockListHandler>;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>RMI 23008 ReqBlockList → 23506 AnsBlockList (the stored block list). Both the reply opcode and the body
/// used to be wrong. 23505 is AnsFriendList, not AnsBlockList: the client protocol uses 23008 and 23506
/// in equal counts, and repeating 23004,23003 without repeating 23008 draws no extra 23506 — so the "channel member roster on 23505"
/// reading was the friend list all along (its reader is RmiParams_AnsFriendList_RMI23505_ReadFromMessage,
/// ). The self-entry body moved to ReqFriendListHandler with 23505.</summary>
public class ReqBlockListHandler : BaseBlock
{
    public override ushort Opcode => ChatC2SOpcodes.ReqBlockList;
    public override string Name => "ReqBlockList";
    public ReqBlockListHandler(IServiceScopeFactory s, ILogger<ReqBlockListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name);
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        IReadOnlyList<BlockedCharacter> entries = Array.Empty<BlockedCharacter>();
        if (session.CharacterId is uint ownerId && MessengerUid.TryGet(session, out var ownerGid))
        {
            
            if (!services.GetRequiredService<ChatSocialThrottle>().TryAct(ownerId))
            {
                Logger.LogWarning("Session {Id}: 23008 over the social rate limit for character {Owner}, dropped",
                    session.SessionId, ownerId);
                return;
            }
            entries = await services.GetRequiredService<BlockListRepository>()
                .GetAsync(ownerId, ownerGid, ct);
        }

        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsBlockList, BlockListPackets.BuildBlockListBody(entries));
    }
}
