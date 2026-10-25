using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

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
            var sale = await PurchaseAsync(player, owner, indices, inventory, store.Items,
                gold => services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, gold),
                gold => services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(owner.CharacterId, gold));
            if (sale.Refusal != 0)
            {
                await RefuseAsync(session, player, sale.Refusal, sale.Reason, ct);
                return;
            }

            
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
    /// Carries out the purchase of the listings <paramref name="indices"/> from <paramref name="owner"/>'s shop: the host
    /// eligibility gates (not the owner, shop open, buyer viewing, every index still listed), then
    /// <see cref="StallBuyApplicationService.BuyAsync"/>, then the frames' data from the host rows. The caller holds the
    /// shop's purchase gate. Nothing is sent from here. The wallets' persistence is the optional callbacks (the balance
    /// right after the payment, buyer then seller); a refusal persists nothing.
    /// </summary>
    internal static async Task<Sale> PurchaseAsync(PlayerState buyer, PlayerState owner, IReadOnlyList<byte> indices,
        IInventoryRepository inventory, IReadOnlyDictionary<uint, ItemData> itemData,
        Func<long, Task>? persistBuyer = null, Func<long, Task>? persistSeller = null)
    {
        bool buyerIsOwner = ReferenceEquals(owner, buyer);
        List<PrivateShopRegistry.Listing>? listings = null;
        bool openAndListed = PrivateShopRegistry.IsOpen(owner) && PrivateShopRegistry.TryGet(owner, out _, out listings);
        var refusal = StallBuyRules.EligibilityRefusal(buyerIsOwner, openAndListed, PrivateShopRegistry.IsViewer(owner, buyer));
        if (refusal != 0)
            return Sale.Refused(refusal, buyerIsOwner ? "the buyer owns the shop"
                : !openAndListed ? $"{owner.Name}'s shop is not open" : $"the buyer has not opened {owner.Name}'s shop");

        var picked = new List<StallListing>();
        foreach (var index in indices.Distinct())
        {
            var listing = listings!.FirstOrDefault(l => l.Index == index);
            if (listing is null)
                return Sale.Refused(PrivateShopItemSalePackets.ResultAlreadySold, $"list index {index} is not on sale");
            picked.Add(new StallListing(listing.Index, listing.Uid, listing.Count, listing.Price));
        }

        var stall = new StallBuyInventoryAdapter(inventory, itemData, owner);
        var result = await new StallBuyApplicationService().BuyAsync(
            new StallBuyCommand(buyer.CharacterId, owner.CharacterId, picked), buyer, owner, PrivateShopRegistry.WalletLock, stall,
            persistBuyer ?? (_ => Task.CompletedTask), persistSeller ?? (_ => Task.CompletedTask));
        if (!result.Succeeded)
            return Sale.Refused(result.Refusal, result.Reason);

        return new Sale
        {
            Total = result.Total,
            BuyerGoldAfter = result.BuyerGoldAfter,
            SellerGoldAfter = result.SellerGoldAfter,
            SoldItemIds = result.SoldItemIds.ToList(),
            BuyerStacks = stall.BuyerStacks,
            SellerRecords = stall.BuildSellerRecords(),
        };
    }
}
