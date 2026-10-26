using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.GameData.Tables;
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
/// RMI 1334 ReqBuyItem - Buy item from NPC shop.
/// </summary>
public class BuyItemHandler : ZoneRmiHandlerBase<BuyItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBuyItem;
    public override string Name => "BuyItem";

    /// <summary>The shop economy flow; stateless, every dependency is passed per call.</summary>
    private static readonly ShopApplicationService Shop = new();

    public BuyItemHandler(IServiceScopeFactory s, ILogger<BuyItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    // C2S 1334 body, payload-absolute offsets (the body opens with a 6-byte compact-length envelope: marker, length,
    // u16 innerSize, u16 msgTag 0xE2C0). Bytes +6 (buyer PC handle) and +14 (context, 0) are not read.
    private const int ReqNpcHandleOffset = 10;                  // u32 shop NPC actor handle
    private const int ReqMerchantNoOffset = 18;                 // u16 Merchant table row ("No" column)
    private const int ReqItemIdOffset = 20;                     // u32 item template id
    private const int ReqCountOffset = 24;                      // u8 buy count
    private const int ReqTokenRowOffset = 25;                   // u16 BuyTokenInfo row (0 = plain zeny purchase)
    private const int ReqMinLength = ReqCountOffset + 1;        // shortest request carrying the count
    private const int ReqTokenRowLength = ReqTokenRowOffset + sizeof(ushort);   // request that also carries the token row

    /// <summary>Merchant.json Buy_Type values this handler distinguishes (the row decides the currency).</summary>
    private const int BuyTypeZeny = 0;
    private const int BuyTypeToken = 1;
    private const int BuyTypeMastery = 4;

    /// <summary>Buy type assumed for a request that names a token row but no Merchant row.</summary>
    private const int BuyTypeNoMerchantRow = -1;

    /// <summary>NPCInfo Function_Type whose Function_ID is a shop (Merchant) id.</summary>
    private const int ShopFunctionType = 11;

    /// <summary>InventoryItem.StorageType of the bag.</summary>
    private const byte StorageBag = 0;

    /// <summary>Offset of the count byte inside a 126-byte item record.</summary>
    private const int ItemRecordCountOffset = 14;

    /// <summary>Source-item uid passed to the stone/token take plan: none (a shop buy has no source item to leave out).</summary>
    private const ulong NoExcludedUid = 0;

    /// <summary>The Merchant.json columns this handler needs. GameDataStore keeps only NPC → item lists
    /// (no row number, Buy_Type or BuyTokenID), so the rows are read here once, from the same "data"
    /// directory Program.cs hands GameDataStore.</summary>
    internal sealed record MerchantRow(int No, uint ID, int Buy_Type, uint Item, int BuyTokenID);

    private static readonly Lazy<IReadOnlyDictionary<int, MerchantRow>?> MerchantRows = new(LoadMerchantRows);

    private static IReadOnlyDictionary<int, MerchantRow>? LoadMerchantRows()
    {
        try
        {
            var path = Path.Combine("data", "Merchant.json");
            if (!File.Exists(path)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<MerchantRow>>(File.ReadAllBytes(path));
            if (rows is null) return null;
            var byNo = new Dictionary<int, MerchantRow>(rows.Count);
            foreach (var r in rows) byNo.TryAdd(r.No, r); // "No" is unique
            return byNo;
        }
        catch
        {
            return null; // unreadable table: fall back to the request's own BuyTokenInfo field
        }
    }

    private static MerchantRow? LookupMerchantRow(ushort no)
        => MerchantRows.Value is { } rows && rows.TryGetValue(no, out var row) ? row : null;

    /// <summary>
    /// Possession limits that override the Possession_Max this server ships. These take precedence over
    /// ItemInfo.
    /// </summary>
    /// <remarks>
    /// 22261077 (siege supplies, the only Merchant-sold item whose ItemInfo row sets Possession_Max): ItemInfo
    /// says 10 and no ItemInfo column holds 15, but the effective limit is 15. The rule is: a buy that leaves the
    /// bag total at up to 15 is accepted (including a single buy of 15) and any buy that would pass 15 is refused.
    /// So the server's table has 15 where the shipped data has 10.
    /// </remarks>
    private static readonly IReadOnlyDictionary<uint, int> MeasuredPossessionLimits = new Dictionary<uint, int>
    {
        [SiegeSuppliesItemId] = SiegeSuppliesPossessionLimit,
    };

    /// <summary>Item template 22261077, the siege supplies sold by Merchants.</summary>
    private const uint SiegeSuppliesItemId = 22261077;

    /// <summary>Possession limit enforced for <see cref="SiegeSuppliesItemId"/> (ItemInfo says 10).</summary>
    private const int SiegeSuppliesPossessionLimit = 15;

    /// <summary>ItemInfo Possession_Max for the rows that set one. ItemData does not carry the
    /// column, so it is read here once from the same "data" directory Merchant.json comes from.</summary>
    private sealed record PossessionRow(uint ID, int Possession_Max);

    private static readonly Lazy<IReadOnlyDictionary<uint, int>> PossessionLimits = new(LoadPossessionLimits);

    private static IReadOnlyDictionary<uint, int> LoadPossessionLimits()
    {
        try
        {
            var path = Path.Combine("data", "ItemInfo.json");
            if (!File.Exists(path)) return new Dictionary<uint, int>();
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<PossessionRow>>(File.ReadAllBytes(path));
            var limits = new Dictionary<uint, int>();
            foreach (var r in rows ?? new List<PossessionRow>())
                if (r.Possession_Max > 0) limits.TryAdd(r.ID, r.Possession_Max);
            return limits;
        }
        catch
        {
            return new Dictionary<uint, int>(); // unreadable table: no limit is enforced
        }
    }

    /// <summary>The possession limit of <paramref name="itemId"/>. It is the override where one
    /// exists (<see cref="MeasuredPossessionLimits"/>), else ItemInfo's Possession_Max, and 0 when neither sets
    /// one.</summary>
    internal static int PossessionLimitOf(uint itemId)
        => MeasuredPossessionLimits.TryGetValue(itemId, out var measured) ? measured
         : PossessionLimits.Value.TryGetValue(itemId, out var max) ? max : 0;

    /// <summary>
    /// True when buying <paramref name="quantity"/> more of <paramref name="itemId"/> would put the bag
    /// total (StorageType 0) above the item's possession limit. That total is where a buy lands.
    /// An item with no limit is never refused.
    /// </summary>
    internal static bool ExceedsPossessionLimit(IReadOnlyList<InventoryItem> owned, uint itemId, long quantity,
        out long held, out int limit)
    {
        limit = PossessionLimitOf(itemId);
        held = 0;
        if (limit <= 0) return false;
        return ShopBuyPlanner.ExceedsPossessionLimit(ToBagStacks(owned), itemId, quantity, limit, out held);
    }

    /// <summary>The bag stacks (StorageType 0) of <paramref name="owned"/> as neutral <see cref="BagStack"/>s for the
    /// economy planner; worn and banked rows are dropped.</summary>
    private static IReadOnlyList<BagStack> ToBagStacks(IReadOnlyList<InventoryItem> owned)
        => owned.Where(i => i.StorageType == StorageBag).Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList();

    /// <summary>
    /// Why a buy of <paramref name="itemId"/> from <paramref name="npc"/> must be refused, or null when the NPC is a
    /// live shop of the player's map that stocks the item (and, when Merchant.json is loaded, the Merchant row the
    /// request names is one of that NPC's own). An unresolved handle is a refusal, never a pass: with no NPC there is
    /// no catalog, and the buy would fall through to a free (Price_Buy 0) or off-catalog (drop / quest-only item)
    /// purchase from anywhere. The shop list itself is client-side, so this is the server's only catalog check.
    /// </summary>
    internal static string? ShopRefusal(NpcRuntimeState? npc, uint playerMapId, uint itemId, GameDataStore store,
        MerchantRow? row, bool merchantTableLoaded)
    {
        // Resolve the facts; the pure catalog verdict lives in ShopPurchaseRules.CheckPurchase. The NPC's FULL
        // shop is resolved through its Function slots, not just its own Merchant row: a field NPC opens a
        // Kafra/general-goods shop (Function_Type 11), and a merchant may also reference a second shop (e.g.
        // 10780000 sells mounts AND opens the 10780100 taxi shop). The Merchant row decides the currency, so a
        // request that names no row (the client always sends one) or a row of another shop would price the item
        // as plain zeny (Price_Buy 0 on the token rows); when the table is unreadable the BuyTokenInfo-field
        // fallback stays (see LoadMerchantRows).
        bool npcLiveOnMap = npc is not null && !npc.IsDead && npc.MapId == playerMapId;
        var stock = npcLiveOnMap ? store.GetNpcShopItems(npc!.NpcId) : Array.Empty<uint>();
        bool stocksItem = Array.IndexOf(stock, itemId) >= 0;
        bool rowBelongs = npcLiveOnMap && row is not null && ShopMerchantIds(store, npc!.NpcId).Contains(row.ID);

        return ShopPurchaseRules.CheckPurchase(npcLiveOnMap, stock.Length, stocksItem,
            merchantTableLoaded, row is not null, rowBelongs) switch
        {
            ShopPurchaseRefusal.None => null,
            ShopPurchaseRefusal.NpcUnavailable => "its NPC handle names no live NPC on the player's map",
            ShopPurchaseRefusal.NoShop => "that NPC has no shop",
            ShopPurchaseRefusal.NotStocked => "that NPC's shop does not stock the item",
            ShopPurchaseRefusal.NoMerchantRow => "it names no Merchant row",
            ShopPurchaseRefusal.WrongMerchantRow => "its Merchant row belongs to another shop",
            _ => "refused",
        };
    }

    /// <summary>The Merchant ids an NPC's shop is made of: its own id and every Function_Type 11 target (the same
    /// resolution as GameDataStore.GetNpcShopItems).</summary>
    private static HashSet<uint> ShopMerchantIds(GameDataStore store, uint npcId)
    {
        var ids = new HashSet<uint> { npcId };
        if (store.Npcs.TryGetValue(npcId, out var info))
        {
            if (info.Function_Type_1 == ShopFunctionType) ids.Add(info.Function_ID_1);
            if (info.Function_Type_2 == ShopFunctionType) ids.Add(info.Function_ID_2);
            if (info.Function_Type_3 == ShopFunctionType) ids.Add(info.Function_ID_3);
        }
        return ids;
    }

    /// <summary>BuyTokenInfo rows by ID, per loaded store (a store is loaded once, never reloaded).</summary>
    private static readonly ConditionalWeakTable<GameDataStore, Dictionary<ushort, BuyTokenInfo>> TokenRowsByStore = new();

    /// <summary>The BuyTokenInfo row <paramref name="id"/>, or null.</summary>
    internal static BuyTokenInfo? TokenRowOf(GameDataStore store, int id)
    {
        if (id is <= 0 or > ushort.MaxValue) return null;
        var rows = TokenRowsByStore.GetValue(store, static s =>
        {
            var byId = new Dictionary<ushort, BuyTokenInfo>(s.BuyTokenInfo.Count);
            foreach (var r in s.BuyTokenInfo) byId.TryAdd(r.ID, r);
            return byId;
        });
        return rows.TryGetValue((ushort)id, out var row) ? row : null;
    }

    /// <summary>True when the row costs only token items: no PvP_Point, Mastery_Point or Dia, which this server
    /// keeps no balance of. Most of data/Merchant.json's Buy_Type 1 rows resolve to such a row; the others
    /// have no BuyTokenInfo row at all.</summary>
    internal static bool IsTokenOnly(BuyTokenInfo row)
        => row.PvP_Point == 0 && row.Mastery_Point == 0 && row.Dia == 0;

    /// <summary>The per-unit token cost of a BuyTokenInfo row for the economy flow: up to three (template, need) pairs.</summary>
    internal static TokenCost TokenCostOf(BuyTokenInfo row)
        => new(new (uint, int)[]
        {
            (row.TokenItemID1, (int)row.NeedValue1),
            (row.TokenItemID2, (int)row.NeedValue2),
            (row.TokenItemID3, (int)row.NeedValue3),
        });

    /// <summary>
    /// The 2379 record of a token stack a buy touched: the stack as it now stands, with count 0 for an emptied one (the
    /// record writer clamps the count to at least 1, so the client only empties the cell when the byte is forced to 0).
    /// </summary>
    internal static byte[] TokenRecordOf(ShopTokenTouch touch, uint characterId)
    {
        var record = ItemPackets.BuildItemRecord126(InventoryEscrowAdapter.ToInventoryItem(touch.Stack, characterId));
        if (touch.Emptied) record[ItemRecordCountOffset] = 0;
        return record;
    }


    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // COMPACT-BA envelope (6 bytes: marker + len + u16 innerSize + u16 msgTag=0xE2C0), then a
        // 25-byte blob. Field offsets are payload-absolute; the client packs a struct {u16 slot, u32
        // itemTemplateId, u8 count, u16 _}:
        //   +6  u32 buyer PC actor handle
        //   +10 u32 NPC (shop) actor handle
        //   +14 u32 context (0)
        //   +18 u16 Merchant table row (its "No" column) - NOT a UI grid slot
        //   +20 u32 item TEMPLATE id   <- the real item the player clicked
        //   +24 u8  buy count
        //   +25 u16 BuyTokenInfo row (that Merchant row's BuyTokenID; 0 = a plain zeny purchase)
        // The +18 field resolves to a Merchant row whose Item is +20 and whose BuyTokenID is +25, and the 2375
        // answer echoes that row's Buy_Type at blob+28 and the token row at blob+30.
        if (payload.Length < ReqMinLength) return;
        var span = payload.Span;
        var npcHandle = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(ReqNpcHandleOffset));
        var merchantNo = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(ReqMerchantNoOffset));
        var itemId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(ReqItemIdOffset));
        var quantity = (uint)Math.Max(1, (int)span[ReqCountOffset]);
        var tokenRow = payload.Length >= ReqTokenRowLength
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(ReqTokenRowOffset))
            : (ushort)0;

        var store = services.GetRequiredService<GameDataStore>();

        // Every refusal is answered with the failed-buy 2375 (ShopItemPackets.BuildBuyRefusal2375). A
        // silent return leaves the shop's quantity dialog waiting for an answer that never comes. A refusal of the
        // economy flow has already handed the reserved zeny back, so the wallet is final when the frame is built.
        async Task RefuseAsync(ushort code)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.OnBuyItem, writer => writer.WriteBytes(
                ShopItemPackets.BuildBuyRefusal2375(player.EntityId, code, tokenRow, itemId)));
        }

        // SERVER-SIDE SHOP VALIDATION. The NPC shop list is shown entirely client-side (from the
        // client's own Merchant table); the server never sends it, but it must not trust the buy.
        // The 1334 carries the shop NPC's actor handle at payload+10 - resolve it to its template
        // (NpcRuntimeState.NpcId == the spawn AssetIndex, which is the Merchant table key) and
        // confirm this NPC actually stocks this item. This rejects buying from a non-merchant NPC
        // (e.g. a quest or guard NPC whose "shop" the client opened with stale UI, sending
        // phantom item ids) and buying an item the NPC does not sell. Merchant items are always real,
        // so a legitimate client buy always passes. Both refusals take the generic code 1.
        // The 1334 must name a live NPC of the player's map whose shop stocks the item; an unresolved handle
        // (forged, stale, cross-map) is REFUSED, not waved through to a merchantNo 0 free-buy.
        var npcState = services.GetRequiredService<NpcStateManager>();
        var npc = npcState.GetNpc(npcHandle);
        var merchantRow = LookupMerchantRow(merchantNo);
        var refusal = ShopRefusal(npc, player.MapId, itemId, store, merchantRow, MerchantRows.Value is not null);
        if (refusal is not null)
        {
            Logger.LogInformation("{Name} tried to buy item {Item} (NPC handle 0x{Npc:X8}, template {Template}, "
                + "Merchant row {No}); refused: {Why}", player.Name, itemId, npcHandle, npc?.NpcId ?? 0, merchantNo, refusal);
            await RefuseAsync(ShopCodes.BuyFailGeneric);
            return;
        }

        // Proximity (E-H1): the buyer must be alive and near the shop NPC. Same map is the HARD rule (ShopRefusal
        // above, and the gate's CrossMap); the distance is a loose guard on top of it. The NPC interaction radius
        // (ActorGate.NpcInteractReach) is in no data table, so it is generous.
        var reach = ActorGate.CanTransact(player, npc, ActorGate.NpcInteractReach);
        if (reach != ActorRefusal.None)
        {
            Logger.LogInformation("{Name} tried to buy item {Item} from NPC 0x{Npc:X8}; refused: {Why}",
                player.Name, itemId, npcHandle, reach);
            await RefuseAsync(ShopCodes.BuyFailGeneric);
            return;
        }

        // ZENY AND TOKEN ITEMS ONLY. The Merchant row decides what the item costs, not Price_Buy: Buy_Type 0 is zeny;
        // 1 is BuyTokenInfo token items, 2 PvP points, 3 PvP points + token items, 4 Mastery points,
        // 5 Dia, 7 Dia + token items (in data/Merchant.json the Buy_Type 2..7 rows, and nearly all Buy_Type 1 rows
        // with a BuyTokenID, resolve to exactly that cost mix in BuyTokenInfo; the Buy_Type 1 rows without one -
        // raid boxes and costumes - are not zeny rows either). Retail never charges zeny for those.
        // This server keeps no Dia, Mastery or PvP balance, so Buy_Type 2/3/4/5/7 purchases are REFUSED - charging
        // Price_Buy zeny instead would sell e.g. a Dia item or a Mastery-point pass for a few zeny. The refusal is
        // the failure reply (see BuildBuyRefusal2375): what a player with none of that currency would get. These
        // stay blocked on a currency-balance system. Buy_Type 1 (token items only) is served below.
        if (merchantRow is not null && merchantRow.Item != itemId)
        {
            // A row/item mismatch is refused with the generic code 1.
            Logger.LogInformation("{Name} sent Merchant row {No} (item {RowItem}) with item {Item}; refused",
                player.Name, merchantNo, merchantRow.Item, itemId);
            await RefuseAsync(ShopCodes.BuyFailGeneric);
            return;
        }
        var buyType = merchantRow?.Buy_Type ?? (tokenRow != 0 ? BuyTypeNoMerchantRow : BuyTypeZeny);

        // TOKEN ITEMS (Buy_Type 1). Retail takes NeedValue_i x count of each TokenItemID_i of the row's
        // BuyTokenInfo row and charges no zeny: one 2379 per token stack touched (reason 2 at blob+10, an
        // emptied stack with count 0) before the 2375, which carries mode 1 at blob+28, the token row at
        // blob+30 and an unchanged wallet at blob+12. A row with no BuyTokenInfo row, or one that also costs PvP,
        // Mastery or Dia points, is refused with code 1 below, as is a bag holding too few tokens.
        BuyTokenInfo? tokenCost = null;
        if (buyType == BuyTypeToken && merchantRow is not null)
        {
            tokenCost = TokenRowOf(store, merchantRow.BuyTokenID);
            if (tokenCost is null || !IsTokenOnly(tokenCost))
            {
                Logger.LogInformation("{Name} tried a token purchase of item {Item} (Merchant row {No}, BuyTokenInfo {Token}): "
                    + "{Why}; refused", player.Name, itemId, merchantNo, merchantRow.BuyTokenID,
                    tokenCost is null ? "no such BuyTokenInfo row" : "it also costs PvP, Mastery or Dia points, which this server keeps no balance of");
                await RefuseAsync(ShopCodes.BuyFailGeneric);
                return;
            }
        }
        else if (buyType != BuyTypeZeny)
        {
            Logger.LogInformation("{Name} tried a non-zeny purchase of item {Item} (Merchant row {No}, "
                + "Buy_Type {BuyType}, BuyTokenInfo {Token}); no Dia/Mastery/PvP balance or token exchange "
                + "exists server-side, refused", player.Name, itemId, merchantNo, buyType, tokenRow);
            await RefuseAsync(buyType == BuyTypeMastery ? ShopCodes.BuyFailMastery : ShopCodes.BuyFailGeneric);
            return;
        }

        var item = store.GetItem(itemId);
        if (item == null)
        {
            Logger.LogWarning("Session {Id}: Buy unknown item {ItemId}", session.SessionId, itemId);
            await RefuseAsync(ShopCodes.BuyFailGeneric);
            return;
        }

        // The wallet, bag and placement rules are the economy flow's. The cost is TAKEN from the wallet first (a token
        // buy costs none), every later refusal hands it back, and the order stays debit -> read bag -> possession ->
        // tokens -> plan -> write -> ACK. The flow sends nothing: this handler sequences the packets around it.
        var characters = services.GetRequiredService<ICharacterRepository>();
        var command = new ShopBuyCommand(player.CharacterId, player.Name, npcHandle, itemId, item.Name, quantity,
            item.Price_Buy, item.Stack_Max, item.Durability, PossessionLimitOf(itemId),
            tokenCost is null ? null : TokenCostOf(tokenCost), tokenCost?.ID ?? 0);
        var result = await Shop.BuyAsync(command, player,
            new ShopInventoryAdapter(services.GetRequiredService<IInventoryRepository>(), store.Items, Logger),
            new ShopLogAdapter(Logger),
            () => characters.UpdateGoldAsync(player.CharacterId, player.Gold),
            async written =>
            {
                // The token 2379s go out only now that every write succeeded (a failed buy sends none, so the client's
                // cells never diverge from the bag): one per stack touched, ahead of the 2375 as in retail, each the
                // stack as it now stands with reason 2 at blob+10, count 0 for an emptied one (as BreakDownItemHandler
                // does). The wallet is written through after them.
                foreach (var touch in written.TokenTouches)
                {
                    var destroyBody = ShopItemPackets.BuildDestroyItem2379(player.EntityId,
                        TokenRecordOf(touch, player.CharacterId), ShopItemPackets.DestroyReasonTokenBuy);
                    await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem, writer => writer.WriteBytes(destroyBody));
                }
            });
        if (result.Written is not { } written)
        {
            await RefuseAsync(result.RefusalCode);
            return;
        }

        // Deduct the cost and hand the stacks over INSIDE the OnBuyItem (2375) ACK. That single packet does
        // everything: its record count (body+29) is the number of 126-byte records that follow (from body+41),
        // each of which is added to (or, for a restack, updates) the bag, and its wallet field (body+12) sets the
        // new balance. The count MUST be >= 1 or the client ignores the packet (the quantity dialog stays open).
        //
        // 2375 already sets the wallet, so no separate OnZenyChange 2435 is needed. Retail sends the 2375
        // ALONE: no 2333 OnPickupItem accompanies a successful buy. The client prints the "obtained" chat
        // itself for a uid it already holds (a restack); a fresh stack shows only the zeny spent.
        var records = new List<byte[]>(written.Stacks.Count);
        foreach (var st in written.Stacks)
            records.Add(ItemPackets.BuildItemRecord126(InventoryEscrowAdapter.ToInventoryItem(st, player.CharacterId)));
        var buyBody = tokenCost is null
            ? ShopItemPackets.BuildBuyItem2375(player.EntityId, player.Gold, itemId, records)
            : ShopItemPackets.BuildBuyItem2375(player.EntityId, player.Gold, itemId, records,
                ShopItemPackets.BuyModeToken, tokenCost.ID);
        await session.SendRmiAsync(ZoneS2COpcodes.OnBuyItem,
            writer => writer.WriteBytes(buyBody));
    }
}
