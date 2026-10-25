using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1475 ReqDeletePrivateShopItem: takes a listing off the seller's private shop.</summary>
/// <remarks>
/// Request: 1 byte, the listing index handed out by the add request.
/// Answer: 2648 AnsDeletePrivateShopItem with the shop title and the seller's remaining live listings.
/// An index that is not listed still receives the current list, so the client rebuilds from server state.
/// </remarks>
public class PrivateShopDeleteItemHandler : ZoneRmiHandlerBase<PrivateShopDeleteItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqDeletePrivateShopItem;
    public override string Name => "PrivateShopDeleteItem";
    public PrivateShopDeleteItemHandler(IServiceScopeFactory s, ILogger<PrivateShopDeleteItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length != 1)
        {
            Logger.LogWarning("{Name}: 1475 body is {Len} bytes (the client writes 1), ignored", player.Name, payload.Length);
            return;
        }
        byte index = payload.Span[0];

        if (PrivateShopRegistry.TryRemove(player, index))
            Logger.LogInformation("{Name} takes private shop listing {Index} off sale", player.Name, index);
        else
            Logger.LogWarning("{Name} asked to take private shop listing {Index} off sale, which is not listed; answering with the current list",
                player.Name, index);

        
        
        
        
        
        
        
        
        byte[] title;
        List<(PrivateShopRegistry.Listing Listing, RO2.Inventory.Domain.InventoryItem Item)> live;
        if (PrivateShopRegistry.TryGet(player, out var name, out var listings))
        {
            title = PrivateShopRegistry.IsOpen(player) ? name : new byte[PrivateShopRegistry.NameBytes];
            var inv = services.GetRequiredService<IInventoryRepository>();
            live = PrivateShopRegistry.Live(listings, await inv.GetByCharacterAsync(player.CharacterId));
        }
        else
        {
            title = new byte[PrivateShopRegistry.NameBytes];
            live = new();
        }

        var body = PrivateShopItemPackets.BuildAnsDeletePrivateShopItem2648(title, PrivateShopItemPackets.SellerRows(live));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsDeletePrivateShopItem, body, ct);
    }
}
