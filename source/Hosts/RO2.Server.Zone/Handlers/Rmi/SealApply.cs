using Microsoft.Extensions.DependencyInjection;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Shared tail of the guardian-seal imprint: re-push the 2498 trait sheet after a seal is committed, so a
/// sealed WORN accessory's stats apply at once (same path as the refine handler). Used by the 1694 auto-apply and the
/// 1695 take-new confirm.</summary>
internal static class SealApply
{
    public static async Task PushTraitSheetAsync(ProudSession session, PlayerState player, IInventoryRepository inventory, IServiceProvider services)
    {
        if (player.Character is not { } ch) return;
        var worn = await inventory.GetEquipmentAsync(ch.Id);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
            services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, worn));
    }
}
