using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1387 ReqPartyChangeMaster - Transfer party leadership</summary>
public class PartyChangeMasterHandler : ZoneRmiHandlerBase<PartyChangeMasterHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyChangeMaster;
    public override string Name => "PartyChangeMaster";

    
    private const ushort MsgTagReqPartyChangeMaster = 58150;

    public PartyChangeMasterHandler(IServiceScopeFactory s, ILogger<PartyChangeMasterHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        var span = payload.Span;
        if (span.Length < 18 || span[0] != 1 || span[1] != 16) return;
        if (BinaryPrimitives.ReadUInt16LittleEndian(span[4..]) != MsgTagReqPartyChangeMaster) return;
        var requesterHandle = BinaryPrimitives.ReadUInt32LittleEndian(span[6..]);   
        var newLeaderGid = BinaryPrimitives.ReadUInt64LittleEndian(span[10..]);     
        if (newLeaderGid == 0) return;

        var partyService = services.GetRequiredService<PartyService>();

        
        
        
        
        
        
        
        
        
        
        
        
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party != null && party.LeaderSessionId == session.SessionId)
        {
            var targetId = partyService.FindMemberByGid(party, newLeaderGid);
            var target = targetId == 0 || targetId == session.SessionId ? null : partyService.GetOnlineMember(party, targetId);
            if (target != null && RefusedAsWoe(services.GetRequiredService<GameDataStore>(), player.MapId, target.MapId))
            {
                Logger.LogDebug("{Name}: leader change to {Target} refused, WoE map {Map} vs {TargetMap}",
                    player.Name, target.Name, player.MapId, target.MapId);
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyChangeMaster,
                    PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyChangeMaster, requesterHandle,
                        PartyAnswerPackets.ResultChangeMasterWoeMap));
                return;
            }
        }

        
        
        await partyService.ChangeLeaderAsync(session.SessionId, requesterHandle, newLeaderGid);
    }

 /// <summary>Map_List.Map_Type of the WoE fortresses 3101 and 3201, the only rows that carry it.</summary>
    public const byte WoeMapType = 4;

 /// <summary>
 /// True when a leader on <paramref name="requesterMapId"/> may not hand the party to a member on
 /// <paramref name="targetMapId"/>: the leader stands on a WoE map and the member is elsewhere.
 /// </summary>
    public static bool RefusedAsWoe(GameDataStore data, uint requesterMapId, uint targetMapId)
        => targetMapId != requesterMapId && data.GetMap(requesterMapId)?.Map_Type == WoeMapType;
}
