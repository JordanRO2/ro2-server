using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1463 ReqCancelBuff — the player cancels one of its own buffs.
/// </summary>
/// <remarks>
/// <para>
/// <b>RECIPIENT FIX — this used to unicast the removal to the requester only, so nobody else ever
/// saw the buff drop.</b> 2494 OnDelEffect is a BROADCAST: a share of its subjects are NPCs, which no
/// per-owner unicast can produce, and a player also receives 2494 about their own actor, i.e. own-effects and
/// other actors' effects arrive on the same channel. It goes out through <c>BroadcastToMapAsync</c> with NO
/// exclusion, because the affected actor must receive its own copy and <c>BroadcastToNearbyAsync</c> filters
/// the source session out.
/// </para>
/// <para>
/// <b>AND 2493 OnDelEffectSuccess was missing entirely.</b> When the affected actor is a PC, the 2493 is
/// unicast to that PC IMMEDIATELY BEFORE the 2494 broadcast. Every 2494 whose subject is the receiving player is
/// preceded by a 2493, while a 2494 about another actor usually is not.
/// </para>
/// <para>
/// 2493 MUST NOT be broadcast: its client handler applies the id to the local player unconditionally, with no
/// handle to check against, and then ERASES the matching element from that player's own effect vector. A
/// broadcast copy would strip the effect from every receiving client's state.
/// </para>
/// </remarks>
public class CancelBuffHandler : ZoneRmiHandlerBase<CancelBuffHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCancelBuff;
    public override string Name => "CancelBuff";

    public CancelBuffHandler(IServiceScopeFactory s, ILogger<CancelBuffHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4) return;

        uint effectId;
        {
            var reader = new PacketReader(payload.Span);
            effectId = reader.ReadUInt32();
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var effects = services.GetRequiredService<EffectService>();
        var removed = await effects.CancelPlayerEffectAsync(player, effectId);

        
        
        
        if (removed && effectId == RidingService.RidingEffectId)
            await RidingService.DropAsync(session, player, services, effectAlreadyRemoved: true);

        Logger.LogDebug("{Name} cancels effect {EffectId}: {Result}", player.Name, effectId,
            removed ? "removed" : "not active or not cancellable, no reply");
    }
}
