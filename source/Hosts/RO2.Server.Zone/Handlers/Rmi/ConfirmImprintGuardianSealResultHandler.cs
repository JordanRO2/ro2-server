using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1695 ReqConfirmImprintGuardianSealResult: the player's keep/take choice for a guardian-seal re-roll.</summary>
/// <remarks>
/// Body is 1 byte (live frame is 1 + a 4-byte transport trailer): 0 = keep the old line (Previous), 1 = take the new
/// roll (Change). Retail sends no 3032 answer (0 samples); the client applies the choice to its own view. The server
/// persists it: a take-new writes the <see cref="PlayerState.PendingSeal"/> roll onto the accessory and re-pushes 2498;
/// a keep-old drops the pending roll, leaving the old line. An empty accessory never gets here (it auto-applies at 1694).
/// </remarks>
public class ConfirmImprintGuardianSealResultHandler : ZoneRmiHandlerBase<ConfirmImprintGuardianSealResultHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqConfirmImprintGuardianSealResult;
    public override string Name => "ConfirmImprintGuardianSealResult";
    public ConfirmImprintGuardianSealResultHandler(IServiceScopeFactory s, ILogger<ConfirmImprintGuardianSealResultHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 1) return;
        var choice = payload.Span[0];   // 0 = keep old, 1 = take new

        if (player.PendingSeal is { } pend)
        {
            player.PendingSeal = null;
            if (choice != 0)   // take new: persist the staged roll
            {
                var inventory = services.GetRequiredService<IInventoryRepository>();
                var target = await inventory.GetByIdAsync(pend.Uid);
                if (target is not null && target.CharacterId == player.CharacterId)
                {
                    target.SealJson = SealLines.Serialize([new SealLine(pend.Kind, pend.Type, pend.Value)]);
                    await inventory.UpdateItemAsync(target);
                    await SealApply.PushTraitSheetAsync(session, player, inventory, services);
                    Logger.LogInformation("{Name} took the new seal on uid {Uid}: type {Ty} value {V}",
                        player.Name, pend.Uid, pend.Type, pend.Value);
                }
            }
            else
            {
                Logger.LogDebug("{Name} kept the old seal on uid {Uid}", player.Name, pend.Uid);
            }
        }

        // Ack with 3032 (empty body) so the client finalizes: its handler calls GetItemActor @0x4522b0, which applies
        // the staged line it picked (ApplyEnchantData) and resets the window to ready. Without this the compare menu
        // stays open and nothing visibly changes, even though the choice was already persisted above.
        await session.SendRmiAsync(ZoneS2COpcodes.AnsConfirmImprintGuardianSealResult, _ => { });
    }
}
