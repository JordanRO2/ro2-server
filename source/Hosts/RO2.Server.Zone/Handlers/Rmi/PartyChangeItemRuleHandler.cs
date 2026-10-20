using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1385 ReqPartyChangeItemRule - Change party loot rule</summary>
public class PartyChangeItemRuleHandler : ZoneRmiHandlerBase<PartyChangeItemRuleHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyChangeItemRule;
    public override string Name => "PartyChangeItemRule";

    public PartyChangeItemRuleHandler(IServiceScopeFactory s, ILogger<PartyChangeItemRuleHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        if (!PartyAnswerPackets.TryGetCompactBlob(payload.Span, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, PartyAnswerPackets.MsgTagReqPartyChangeItemRule, 18, out var handle))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyChangeItemRule ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        var lootMode = blob[8];
        var quality = blob[9];
        var looterGid = BinaryPrimitives.ReadUInt64LittleEndian(blob[10..]);

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null || party.LeaderSessionId != session.SessionId) return;

        
        
        
        
        party.ItemRule = lootMode;
        party.ItemQuality = quality;

        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyChangeItem,
            PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyChangeItem, handle));

        
        
        
        
        await partyService.BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.OnPartyNotifyChangeItem,
            PartyAnswerPackets.BuildPartyNotifyChangeItem2363(PartyAnswerPackets.PartyHandle(party), lootMode, quality));

        Logger.LogInformation("Party {PartyId}: item rule {Mode} quality {Quality} (request looter GID {Gid})",
            party.PartyId, lootMode, quality, looterGid);
    }
}
