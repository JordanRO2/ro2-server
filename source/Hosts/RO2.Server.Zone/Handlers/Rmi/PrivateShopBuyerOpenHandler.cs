using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1481 ReqOpenPrivateShopBuyerUI: opens another player's private shop as a buyer.</summary>
/// <remarks>
/// The buyer receives four messages, in this order: the item list (2657), the craft list (2658),
/// the refine commission (3000) and the random refine commission (3006).
/// </remarks>
public class PrivateShopBuyerOpenHandler : ZoneRmiHandlerBase<PrivateShopBuyerOpenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOpenPrivateShopBuyerUI;
    public override string Name => "PrivateShopBuyerOpen";
    public PrivateShopBuyerOpenHandler(IServiceScopeFactory s, ILogger<PrivateShopBuyerOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Bytes before the entries in the item list: result, title and count.</summary>
    private const int ListHeaderBytes = 2 + PrivateShopRegistry.NameBytes + 2;

 /// <summary>Zero bytes between the item count and the first entry of the item list.</summary>
    private const int ItemListGapBytes = 12;

 /// <summary>One item list entry: 27 bytes of listing data followed by the 126-byte item record.</summary>
    private const int ItemEntryBytes = 27 + ItemPackets.ItemRecordLength;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4) return;
        uint ownerHandle = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        var owner = PlayerManager.GetAllPlayers().FirstOrDefault(p => p.EntityId == ownerHandle);
        if (owner == null || !PrivateShopRegistry.TryGet(owner, out var name, out var listings))
        {
            
            
            Logger.LogWarning("{Name} opened private shop of handle 0x{Handle:X8}, which has no shop", player.Name, ownerHandle);
            return;
        }

        
        
        
        PrivateShopRegistry.AddViewer(owner, player);

        var inv = services.GetRequiredService<IInventoryRepository>();
        var ownerItems = await inv.GetByCharacterAsync(owner.CharacterId);
        var live = PrivateShopRegistry.Live(listings, ownerItems);
        Logger.LogDebug("{Name} opens {Owner}'s private shop ({Count} items)", player.Name, owner.Name, live.Count);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenPrivateShopBuyerUI_ItemList, writer =>
        {
            writer.WriteCompactLength(ListHeaderBytes + ItemListGapBytes + ItemEntryBytes * live.Count);
            writer.WriteUInt16(0);                     
            writer.WriteBytes(name);                   
            writer.WriteUInt16((ushort)live.Count);    
            writer.WriteZeros(ItemListGapBytes);       
            
            
            
            foreach (var (listing, item) in live)
                PrivateShopItemListPackets.WriteEntry(writer, PrivateShopItemListPackets.BuyerEntry(listing, item));
        });

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsOpenPrivateShopBuyerUI_CraftList,
            PrivateShopCraftPackets.BuildBuyerCraftList2658(name, PrivateShopRegistry.GetCrafts(owner)), ct);

        
        
        
        BeginRefineShopHandler.TryGetRefineService(owner, out var refineService);
        var ownerRates = RefineCommissionPackets.RatesOf(
            StatService.ComputeEquipmentStats(services.GetRequiredService<GameDataStore>(), ownerItems));
        await session.SendRmiAsync(ZoneS2COpcodes.NfyRefineCommissionToBuyer,
            RefineCommissionPackets.BuildRefineCommissionToBuyer3000(player.EntityId, refineService, ownerRates), ct);

        
        RandomRefineCommissionHandler.TryGetCommission(owner, out var commission);
        var randomRefine = ShopItemPackets.BuildRandomRefineCommission3006(player.EntityId, commission);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyRandomRefineCommissionToBuyer, randomRefine);
    }
}
