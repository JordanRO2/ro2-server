using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles the close of a loot window (RMI 1339, ReqCloseItemListInNPC).
/// </summary>
/// <remarks>
/// Answers with a state-0 OnChangeState for the looter, which ends the looting state. This is done only when the
/// server put the player into that state through a 1338 answered with a loot list
/// (<see cref="NpcShopOpenHandler.MarkLooting"/>). A dead player is left alone.
/// The state change is sent to the looter and to the other players on the map.
/// <para>
/// Body: <c>[01 0c][u16 12][u16 msgTag=58367][u32 own PC][u32 NPC handle]</c>. The NPC handle is not used.
/// </para>
/// </remarks>
public class NpcShopCloseHandler : ZoneRmiHandlerBase<NpcShopCloseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCloseItemListInNPC;
    public override string Name => "NpcShopClose";

    public NpcShopCloseHandler(IServiceScopeFactory s, ILogger<NpcShopCloseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!NpcShopOpenHandler.TryEndLooting(player, out var corpse))
        {
            Logger.LogDebug("Session {Id}: {Name} closed a loot window without being in the looting state; no reply",
                session.SessionId, player.Name);
            return;
        }

        if (player.IsDead)
        {
            Logger.LogDebug("Session {Id}: {Name} closed the loot window of corpse {Corpse:X8} while dead; state left as is",
                session.SessionId, player.Name, corpse);
            return;
        }

        var body = BuildLootEndState2398(player.EntityId);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnChangeState, body);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, w => w.WriteBytes(body));
        Logger.LogDebug("Session {Id}: {Name} closed the loot window of corpse {Corpse:X8}: 2398 state 0",
            session.SessionId, player.Name, corpse);
    }

 /// <summary>
 /// Builds the 2398 OnChangeState that takes <paramref name="looterHandle"/> out of the looting state (state 0, no target).
 /// </summary>
    internal static byte[] BuildLootEndState2398(uint looterHandle)
        => CombatPackets.BuildChangeState2398(looterHandle, CombatPackets.ActorState.PcNormal, 0,
            NpcShopOpenHandler.LootingStateTrailer);
}
