using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1350 ReqCancleCraftItem - stop the craft batch in progress.</summary>
/// <remarks>
/// <para>
/// Request (client builder , innerSize literal 8): <c>[01 08][u16 8][u16 msgTag=58391][u32 PC handle]</c>,
/// no payload beyond the envelope.
/// </para>
/// <para>
/// Retail's answer, 2540
/// OnNotifyMakeCraftItem phase 3 with recipe 0, then 2398 back to state 0, in the same millisecond, and
/// no 2539. The items already made stay made: the first cancel came after 21 of 66 items, each already
/// delivered by its own 2379 + 2541 tick, and nothing was taken back. The old handler answered with 2539,
/// which is the craft-START ack (its success path puts the player INTO the crafting state).
/// </para>
/// <para>
/// The 2540/2398 pair also goes to the players around the crafter, as the batch's start and end do
/// (CraftItemHandler), so onlookers do not keep seeing a crafting pose. It is sent even when no batch is
/// running here (one that has just finished, or a server restart), because the client that asks to stop
/// is showing a batch and this is what takes it out of it.
/// </para>
/// </remarks>
public class CancelCraftItemHandler : ZoneRmiHandlerBase<CancelCraftItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCancleCraftItem;
    public override string Name => "CancelCraftItem";
    public CancelCraftItemHandler(IServiceScopeFactory s, ILogger<CancelCraftItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        bool wasRunning = await CraftItemHandler.StopBatchAsync(session.SessionId);
        Logger.LogInformation("{Name} cancels crafting ({State})", player.Name, wasRunning ? "batch stopped" : "no batch running");

        await CraftItemHandler.SendEndAsync(Broadcast, session, player, ShopItemPackets.CraftPhaseTerminate, 0);
    }
}
