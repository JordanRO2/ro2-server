using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;

using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

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

    /// <summary>Offset of the u8 stack count inside a 126-byte item record.</summary>
    private const int ItemRecordCountOffset = 14;

    /// <summary>The u8 stack count saturates at this value.</summary>
    private const int MaxRecordCount = byte.MaxValue;

    /// <summary>The shop economy flow; stateless, every dependency is passed per call.</summary>
    private static readonly ShopApplicationService Shop = new();

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

        // What a template pays when sold: its own Price_Sell (it is NOT half the buy price: 21330011 buys at 250 but
        // sells for 1; 30000311 buys at 100000 but sells for 100). The template must allow a sale (ItemInfo Is_Sell != 0);
        // an unknown template has neither price nor flag, so it cannot be sold.
        ShopSellTerms? TermsOf(uint itemId)
            => store.GetItem(itemId) is { Is_Sell: not 0 } tmpl ? new ShopSellTerms(tmpl.Price_Sell, tmpl.Name) : null;

        // The load-decide-write loop (one hold of the seller's inventory gate), the credit and the buy-back list are the
        // economy flow's; it sends nothing. This handler resolves what a template pays, builds the records and sequences
        // the packets around it.
        var characters = services.GetRequiredService<ICharacterRepository>();
        var command = new ShopSellCommand(player.CharacterId, player.Name,
            entries.Select(e => new ShopSellEntry(e.uid, e.qty)).ToList(), TermsOf);
        var result = await Shop.SellAsync(command, player, BuyBackLedger.ListOf(player),
            new ShopInventoryAdapter(inventory, store.Items, Logger), new ShopLogAdapter(Logger),
            () => characters.UpdateGoldAsync(player.CharacterId, player.Gold));

        if (!result.Sold)
        {
            await RefuseNothingSoldAsync();
            return;
        }

        // The records of the sold stacks, each built from the item as it was (its pre-sale count).
        var records = new List<byte[]>(result.Lines.Count);
        foreach (var line in result.Lines)
            records.Add(SoldRecordOf(line, player.CharacterId));

        // Held-item Khara rows naming a sold template move to the new held count, their 2577 ahead of the 2376
        // (1335 -> 2577 -> 2376).
        // A held collect quest naming a sold template gets its 2428 with the new held count, also ahead of the 2376
        // (1335 -> 2428 -> 2376; QuestCollectDropTicks).
        foreach (var templateId in result.SoldTemplates)
        {
            await KharaService.OnHeldItemChangedAsync(session, player, templateId, services, Logger, ct);
            await QuestCollectDropTicks.OnCollectItemLeftBagAsync(session, player, templateId, services, Logger, ct);
        }

        // OnSellItem 2376: for each sold stack the client finds the slot by the record's uid and clears
        // it when record+14 (stack count) is 0; it also sets the wallet from blob+10 (the NEW absolute
        // zeny total) — so no separate OnZenyChange is needed. Layout: msgTag@2, sellerHandle@4,
        // result@8=0, newZeny u64@10, sold-uid list (10 × u64, zero-padded)@18, count@98, then
        // N x 126B records@99 (ShopItemPackets.BuildSellItem2376). The consumer compares uids against
        // every one of the ten list slots. blob+18 is the buy-back list itself, read after this request's
        // sales were recorded (see ShopApplicationService.SellAsync).
        var sellBody = ShopItemPackets.BuildSellItem2376(player.EntityId, ShopCodes.SellResultSold, player.Gold, result.BuyBackUids, records);
        await session.SendRmiAsync(ZoneS2COpcodes.OnSellItem, writer => writer.WriteBytes(sellBody));
    }

    /// <summary>
    /// The 126-byte record of a sold stack, built from the stack as it was (its pre-sale count), then with the stack-count
    /// byte forced: WriteItemInstance clamps record+0x0E to a minimum of 1, so a fully-sold stack can only reach count 0
    /// by overwriting record[14] AFTER building, and the client clears the slot only when it is 0.
    /// </summary>
    internal static byte[] SoldRecordOf(ShopSaleLine line, uint characterId)
    {
        var rec = WorldEntryService.BuildItemRecord126(InventoryEscrowAdapter.ToInventoryItem(line.Row, characterId));
        rec[ItemRecordCountOffset] = RecordCount(line.NewCount);
        return rec;
    }

    /// <summary>The u8 stack-count byte of a sold stack's record: 0 for a fully sold stack, else the count left, saturated at 255.</summary>
    internal static byte RecordCount(int newCount) => (byte)(newCount <= 0 ? 0 : Math.Min(newCount, MaxRecordCount));
}
