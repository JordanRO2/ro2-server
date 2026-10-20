using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1482 ReqBuyPrivateShopItem: buys listings from another player's private shop.</summary>
/// <remarks>
/// Request: u32 owner handle, u16 count, then one u8 listing index per item. Each listing is bought whole.
/// The purchase needs the shop to be open, the buyer to be viewing it, every listing to still be available,
/// and the buyer to have enough zeny and bag room. Bought items move to the buyer's bag, merging into existing stacks where they fit.
/// On success the buyer receives the items (2333 per stack) and the answer (2659), the remaining listings are sent
/// to the buyer and to other viewers (2662), and the seller receives the sale notices (2379, 2655, 2656).
/// On refusal nothing moves and a 2659 carries the refusal code.
/// </remarks>
public class PrivateShopBuyItemHandler : ZoneRmiHandlerBase<PrivateShopBuyItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBuyPrivateShopItem;
    public override string Name => "PrivateShopBuyItem";
    public PrivateShopBuyItemHandler(IServiceScopeFactory s, ILogger<PrivateShopBuyItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!PrivateShopItemSalePackets.TryParseBuyRequest1482(payload.Span, out uint ownerHandle, out var indices))
        {
            Logger.LogWarning("Session {Id}: ReqBuyPrivateShopItem body of {Len} bytes is not [compact][u32 owner][u16 n][n indices]",
                session.SessionId, payload.Length);
            return;
        }
        if (indices.Length == 0)
        {
            
            Logger.LogInformation("{Name} sent a private-shop purchase with no item; not answered", player.Name);
            return;
        }

        var owner = PlayerManager.GetAllPlayers().FirstOrDefault(p => p.EntityId == ownerHandle);
        var gate = owner is null ? null : PrivateShopRegistry.PurchaseGate(owner);
        if (owner is null || gate is null)
        {
            await RefuseAsync(session, player, PrivateShopItemSalePackets.ResultShopClosed,
                $"no shop of handle 0x{ownerHandle:X8} in this zone", ct);
            return;
        }

        await gate.WaitAsync(ct);
        try
        {
            var inventory = services.GetRequiredService<IInventoryRepository>();
            var store = services.GetRequiredService<GameDataStore>();
            var sale = await PurchaseAsync(player, owner, indices, inventory, store.Items);
            if (sale.Refusal != 0)
            {
                await RefuseAsync(session, player, sale.Refusal, sale.Reason, ct);
                return;
            }

            var characters = services.GetRequiredService<ICharacterRepository>();
            await characters.UpdateGoldAsync(player.CharacterId, sale.BuyerGoldAfter);
            await characters.UpdateGoldAsync(owner.CharacterId, sale.SellerGoldAfter);

            
            foreach (var stack in sale.BuyerStacks)
                await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                    LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack)), ct);
            
            
            await session.SendRmiAsync(ZoneS2COpcodes.AnsBuyPrivateShopItem,
                PrivateShopItemSalePackets.BuildBuyAnswer2659(sale.Total, sale.BuyerGoldAfter, indices), ct);

            
            
            
            if (!PrivateShopRegistry.TryGet(owner, out var title, out var listings))
            {
                title = new byte[PrivateShopRegistry.NameBytes];
                listings = new();
            }
            var remaining = PrivateShopRegistry.Live(listings, await inventory.GetByCharacterAsync(owner.CharacterId));

            
            
            var update = PrivateShopItemListPackets.BuildItemListUpdate2662(player.EntityId,
                remaining.Select(r => PrivateShopItemListPackets.BuyerEntry(r.Listing, r.Item)).ToList());
            await session.SendRmiAsync(ZoneS2COpcodes.NfyAnsBuyPrivateShopItem, update, ct);
            foreach (var (viewerSessionId, viewerCharacterId) in PrivateShopRegistry.ViewersSnapshot(owner))
            {
                if (viewerSessionId == player.SessionId) continue;
                var viewer = PlayerManager.GetPlayer(viewerSessionId);
                if (viewer is null || viewer.CharacterId != viewerCharacterId || !PrivateShopRegistry.IsWatching(owner, viewer)) continue;
                var viewerSession = Broadcast.GetSession(viewerSessionId);
                if (viewerSession is not null)
                    await viewerSession.SendRmiAsync(ZoneS2COpcodes.NfyAnsBuyPrivateShopItem, update, ct);
            }

            
            var sellerSession = Broadcast.GetSession(owner.SessionId);
            if (sellerSession is not null)
            {
                foreach (var record in sale.SellerRecords)
                    await sellerSession.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
                        ShopItemPackets.BuildDestroyItem2379(owner.EntityId, record), ct);
                await sellerSession.SendRmiAsync(ZoneS2COpcodes.NfyPrivateShopItemSaled,
                    PrivateShopSalePackets.BuildItemSaled2655(title, player.EntityId, sale.SellerGoldAfter,
                        remaining.Select(r => PrivateShopItemListPackets.SellerEntry(r.Listing, r.Item)).ToList()), ct);
                await sellerSession.SendRmiAsync(ZoneS2COpcodes.NfyPrivateShopSaledList,
                    PrivateShopSalePackets.BuildSaledList2656(player.EntityId, sale.SoldItemIds), ct);
            }

            
            foreach (var templateId in sale.SoldItemIds.Distinct())
            {
                await KharaService.OnHeldItemChangedAsync(session, player, templateId, services, Logger, ct);
                if (sellerSession is not null)
                    await KharaService.OnHeldItemChangedAsync(sellerSession, owner, templateId, services, Logger, ct);
            }
            await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, sale.SoldItemIds, services, Logger);

            Logger.LogInformation("{Name} bought {Count} listing(s) ({Items}) from {Owner}'s private shop for {Total} zeny",
                player.Name, sale.SoldItemIds.Count, string.Join(",", sale.SoldItemIds), owner.Name, sale.Total);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RefuseAsync(ProudSession session, PlayerState player, ushort code, string reason, CancellationToken ct)
    {
        Logger.LogInformation("{Name}: private-shop purchase refused with {Code} ({Reason})", player.Name, code, reason);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsBuyPrivateShopItem, PrivateShopItemSalePackets.BuildBuyRefusal2659(code), ct);
    }

 /// <summary>What a 1482 did: a refusal code (and why), or the sale as carried out.</summary>
    internal sealed class Sale
    {
 /// <summary>0 when the purchase went through, else the 2659 code (nothing moved).</summary>
        public ushort Refusal { get; init; }
        public string Reason { get; init; } = "";
 /// <summary>Sum of the bought listings' prices, the zeny that moved.</summary>
        public ulong Total { get; init; }
        public long BuyerGoldAfter { get; init; }
        public long SellerGoldAfter { get; init; }
 /// <summary>ItemInfo id of each bought listing, in the request's order (2656's list).</summary>
        public List<uint> SoldItemIds { get; init; } = new();
 /// <summary>The buyer's bag stacks that received items, as they now stand (one 2333 each).</summary>
        public List<InventoryItem> BuyerStacks { get; init; } = new();
 /// <summary>The seller's sold stacks as they now stand, 126-byte records (one 2379 each).</summary>
        public List<byte[]> SellerRecords { get; init; } = new();

        internal static Sale Refused(ushort code, string reason) => new() { Refusal = code, Reason = reason };
    }

 /// <summary>
 /// Carries out the purchase of the listings <paramref name="indices"/> from <paramref name="owner"/>'s shop.
 /// Checks everything first, then moves the items and zeny and removes the listings. The caller holds the
 /// shop's purchase gate. Nothing is sent from here.
 /// </summary>
    internal static async Task<Sale> PurchaseAsync(PlayerState buyer, PlayerState owner, IReadOnlyList<byte> indices,
        IInventoryRepository inventory, IReadOnlyDictionary<uint, ItemData> itemData)
    {
        if (ReferenceEquals(owner, buyer))
            return Sale.Refused(PrivateShopItemSalePackets.ResultWrongBuyer, "the buyer owns the shop");
        if (!PrivateShopRegistry.IsOpen(owner) || !PrivateShopRegistry.TryGet(owner, out _, out var listings))
            return Sale.Refused(PrivateShopItemSalePackets.ResultShopClosed, $"{owner.Name}'s shop is not open");
        if (!PrivateShopRegistry.IsViewer(owner, buyer))
            return Sale.Refused(PrivateShopItemSalePackets.ResultWrongBuyer, $"the buyer has not opened {owner.Name}'s shop");

        
        var picked = new List<PrivateShopRegistry.Listing>();
        foreach (var index in indices.Distinct())
        {
            var listing = listings.FirstOrDefault(l => l.Index == index);
            if (listing is null)
                return Sale.Refused(PrivateShopItemSalePackets.ResultAlreadySold, $"list index {index} is not on sale");
            picked.Add(listing);
        }
        var sold = PrivateShopRegistry.Live(picked, await inventory.GetByCharacterAsync(owner.CharacterId));
        if (sold.Count != picked.Count)
            return Sale.Refused(PrivateShopItemSalePackets.ResultAlreadySold, "a listed item left the seller's bag");

        ulong total = 0;
        foreach (var (listing, _) in sold)
        {
            if (listing.Price > ulong.MaxValue - total)
                return Sale.Refused(PrivateShopItemSalePackets.ResultNotEnoughMoney, "the total overflows");
            total += listing.Price;
        }
        if (total > long.MaxValue || buyer.Gold < (long)total)
            return Sale.Refused(PrivateShopItemSalePackets.ResultNotEnoughMoney, $"{buyer.Gold} zeny for {total}");

        var plan = PlanPlacement(sold, await inventory.GetByCharacterAsync(buyer.CharacterId), itemData);
        if (plan is null)
            return Sale.Refused(PrivateShopItemSalePackets.ResultNoBagRoom, "no free bag cell");

        long price = (long)total;
        long buyerAfter, sellerAfter;
        lock (PrivateShopRegistry.WalletLock) 
        {
            if (owner.Gold > long.MaxValue - price)
                return Sale.Refused(PrivateShopItemSalePackets.ResultSellerMoneyLimit, $"seller wallet {owner.Gold} + {total}");
            if (!buyer.TryDebit(price))
                return Sale.Refused(PrivateShopItemSalePackets.ResultNotEnoughMoney, $"{buyer.Gold} zeny for {total}");
            owner.Credit(price);
            if (buyer.Character is { } buyerCharacter) buyerCharacter.Gold = buyer.Gold;
            if (owner.Character is { } sellerCharacter) sellerCharacter.Gold = owner.Gold;
            buyerAfter = buyer.Gold;
            sellerAfter = owner.Gold;
        }

        
        var leavingWhole = new Dictionary<InventoryItem, byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var (listing, item) in sold)
            if (listing.Count >= item.Count)
            {
                var record = WorldEntryService.BuildItemRecord126(item);
                record[14] = 0; 
                leavingWhole[item] = record;
            }

        var placed = await ApplyPlacementAsync(inventory, plan, buyer.CharacterId);

        foreach (var (listing, _) in sold)
            PrivateShopRegistry.TryRemove(owner, listing.Index);

        return new Sale
        {
            Total = total,
            BuyerGoldAfter = buyerAfter,
            SellerGoldAfter = sellerAfter,
            SoldItemIds = sold.Select(s => s.Item.ItemId).ToList(),
            BuyerStacks = placed,
            SellerRecords = sold.Select(s => leavingWhole.TryGetValue(s.Item, out var gone)
                ? gone : WorldEntryService.BuildItemRecord126(s.Item)).ToList(),
        };
    }

 /// <summary>
 /// Plans where each bought listing goes in the buyer's bag, without writing: first into the buyer's stacks of
 /// the same item up to Stack_Max, then into free cells. Null when a listing needs a cell and none is left.
 /// </summary>
    private static Placement? PlanPlacement(List<(PrivateShopRegistry.Listing Listing, InventoryItem Item)> sold,
        IReadOnlyList<InventoryItem> buyerInventory, IReadOnlyDictionary<uint, ItemData> itemData)
    {
        var plan = new Placement();
        var freeCells = BagCapacity.FreeCells(buyerInventory, itemData);
        int nextFree = 0;
        var targets = buyerInventory
            .Where(i => i.StorageType == 0 && i.Slot >= 0)
            .OrderBy(i => i.Slot)
            .Select(i => new MergeTarget(i.ItemId, i.Count) { Held = i })
            .ToList();

        foreach (var (listing, item) in sold)
        {
            int max = itemData.TryGetValue(item.ItemId, out var data) ? data.Stack_Max : 0;
            int remaining = listing.Count;
            if (max > 1)
            {
                foreach (var target in targets)
                {
                    if (remaining == 0) break;
                    if (target.ItemId != item.ItemId || target.Count >= max) continue;
                    int add = Math.Min(max - target.Count, remaining);
                    target.Count += add;
                    remaining -= add;
                    if (target.Held is { } held)
                    {
                        if (!plan.HeldCounts.ContainsKey(held)) plan.Touched.Add(held);
                        plan.HeldCounts[held] = target.Count;
                    }
                    else target.Created!.CellCount = target.Count;
                }
            }

            var move = new PlannedMove(item, listing.Count);
            if (remaining > 0)
            {
                if (nextFree >= freeCells.Count) return null;
                move.Cell = freeCells[nextFree++];
                move.CellCount = remaining;
                targets.Add(new MergeTarget(item.ItemId, remaining) { Created = move });
                plan.Touched.Add(move);
            }
            plan.Moves.Add(move);
        }
        return plan;
    }

 /// <summary>
 /// Writes a placement plan and returns the buyer's stacks that received items, in the order they were first filled.
 /// </summary>
    private static async Task<List<InventoryItem>> ApplyPlacementAsync(IInventoryRepository inventory, Placement plan, uint buyerCharacterId)
    {
        foreach (var (held, count) in plan.HeldCounts)
        {
            held.Count = count;
            await inventory.UpdateItemAsync(held);
        }

        foreach (var move in plan.Moves)
        {
            var it = move.Item;
            bool wholeStack = move.Qty >= it.Count;
            if (move.Cell < 0)
            {
                if (wholeStack) await inventory.RemoveItemAsync(it.Id);
                else
                {
                    it.Count -= move.Qty;
                    await inventory.UpdateItemAsync(it);
                }
                continue;
            }
            if (!wholeStack)
            {
                it.Count -= move.Qty;
                await inventory.UpdateItemAsync(it);
                var row = new InventoryItem
                {
                    CharacterId = buyerCharacterId,
                    ItemId = it.ItemId,
                    Count = move.CellCount,
                    Slot = move.Cell,
                    StorageType = 0,
                }.CopyInstanceStateFrom(it);
                await inventory.AddItemAsync(row);
                move.Placed = row;
            }
            else
            {
                it.CharacterId = buyerCharacterId;
                it.StorageType = 0;
                it.Slot = move.Cell;
                it.Count = move.CellCount;
                await inventory.UpdateItemAsync(it);
                move.Placed = it;
            }
        }

        return plan.Touched.Select(t => t as InventoryItem ?? ((PlannedMove)t).Placed!).ToList();
    }

    private sealed class Placement
    {
        public Dictionary<InventoryItem, int> HeldCounts { get; } = new(ReferenceEqualityComparer.Instance);
        public List<PlannedMove> Moves { get; } = new();
 /// <summary>Held stacks (InventoryItem) and cell moves (PlannedMove), in the order they were first filled.</summary>
        public List<object> Touched { get; } = new();
    }

 /// <summary>A bought item and its destination; <see cref="Cell"/> -1 means it merged completely.</summary>
    private sealed class PlannedMove(InventoryItem item, int qty)
    {
        public InventoryItem Item { get; } = item;
        public int Qty { get; } = qty;
        public short Cell { get; set; } = -1;
        public int CellCount { get; set; }
        public InventoryItem? Placed { get; set; }
    }

 /// <summary>A stack a bought item may merge into: one the buyer holds, or a cell planned for an earlier listing.</summary>
    private sealed class MergeTarget(uint itemId, int count)
    {
        public uint ItemId { get; } = itemId;
        public int Count { get; set; } = count;
        public InventoryItem? Held { get; init; }
        public PlannedMove? Created { get; init; }
    }
}
