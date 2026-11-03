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

/// <summary>RMI 1504 ReqPartyFrameMove - Reorder party member position in UI</summary>
public class PartyFrameMoveHandler : ZoneRmiHandlerBase<PartyFrameMoveHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyFrameMove;
    public override string Name => "PartyFrameMove";

    public PartyFrameMoveHandler(IServiceScopeFactory s, ILogger<PartyFrameMoveHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        var span = payload.Span;
        if (span.Length < 17) return;
        var movedGid = BinaryPrimitives.ReadUInt64LittleEndian(span);
        var occupantGid = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
        var targetSlot = span[16];
        if (movedGid == 0 || targetSlot == 0) return;

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null) return;
        if (targetSlot > PartyService.MaxMembers(party)) return;

        
        
        
        var moverId = partyService.FindMemberByGid(party, movedGid);
        if (moverId == 0) return;
        var occupantId = occupantGid == 0 ? 0u : partyService.FindMemberByGid(party, occupantGid);
        if (occupantGid != 0 && occupantId == 0) return;

        
        
        
        
        if (occupantId == 0)
        {
            
            
            foreach (var (memberId, slot) in party.FrameSlots)
            {
                if (slot != targetSlot || memberId == moverId) continue;
                occupantId = memberId;
                Logger.LogDebug("{Name} frame move: client sees slot {Slot} empty, server holds session {Id} there",
                    player.Name, targetSlot, memberId);
                break;
            }
        }

        if (occupantId != 0)
        {
            var serverSlot = PartyService.GetFrameSlot(party, occupantId);
            if (serverSlot != targetSlot)
                Logger.LogDebug("{Name} frame move: client slot {Client} but server holds the occupant at {Server}",
                    player.Name, targetSlot, serverSlot);
        }

        await partyService.SwapFrameSlotsAsync(party, session, movedGid, moverId, occupantId, targetSlot);
    }
}
