using RO2.Application.Interfaces;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;

using Base1304 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.AnsMapLoadingHandler>;
using Base1307 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqActorDetailInfoHandler>;
using Base1340 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPCBankDataHandler>;
using Base1435 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqLoadKharaTraceListHandler>;
using Base1442 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqCardBookListHandler>;
using Base1513 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGuildSkillListHandler>;
using Base1516 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqMessengerLoginKeyHandler>;
using Base1541 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqLoadQuestTraceListHandler>;
using Base1567 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqMyEventHandler>;
using Base1592 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallItemListAllHandler>;
using Base1593 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallItemListHandler>;
using Base1596 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallMyInfoHandler>;
using Base1599 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPresentPasswordStatusHandler>;
using Base1642 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPlayTimeRewardHandler>;
using Base1644 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqAttendanceRewardHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

public class ReqMessengerLoginKeyHandler : Base1516
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMessengerLoginKey;
    public override string Name => "ReqMessengerLoginKey";

 /// <summary>Port of the Chat server advertised to the client.</summary>
    private const ushort ChatPort = 7401;

 /// <summary>How long a messenger login key stays valid. Keys are single-use.</summary>
    private static readonly TimeSpan MessengerKeyLifetime = TimeSpan.FromMinutes(5);

    public ReqMessengerLoginKeyHandler(IServiceScopeFactory s, ILogger<ReqMessengerLoginKeyHandler> l) : base(s, l) { }

 /// <summary>
 /// Handles RMI 1516 (ReqMessengerLoginKey) by issuing a one-shot messenger login key and returning it in
 /// AnsPlayerLoginKey (2733). The client redeems the key with its messenger on the Chat server.
 /// </summary>
 /// <remarks>
 /// The client sends this request on world entry and periodically while its messenger is offline, and a new
 /// key is issued for every request. Keys expire after <see cref="MessengerKeyLifetime"/>.
 /// </remarks>
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name} ({Size}b)", session.SessionId, Name, payload.Length);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var config = services.GetRequiredService<ServerConfiguration>();
        var chatHost = ZoneChannelList.AdvertisedHost(config);
        var player = services.GetRequiredService<RO2.Server.Zone.Services.PlayerStateManager>().GetPlayer(session.SessionId);
        ulong charUid;
        uint accountId;
        if (player is { CharacterUid: not 0 })
        {
            charUid = player.CharacterUid;
            accountId = player.AccountId;
        }
        else
        {
            
            
            
            charUid = session.CharacterId ?? 0u;
            accountId = session.AccountId ?? 0u;
            Logger.LogWarning("Session {Id}: 1516 without a world-entry player state; 2733 carries CharacterId {Uid} " +
                "instead of the 2310 GID, so private-room chat (23529) will not match the client's senderUid",
                session.SessionId, charUid);
        }

        
        
        
        
        var tokens = services.GetRequiredService<IOneShotTokenStore>();
        string tokenHex;
        try
        {
            tokenHex = await tokens.IssueNewAsync(
                OneShotTokens.PurposeMessenger, accountId, charUid, MessengerKeyLifetime, ct);
        }
        catch (Exception ex)
        {
            
            
            Logger.LogWarning(ex, "Session {Id}: could not issue a messenger login key; not sending 2733", session.SessionId);
            return;
        }
        var tokenBytes = Convert.FromHexString(tokenHex);  

        var ipBytes = Encoding.Unicode.GetBytes(chatHost);
        int dataLen = 8 + 16 + 2 + ipBytes.Length + 2;

        using var lk = new PacketWriter();
        lk.WriteUInt32((uint)dataLen);
        lk.WriteUInt32((uint)charUid);                 
        lk.WriteUInt32((uint)(charUid >> 32));         
        lk.WriteBytes(tokenBytes);                     
        lk.WriteUInt16((ushort)(ipBytes.Length / 2));  
        lk.WriteBytes(ipBytes);                        
        lk.WriteUInt16(ChatPort);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPlayerLoginKey, lk.GetWrittenMemory());
        Logger.LogInformation("Session {Id}: Sent AnsPlayerLoginKey (2733) chat={Host}:{Port}",
            session.SessionId, chatHost, ChatPort);
    }
}
