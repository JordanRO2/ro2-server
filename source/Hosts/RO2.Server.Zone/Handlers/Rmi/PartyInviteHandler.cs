using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1382 ReqPartyInvitation - Invite player to party</summary>
public class PartyInviteHandler : ZoneRmiHandlerBase<PartyInviteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyInvitation;
    public override string Name => "PartyInvite";

    public PartyInviteHandler(IServiceScopeFactory s, ILogger<PartyInviteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        if (payload.Length < 52) return; 
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var targetName = reader.ReadWideStringFixed(21).TrimEnd('\0');
        if (targetName.Length == 0) return;

        var target = PlayerManager.GetPlayerByName(targetName);
        if (target == null)
        {
            
            
            
            
            
            Logger.LogDebug("{Name} invited '{Target}', who is not online", player.Name, targetName);
            return;
        }

        
        var partyService = services.GetRequiredService<PartyService>();
        await partyService.InviteToPartyAsync(player, target, targetName);
    }
}
