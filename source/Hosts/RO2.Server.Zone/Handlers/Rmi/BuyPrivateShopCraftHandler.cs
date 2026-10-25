using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1483 ReqBuyPrivateShopCraft - Buy craft from private shop</summary>
/// <remarks>
/// <para>
/// Request, raw 6 bytes: <c>[u32 shop owner handle][u16 craftId]</c>. The handle is the owner of the shop the
/// buyer is viewing and the id is the craft entry the buyer picked out of the owner's 2658 list. The request
/// only makes sense from a buyer whose shop window is open (PrivateShopRegistry.IsViewer).
/// </para>
/// <para>
/// The answer is immediate (not a timed batch like 1349's): the owner's recipe runs on the BUYER's materials,
/// in this order:
/// </para>
/// <list type="bullet">
/// <item>one 2329 OnUseItem per material stack taken from, carrying the buyer's handle, the stack's (slot, bag),
/// uid and the count left, with useSource 1 (CraftItemHandler's batch uses 2379, card enforce 2329
/// useSource 3). There is one per Craft_Material row, in column order, plus one when a material spans two
/// stacks; the lower slot goes to 0 first (CraftItemHandler.TakeMaterialAsync);</item>
/// <item>one 2333 OnPickupItem, result 0 and kind 0, with the product's stack as it now stands: its item is
/// Craft_ItemList.Craft_Item_ID, and Craft_ItemNum is 1 on every bought recipe. A product that joins an
/// existing stack carries that stack's count plus one; otherwise it is a new stack holding 1;</item>
/// <item>2660 AnsBuyPrivateShopCraft, raw 16 bytes, result 0 and the request's craftId, then the
/// buyer's wallet after paying the offer's price and the crafted item id
/// (PrivateShopCraftPackets.BuildBuyPrivateShopCraft2660).</item>
/// </list>
/// <para>
/// No 2435 and no 2379 are in the buyer's burst: the 2660's u64 is the wallet update. The seller gets 2654
/// NfyCraftSaleShopItemBuy right after the buyer's 2660, matching on craftId and on the buyer's handle, with its
/// wallet raised by exactly the price (PrivateShopCraftPackets.BuildCraftSaleShopItemBuy2654). The seller
/// gets no 2435 either. It also gets a 2986 with each 2654 (the job-mastery gain for the crafter); this server
/// has no job-mastery points (WorldEntryService.JobMasteryPoints is 0), so none is sent, as in CraftItemHandler.
/// </para>
/// <para>
/// Refusals: the 2660 has a result code (a non-zero one goes to SysMsg_ShowResultCode_23000_23059 on the
/// client), but no refusal code is defined here, so a buyer who is short of materials, zeny or
/// bag room, a craft that is not on offer and a shop the buyer is not looking at are logged and left
/// unanswered rather than given an invented code. As in CraftItemHandler, Craft_Level and learned recipes are
/// not checked: this server keeps no craft level and does not store 1348.
/// </para>
/// </remarks>
public class BuyPrivateShopCraftHandler : ZoneRmiHandlerBase<BuyPrivateShopCraftHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBuyPrivateShopCraft;
    public override string Name => "BuyPrivateShopCraft";
    public BuyPrivateShopCraftHandler(IServiceScopeFactory s, ILogger<BuyPrivateShopCraftHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Request length: u32 owner handle + u16 craftId, 6 bytes.</summary>
    private const int RequestBytes = 6;

 /// <summary>2329 useSource of a private-shop craft sale's material: always 1 (see remarks).</summary>
    private const byte MaterialUseSource = 1;

 /// <summary>Serialises the seller credit: two buyers can buy from one seller at the same time, and the
 /// wallet read-modify-write would otherwise lose one of the two prices.</summary>

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBytes)
        {
            Logger.LogWarning("Session {Id}: BuyPrivateShopCraft body is {Len} bytes, not the 6 retail sends",
                session.SessionId, payload.Length);
            return;
        }
        var span = payload.Span;
        uint ownerHandle = BinaryPrimitives.ReadUInt32LittleEndian(span);
        ushort craftId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2));

        var owner = PlayerManager.GetAllPlayers().FirstOrDefault(p => p.EntityId == ownerHandle);
        if (owner is null || !PrivateShopRegistry.IsViewer(owner, player))
        {
            Logger.LogInformation("{Name} asked to buy craft {CraftId} from 0x{Owner:X8}, whose shop it does not have open; not answered",
                player.Name, craftId, ownerHandle);
            return;
        }
        var offer = PrivateShopRegistry.GetCrafts(owner).FirstOrDefault(c => c.CraftId == craftId);
        if (offer is null || offer.Price > long.MaxValue)
        {
            Logger.LogInformation("{Name} asked to buy craft {CraftId} from {Owner}, who does not offer it; not answered",
                player.Name, craftId, owner.Name);
            return;
        }
        long price = (long)offer.Price;

        var store = services.GetRequiredService<GameDataStore>();
        var recipe = store.CraftItemList.FirstOrDefault(r => r.ID == craftId);
        var materialRow = store.CraftMaterial.FirstOrDefault(m => m.ID == craftId);
        if (recipe is null || materialRow is null || recipe.Craft_Item_ID == 0)
        {
            Logger.LogInformation("{Name}: craft {CraftId} of {Owner}'s shop is not in Craft_ItemList/Craft_Material; not answered",
                player.Name, craftId, owner.Name);
            return;
        }
        var materials = CraftItemHandler.MaterialsOf(materialRow);
        if (materials.Count == 0)
        {
            Logger.LogInformation("{Name}: craft {CraftId} lists no materials; not answered", player.Name, craftId);
            return;
        }
        if (player.Gold < price)
        {
            Logger.LogInformation("{Name} has {Gold} zeny, {Owner}'s craft {CraftId} costs {Price}; not answered",
                player.Name, player.Gold, owner.Name, craftId, price);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var bag = await inventory.GetByCharacterAsync(player.CharacterId);
        if (!CraftItemHandler.CoversOneItem(bag, materials))
        {
            Logger.LogInformation("{Name} lacks the materials of craft {CraftId} from {Owner}'s shop; not answered",
                player.Name, craftId, owner.Name);
            return;
        }
        int quantity = CraftItemHandler.ProductPerTick(recipe);
        byte productMax = store.GetItem(recipe.Craft_Item_ID)?.Stack_Max ?? (byte)1;
        var room = CraftItemHandler.PlanProduct(bag, materials, recipe.Craft_Item_ID, quantity, productMax, store.Items);
        if (room is null)
        {
            Logger.LogInformation("{Name} has no free bag cell for craft {CraftId} from {Owner}'s shop; not answered",
                player.Name, craftId, owner.Name);
            return;
        }

        
        
        if (!player.TryDebit(price))
        {
            Logger.LogInformation("{Name} can no longer pay {Price} for {Owner}'s craft {CraftId}; not answered",
                player.Name, price, owner.Name, craftId);
            return;
        }

        
        foreach (var (itemId, count) in materials)
            foreach (var stack in await CraftItemHandler.TakeMaterialAsync(inventory, bag, itemId, count))
            {
                var (bagId, slot) = ItemWorkbench.ClientBagSlot(stack);
                var use = CombatPackets.BuildUseItem2329(player.EntityId, slot, bagId, stack.Id,
                    (byte)Math.Clamp(stack.Count, 0, 255), MaterialUseSource);
                await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, use, ct);
            }

        
        var placed = await CraftItemHandler.PlaceProductAsync(inventory, player.CharacterId, recipe.Craft_Item_ID,
            quantity, productMax, room);
        foreach (var stack in placed)
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack)), ct);

        
        
        var characters = services.GetRequiredService<ICharacterRepository>();
        await characters.UpdateGoldAsync(player.CharacterId, player.Gold);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsBuyPrivateShopCraft,
            PrivateShopCraftPackets.BuildBuyPrivateShopCraft2660(craftId, player.Gold, recipe.Craft_Item_ID), ct);

        long sellerGold;
        lock (PrivateShopRegistry.WalletLock)
        {
            owner.Credit(price);
            sellerGold = owner.Gold;
        }
        await characters.UpdateGoldAsync(owner.CharacterId, sellerGold);
        var sellerSession = Broadcast.GetSession(owner.SessionId);
        if (sellerSession is not null)
            await sellerSession.SendRmiAsync(ZoneS2COpcodes.NfyCraftSaleShopItemBuy,
                PrivateShopCraftPackets.BuildCraftSaleShopItemBuy2654(craftId, player.EntityId, sellerGold), ct);

        Logger.LogInformation("{Name} bought craft {CraftId} ('{Item}') from {Owner}'s shop for {Price} zeny",
            player.Name, craftId, recipe.Craft_ItemName, owner.Name, price);
    }
}
