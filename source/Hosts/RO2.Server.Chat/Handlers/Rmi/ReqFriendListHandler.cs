using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Services;

using BaseFriend = RO2.Network.Handlers.ChatRmiHandlerBase<RO2.Server.Chat.Handlers.Rmi.ReqFriendListHandler>;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>Handles RMI 23003 ReqFriendList: replies with 23505 AnsFriendList, the sender's stored friend list.</summary>
public class ReqFriendListHandler : BaseFriend
{
    public override ushort Opcode => ChatC2SOpcodes.ReqFriendList;
    public override string Name => "ReqFriendList";
    public ReqFriendListHandler(IServiceScopeFactory s, ILogger<ReqFriendListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name);
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        IReadOnlyList<CommunityChannels.FriendEntry> friends = Array.Empty<CommunityChannels.FriendEntry>();
        if (session.CharacterId is uint ownerId && MessengerUid.TryGet(session, out var ownerGid))
        {
            
            if (!services.GetRequiredService<ChatSocialThrottle>().TryAct(ownerId))
            {
                Logger.LogWarning("Session {Id}: 23003 over the social rate limit for character {Owner}, dropped",
                    session.SessionId, ownerId);
                return;
            }
            var stored = await services.GetRequiredService<FriendStore>().GetEntriesAsync(
                services.GetRequiredService<ChatBroadcastService>(),
                ownerId, ct);
            friends = WithGids(stored, ownerGid);
        }
        else
        {
            Logger.LogWarning("Session {Id}: 23003 before messenger login, sending the empty roster", session.SessionId);
        }

        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsFriendList, CommunityChannels.BuildFriendListBody(friends));
    }

 /// <summary>
 /// Returns the friend records with their uid set to the friend's GID: the requester's messenger uid in the
 /// high dword and the friend's character id in the low dword.
 /// </summary>
 /// <remarks>
 /// The client matches blocked friends by the full 64-bit uid, so friend uids must be GIDs for block state to
 /// display correctly. The block list and the other friend-related messages carry GIDs as well. An entry that
 /// already carries a GID maps to itself.
 /// </remarks>
    internal static IReadOnlyList<CommunityChannels.FriendEntry> WithGids(
        IReadOnlyList<CommunityChannels.FriendEntry> entries, ulong ownerGid)
    {
        var withGids = new List<CommunityChannels.FriendEntry>(entries.Count);
        foreach (var e in entries)
            withGids.Add(e with { Uid = CharacterGid.Of(ownerGid, CharacterGid.CharacterIdOf(e.Uid)) });
        return withGids;
    }
}
