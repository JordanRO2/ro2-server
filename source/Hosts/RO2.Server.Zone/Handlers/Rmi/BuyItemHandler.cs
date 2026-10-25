using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using System.Runtime.CompilerServices;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

using RO2.Economy.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1334 ReqBuyItem - Buy item from NPC shop.
/// </summary>
public class BuyItemHandler : ZoneRmiHandlerBase<BuyItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBuyItem;
    public override string Name => "BuyItem";

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
    /// Where a zeny buy of <paramref name="quantity"/> lands, the way retail stacks a buy (the rule in InventoryStacking):
    /// onto the lowest-cell stack of the item that it fits into whole
    /// (existing + quantity &lt;= Stack_Max), else into new stacks of Stack_Max, each in the next free cell of
    /// <see cref="BagCapacity.FreeCells"/>. Null when the bag has fewer free cells than the new stacks need.
    /// </summary>
    /// <remarks>
    /// The cells come from BagCapacity rather than from AddItemAsync's Slot -1, which takes the lowest unused
    /// index with no upper bound: a full main bag and a 30-cell bag in character-bag cell 0 would put the next
    /// stack in flat 50, a cell no bag provides.
    /// </remarks>
    internal static BuyPlan? PlanBuy(IReadOnlyList<InventoryItem> owned, IReadOnlyDictionary<uint, ItemData> items,
        uint itemId, int quantity, byte stackMax)
    {
        // The placement rule is the economy planner's; the host still resolves the free cells (BagCapacity) and maps
        // the restack target back to the live inventory row the writer mutates.
        var plan = ShopBuyPlanner.Plan(ToBagStacks(owned), BagCapacity.FreeCells(owned, items), itemId, quantity, stackMax);
        if (plan is null) return null;
        var restack = plan.RestackId is { } id ? owned.First(i => i.Id == id) : null;
        return new BuyPlan(restack, plan.NewStacks);
    }

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
        if (npc is null || npc.IsDead || npc.MapId != playerMapId)
            return "its NPC handle names no live NPC on the player's map";

        // Resolve the NPC's FULL shop through its Function slots, not just its own Merchant row: a field NPC
        // opens a Kafra/general-goods shop (Function_Type 11), and a merchant may also reference a second shop
        // (e.g. 10780000 sells mounts AND opens the 10780100 taxi shop).
        var stock = store.GetNpcShopItems(npc.NpcId);
        if (stock.Length == 0) return "that NPC has no shop";
        if (Array.IndexOf(stock, itemId) < 0) return "that NPC's shop does not stock the item";

        // The Merchant row decides the currency, so a request that names no row (the client always sends one)
        // or a row of another shop would price the item as plain zeny: Price_Buy 0 on the token
        // rows. When the table is unreadable the BuyTokenInfo-field fallback stays (see LoadMerchantRows).
        if (merchantTableLoaded)
        {
            if (row is null) return "it names no Merchant row";
            if (!ShopMerchantIds(store, npc.NpcId).Contains(row.ID)) return "its Merchant row belongs to another shop";
        }
        return null;
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

    /// <summary>
    /// The token stacks a Buy_Type 1 buy of <paramref name="quantity"/> takes and the count each is left with:
    /// NeedValue_i x quantity of each TokenItemID_i (a template named twice adds up), from the bag stacks of that
    /// template, lowest cell first (<see cref="BreakDownItemHandler.PlanStoneTake"/>, the order used for
    /// break-down stones). Null when the bag holds fewer
    /// than any one need.
    /// </summary>
    /// <remarks>
    /// Several stacks are taken when one is not enough: a row that needs 3,400 of a token template is paid from as
    /// many stacks as it takes, each emptied one reported with count 0 and the last left with the remainder.
    /// </remarks>
    internal static List<(InventoryItem Stack, int Left)>? PlanTokenTake(IReadOnlyList<InventoryItem> owned,
        BuyTokenInfo row, int quantity)
    {
        var cost = new TokenCost(new (uint, int)[]
        {
            (row.TokenItemID1, (int)row.NeedValue1),
            (row.TokenItemID2, (int)row.NeedValue2),
            (row.TokenItemID3, (int)row.NeedValue3),
        });
        var plan = ShopBuyPlanner.PlanTokenTake(ToBagStacks(owned), cost, quantity);
        if (plan is null) return null;
        return plan.Select(t => (owned.First(i => i.Id == t.StackId), t.Left)).ToList();
    }

    /// <summary>A planned buy: <see cref="Restack"/> takes the whole quantity, or else <see cref="NewStacks"/>
    /// lists the (cell, count) of each new stack.</summary>
    internal sealed record BuyPlan(InventoryItem? Restack, IReadOnlyList<(short Cell, int Count)> NewStacks);

    /// <summary>What <see cref="WriteBuyAsync"/> wrote: the bought stacks and, for a token buy, the 2379 record of each
    /// token stack touched (count 0 for an emptied one).</summary>
    internal sealed record BuyWrite(List<InventoryItem> Stacks, List<byte[]> TokenRecords);

    /// <summary>
    /// Writes a planned buy: delivers the bought stacks (<see cref="PlanBuy"/>), then takes the tokens
    /// (<see cref="PlanTokenTake"/>). FAIL-CLOSED: when any write throws, every write already made is undone, newest
    /// first (an emptied token stack is put back, a shrunk one restored, a restacked one lowered, a new stack removed),
    /// and the original exception is rethrown, so nothing is left delivered without its tokens taken. Each undo is
    /// best effort and idempotent (it is registered BEFORE its write, because a write that throws may or may not have
    /// landed); an undo that itself fails is logged and the rest still run.
    /// </summary>
    internal static async Task<BuyWrite> WriteBuyAsync(IInventoryRepository inventory, uint characterId, uint itemId,
        int quantity, BuyPlan plan, uint templateDurability, IReadOnlyList<(InventoryItem Stack, int Left)>? tokenTakes,
        ILogger logger)
    {
        var stacks = new List<InventoryItem>();
        var tokenRecords = new List<byte[]>();
        var undo = new List<Func<Task>>();
        try
        {
            if (plan.Restack is { } target)
            {
                var before = target.Count;
                undo.Add(async () =>
                {
                    target.Count = before;
                    await inventory.UpdateItemAsync(target);
                });
                target.Count += quantity;
                await inventory.UpdateItemAsync(target);
                stacks.Add(target);
            }
            else
            {
                short durability = (short)Math.Min(templateDurability, (uint)short.MaxValue);
                foreach (var (cell, count) in plan.NewStacks)
                {
                    var stack = new InventoryItem
                    {
                        CharacterId = characterId,
                        ItemId = itemId,
                        Count = count,
                        Slot = cell,
                        StorageType = StorageBag,
                        Durability = durability,
                        // record+69 = 0, not the template's Abili_val: a bought stack's 2375 record carries 0 there
                        // (see WorldEntryService.RecordAbilityValue).
                        AbilityValue = 0,
                    };
                    undo.Add(async () =>
                    {
                        if (stack.Id != 0) await inventory.RemoveItemAsync(stack.Id);   // 0: never stamped, never inserted
                    });
                    await inventory.AddItemAsync(stack);   // stamps the instance uid; a cell >= 0 is kept as given
                    stacks.Add(stack);
                }
            }

            // The tokens, after the bought stacks are stored (nothing is taken for a buy that could not be delivered).
            foreach (var (stack, left) in tokenTakes ?? [])
            {
                var original = stack.Count;
                if (left <= 0)
                {
                    undo.Add(async () =>
                    {
                        if (await inventory.GetByIdAsync(stack.Id) is null) await inventory.AddItemAsync(stack);
                    });
                    var record = WorldEntryService.BuildItemRecord126(stack);
                    record[ItemRecordCountOffset] = 0;   // count 0 -> the client empties the cell (the record writer clamps to >= 1)
                    await inventory.RemoveItemAsync(stack.Id);
                    tokenRecords.Add(record);
                }
                else
                {
                    undo.Add(async () =>
                    {
                        stack.Count = original;
                        await inventory.UpdateItemAsync(stack);
                    });
                    stack.Count = left;
                    await inventory.UpdateItemAsync(stack);
                    tokenRecords.Add(WorldEntryService.BuildItemRecord126(stack));
                }
            }
            return new BuyWrite(stacks, tokenRecords);
        }
        catch
        {
            for (var i = undo.Count - 1; i >= 0; i--)
            {
                try { await undo[i](); }
                catch (Exception undoEx)
                {
                    logger.LogError(undoEx, "Buy of item {Item} for character {Char}: could not undo write {Step} after a failed buy; "
                        + "the bag may hold an item without its tokens taken", itemId, characterId, i);
                }
            }
            throw;
        }
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
        // silent return leaves the shop's quantity dialog waiting for an answer that never comes.
        // Zeny held back from the wallet once the buy is affordable; a refusal after that hands it back.
        long reservedZeny = 0;
        async Task RefuseAsync(ushort code)
        {
            if (reservedZeny > 0) { player.Credit(reservedZeny); reservedZeny = 0; }
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

        // A token buy costs no zeny (the 2375 wallet is unchanged). A zeny buy costs Price_Buy x count. The
        // timed shop-discount state is not modelled, so no discount is applied.
        var totalCost = tokenCost is null ? ShopPricing.BuyCost(item.Price_Buy, quantity) : 0L;

        // Can't buy what you can't afford. The cost is TAKEN from the wallet here, atomically (TryDebit), not
        // just compared: a check followed by a later deduct lets a concurrent spender drain the same funds in
        // between. Every refusal below hands the reserved zeny back. Zeny-row failures carry code 1.
        if (!player.TryDebit(totalCost))
        {
            Logger.LogInformation("{Name} cannot afford {Count}x '{Item}' ({Cost}g, has {Have}g)",
                player.Name, quantity, item.Name, totalCost, player.Gold);
            await RefuseAsync(ShopCodes.BuyFailGeneric);
            return;
        }
        reservedZeny = totalCost;

        var inventory = services.GetRequiredService<IInventoryRepository>();
        IReadOnlyList<InventoryItem> owned;
        try
        {
            owned = await inventory.GetByCharacterAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name}: bag unreadable for a buy of {Count}x '{Item}'; refused, nothing charged",
                player.Name, quantity, item.Name);
            await RefuseAsync(ShopCodes.BuyFailGeneric);
            return;
        }

        // Possession limit: the bag total after the buy may not pass it. The limit is the override where one
        // exists (15 for the siege supplies 22261077, where ItemInfo says 10), otherwise ItemInfo's
        // Possession_Max. See MeasuredPossessionLimits and ShopCodes.BuyFailPossession.
        if (ExceedsPossessionLimit(owned, itemId, quantity, out var held, out var limit))
        {
            Logger.LogInformation("{Name} cannot hold {Count} more '{Item}' ({Held} held, possession limit {Max})",
                player.Name, quantity, item.Name, held, limit);
            await RefuseAsync(ShopCodes.BuyFailPossession);
            return;
        }

        List<(InventoryItem Stack, int Left)>? tokenTakes = null;
        if (tokenCost is not null)
        {
            tokenTakes = PlanTokenTake(owned, tokenCost, (int)quantity);
            if (tokenTakes is null)
            {
                Logger.LogInformation("{Name} cannot pay {Count}x '{Item}' with BuyTokenInfo {Token}: the bag holds too few tokens; refused",
                    player.Name, quantity, item.Name, tokenCost.ID);
                await RefuseAsync(ShopCodes.BuyFailGeneric);
                return;
            }
        }

        // The cells are planned on the bag as it stands before the tokens are taken: a new stack is never put
        // into a cell a token take empties.
        var plan = PlanBuy(owned, store.Items, itemId, (int)quantity, item.Stack_Max);
        if (plan is null)
        {
            Logger.LogInformation("{Name}: no free bag cell for {Count}x '{Item}'; refused", player.Name, quantity, item.Name);
            await RefuseAsync(ShopCodes.BuyFailBagFull);
            return;
        }

        Logger.LogInformation("{Name} buys {Count}x '{Item}' for {Gold}g from NPC 0x{Npc:X8}",
            player.Name, quantity, item.Name, totalCost, npcHandle);

        // Actually GIVE the item, stacked the way retail stacks a buy (PlanBuy): a quantity that fits an
        // existing stack of the item is added to it and the record carries that EXISTING uid with the new
        // total; a quantity that does not fit is split into new stacks of Stack_Max (a 255-buy of a Stack_Max-99
        // item is 99 + 99 + 57). A new stack carries the template's Durability, as retail's
        // fresh stackable records do (see InventoryStacking).
        // Persist BEFORE charging: if the bag cannot be written nothing was delivered, so answer retail's
        // failed-buy form instead of charging for (and announcing) a record that would vanish on relog.
        // The bought stacks AND the token takes are written as one unit (WriteBuyAsync): if any write fails the ones
        // already made are undone, so a failure never leaves the item delivered with its tokens untaken (E-H13).
        BuyWrite written;
        try
        {
            written = await WriteBuyAsync(inventory, player.CharacterId, itemId, (int)quantity, plan, item.Durability,
                tokenTakes, Logger);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name}: buy of {Count}x '{Item}' could not be saved; refused, nothing delivered or charged",
                player.Name, quantity, item.Name);
            await RefuseAsync(ShopCodes.BuyFailGeneric);
            return;
        }
        var stacks = written.Stacks;

        // Deduct the cost and hand the stacks over INSIDE the OnBuyItem (2375) ACK. That single packet does
        // everything: its record count (body+29) is the number of 126-byte records that follow (from body+41),
        // each of which is added to (or, for a restack, updates) the bag, and its wallet field (body+12) sets the
        // new balance. The count MUST be >= 1 or the client ignores the packet (the quantity dialog stays open).
        //
        // 2375 already sets the wallet, so no separate OnZenyChange 2435 is needed. Retail sends the 2375
        // ALONE: no 2333 OnPickupItem accompanies a successful buy. The client prints the "obtained" chat
        // itself for a uid it already holds (a restack); a fresh stack shows only the zeny spent.
        // The token 2379s go out only now that every write succeeded (a failed buy sends none, so the client's cells
        // never diverge from the bag): one per stack touched, ahead of the 2375 as in retail, each the stack as it now
        // stands with reason 2 at blob+10, count 0 for an emptied one (as BreakDownItemHandler does).
        foreach (var record in written.TokenRecords)
        {
            var destroyBody = ShopItemPackets.BuildDestroyItem2379(player.EntityId, record, ShopItemPackets.DestroyReasonTokenBuy);
            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem, writer => writer.WriteBytes(destroyBody));
        }

        if (tokenCost is null)
        {
            reservedZeny = 0;   // the charge already happened at the affordability check
            // Write-through: persist the wallet immediately so a crash can't lose a purchase (the stacks are
            // already durable above). Position/vitals ride the periodic autosave instead.
            await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);
        }
        var records = new List<byte[]>(stacks.Count);
        foreach (var st in stacks) records.Add(WorldEntryService.BuildItemRecord126(st));
        var buyBody = tokenCost is null
            ? ShopItemPackets.BuildBuyItem2375(player.EntityId, player.Gold, itemId, records)
            : ShopItemPackets.BuildBuyItem2375(player.EntityId, player.Gold, itemId, records,
                ShopItemPackets.BuyModeToken, tokenCost.ID);
        await session.SendRmiAsync(ZoneS2COpcodes.OnBuyItem,
            writer => writer.WriteBytes(buyBody));
    }
}
