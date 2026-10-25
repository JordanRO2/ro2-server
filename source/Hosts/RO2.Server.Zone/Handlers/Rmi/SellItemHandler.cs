using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

using RO2.Economy.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1335 ReqSellItem. Payload (compact-BA header included, same convention as BuyItemHandler):
///   +6 u32 sellerHandle, +10 u32 shopNpcHandle, +14 u32 ctx, +18 u8 count,
///   then count x { u64 itemUid, u8 qty } starting at +19.
/// Items are keyed by their 64-bit instance uid, NOT the template id.
/// </summary>
public class SellItemHandler : ZoneRmiHandlerBase<SellItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSellItem;
    public override string Name => "SellItem";

    // 1335 request layout, as offsets into the payload (compact-BA header included).
    private const int ShopNpcHandleOffset = 10;
    private const int EntryCountOffset = 18;
    private const int FirstEntryOffset = 19;
    /// <summary>One sell entry: u64 item uid + u8 quantity.</summary>
    private const int EntrySize = sizeof(ulong) + sizeof(byte);

    /// <summary>InventoryItem.StorageType of the bag; worn and banked items use other values and cannot be sold.</summary>
    private const byte StorageBag = 0;

    /// <summary>Offset of the u8 stack count inside a 126-byte item record.</summary>
    private const int ItemRecordCountOffset = 14;

    /// <summary>The u8 stack count saturates at this value.</summary>
    private const int MaxRecordCount = byte.MaxValue;

    public SellItemHandler(IServiceScopeFactory s, ILogger<SellItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < FirstEntryOffset) return;
        // Extract the sell entries BEFORE any await — a ReadOnlySpan cannot live across an await.
        var entries = new List<(ulong uid, int qty)>();
        uint shopNpcHandle;
        {
            var span = payload.Span;
            shopNpcHandle = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(ShopNpcHandleOffset));
            var count = span[EntryCountOffset];
            for (int k = 0; k < count; k++)
            {
                var o = FirstEntryOffset + EntrySize * k;
                if (o + EntrySize > span.Length) break;
                entries.Add((System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(o)),
                    Math.Max(1, (int)span[o + sizeof(ulong)])));
            }
        }

        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();

        // The "nothing sold" 2376: result 1 with the CURRENT wallet, the buy-back list unchanged and k = 0.
        // Used for every refusal: nothing is credited or removed.
        async Task RefuseNothingSoldAsync()
        {
            var refusal = ShopItemPackets.BuildSellItem2376(player.EntityId, ShopItemPackets.SellResultNothingSold,
                player.Gold, BuyBackLedger.UidsOf(player), System.Array.Empty<byte[]>());
            await session.SendRmiAsync(ZoneS2COpcodes.OnSellItem, writer => writer.WriteBytes(refusal));
        }

        // Proximity: the seller must be alive and near the shop NPC the 1335 names (payload+10). An
        // unresolved handle is a refusal, as in BuyItemHandler. Same map is the hard rule; the distance
        // is a loose guard (ActorGate.NpcInteractReach).
        var shopNpc = services.GetRequiredService<NpcStateManager>().GetNpc(shopNpcHandle);
        var reach = ActorGate.CanTransact(player, shopNpc, ActorGate.NpcInteractReach);
        if (reach != ActorRefusal.None)
        {
            Logger.LogInformation("{Name} tried to sell to NPC handle 0x{Npc:X8}; refused: {Why}",
                player.Name, shopNpcHandle, reach);
            await RefuseNothingSoldAsync();
            return;
        }

        long proceeds = 0;
        var records = new List<byte[]>();
        var soldTemplates = new List<uint>();
        // The whole load-decide-write loop runs under this character's gate, so a second concurrent
        // session (another sell, a trade-away, a mail attach) cannot interleave between the read and the write
        // of a stack and get it paid for twice. Each per-item write is also atomic and owner-checked, which is
        // the cross-process backstop the in-process gate cannot give.
        await CharacterInventoryGate.RunAsync(player.CharacterId, async () =>
        {
        foreach (var (itemUid, qty) in entries)
        {
            var it = await inventory.GetByIdAsync(itemUid);
            // Ownership guard: uids are global — only the owner may sell the item.
            if (it is null || it.CharacterId != player.CharacterId) continue;
            // Only the bag (StorageType 0) can be sold: an equipped (1) or banked uid is owned too, and without this
            // it would be paid for and deleted from where it is worn or stored.
            if (it.StorageType != StorageBag) continue;
            // The template must allow a sale (ItemInfo Is_Sell != 0). An unknown template is skipped too: its
            // price and flag are not known.
            var tmpl = store.GetItem(it.ItemId);
            if (tmpl is null || tmpl.Is_Sell == 0) continue;
            // Gold-dupe guard: NEVER pay for more than the owned stack. The client sends a u8 count
            // (1..255); without this clamp, selling qty > it.Count still credits unit*qty while the
            // stack is only removed once — free gold.
            var sellQty = ShopPricing.SellQty(qty, it.Count);
            if (sellQty <= 0) continue;
            var newCount = it.Count - sellQty;

            // Take the stack atomically and owner-checked BEFORE crediting: a whole-stack sale is an
            // owner-filtered delete, a partial one an $inc guarded by the live count. If the guard fails
            // the row no longer holds what we would pay out (a concurrent session already removed or
            // shrank it), so nothing is credited, recorded or put on the buy-back list — no gold is minted.
            var removed = newCount <= 0
                ? await inventory.RemoveItemAsync(itemUid, player.CharacterId)
                : (await inventory.AdjustCountAsync(itemUid, player.CharacterId, -sellQty)).HasValue;
            if (!removed) continue;

            // Each item carries its OWN sell price (ItemInfo Price_Sell) — it is NOT half the buy price.
            // e.g. 21330011 buys at 250 but sells for 1; 30000311 buys at 100000 but sells for 100.
            var unit = tmpl.Price_Sell;
            proceeds += ShopPricing.SellProceeds(unit, sellQty);

            // Build the record from the item as it was (its pre-sale count), then FORCE the stack-count
            // byte. WriteItemInstance clamps record+0x0E to a minimum of 1, so a fully-sold stack can only
            // reach count 0 by overwriting record[14] AFTER building — and the client clears the slot only
            // when it is 0. (it.Count is untouched by the atomic write above, so the record is unchanged.)
            var rec = WorldEntryService.BuildItemRecord126(it);
            rec[ItemRecordCountOffset] = (byte)(newCount <= 0 ? 0 : Math.Min(newCount, MaxRecordCount));
            records.Add(rec);
            if (newCount > 0) it.Count = newCount;
            if (!soldTemplates.Contains(it.ItemId)) soldTemplates.Add(it.ItemId);

            // Put the sale on the buy-back list that 1336 ReqCancelSellItem draws from; without it every
            // buy-back is refused. It keeps a copy with Count = sellQty (the bought-back record
            // carries the sold quantity), and Paid is what this sale
            // credits, which is what the buy-back charges. Recorded only after the inventory write went
            // through, so a failed write cannot leave a buy-back entry for an item the player still has.
            BuyBackLedger.RecordSale(player, it, sellQty, ShopPricing.SellProceeds(unit, sellQty));

            Logger.LogInformation("{Name} sold uid {Uid} ('{Item}') x{Qty} for {Gold}g",
                player.Name, itemUid, tmpl.Name, sellQty, ShopPricing.SellProceeds(unit, sellQty));
        }
        return 0;
        });

        if (records.Count == 0)
        {
            // Nothing resolved: answer the "nothing sold" 2376, never an empty success frame and never
            // silence, e.g. to the second click of a double-clicked sell whose uid the first one already sold.
            Logger.LogInformation("{Name} sold nothing: none of the {Count} requested uid(s) is a sellable bag stack",
                player.Name, entries.Count);
            await RefuseNothingSoldAsync();
            return;
        }

        // blob+18 is the buy-back list itself, not a separate sold history: the client builds its
        // buy-back window from it (every 1336 asks for a uid in the latest 2376 list). Each list is
        // the previous one with the new uid appended or, once full, with the oldest dropped first, and no list
        // ever holds a uid twice. A bought-back uid leaves it: after a 2377 buy-back the next list is exactly the
        // previous list minus it plus the new sale. The ledger does all three (append, drop oldest, take on
        // buy-back), so the list is read from it after this request's sales were recorded.
        var soldList = BuyBackLedger.UidsOf(player);

        player.Credit(proceeds);
        // Write-through the new wallet total so the sale is durable on the next crash.
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);

        // Held-item Khara rows naming a sold template move to the new held count, their 2577 ahead of the 2376
        // (1335 -> 2577 -> 2376).
        // A held collect quest naming a sold template gets its 2428 with the new held count, also ahead of the 2376
        // (1335 -> 2428 -> 2376; QuestCollectDropTicks).
        foreach (var templateId in soldTemplates)
        {
            await KharaService.OnHeldItemChangedAsync(session, player, templateId, services, Logger, ct);
            await QuestCollectDropTicks.OnCollectItemLeftBagAsync(session, player, templateId, services, Logger, ct);
        }

        // OnSellItem 2376: for each sold stack the client finds the slot by the record's uid and clears
        // it when record+14 (stack count) is 0; it also sets the wallet from blob+10 (the NEW absolute
        // zeny total) — so no separate OnZenyChange is needed. Layout: msgTag@2, sellerHandle@4,
        // result@8=0, newZeny u64@10, sold-uid list (10 × u64, zero-padded)@18, count@98, then
        // N x 126B records@99 (ShopItemPackets.BuildSellItem2376). The consumer compares uids against
        // every one of the ten list slots.
        var sellBody = ShopItemPackets.BuildSellItem2376(player.EntityId, ShopCodes.SellResultSold, player.Gold, soldList, records);
        await session.SendRmiAsync(ZoneS2COpcodes.OnSellItem, writer => writer.WriteBytes(sellBody));
    }
}
