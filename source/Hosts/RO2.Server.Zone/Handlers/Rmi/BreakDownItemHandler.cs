using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Economy.Domain;
using RO2.Inventory.Domain;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1328 ReqBreakDownItem - break an item down into materials.</summary>
/// <remarks>
/// <para>
/// BODY: a 2-byte compact prefix over a 31-byte blob with innerSize 31 and msgTag 58105, body length 33.
/// </para>
/// <code>
/// body+0 u8 compact width marker (1)
/// body+1 u8 blob length (31)
/// blob+0 u16 innerSize = 31
/// blob+2 u16 msgTag = 58105
/// blob+4 u32 actor handle
/// blob+8 u32 (not field-mapped, so not used here)
/// blob+12 u8 bag slot of the item
/// blob+13 u8 bag (container) id
/// blob+14 u64 uid of the item being broken down
/// blob+22 u8 remaining stack count
/// blob+23 u64 uid of the break-down stone
/// </code>
/// <para>
/// blob+14 is THE item: the 2417 answer repeats that uid at its own blob+14. blob+12, blob+13 and blob+22 are
/// echoed byte-for-byte in the answer; blob+22 is a stack count, not a slot.
/// </para>
/// <para>
/// The 2417 answer is <c>[u16 innerSize][u16 msgTag 58106][u32 handle][u16 result][u8 n][u8 n]
/// [u8 slot][u8 bag][u64 srcUid][u8 remaining][n x 126-byte material record]</c>, i.e. blob =
/// 23 + 126n (body lengths 25/152/278/404/530 for n = 0..4).
/// </para>
/// <para>
/// blob+23 names the break-down STONE the player put in: its template picks the ItemBreakInfo row. The
/// BreakNeedItemNum stones themselves come out of every bag stack of that template, lowest cell first, one
/// 2379 per stack touched right after the 2329 that consumes the item (see HandleAsync). The outputs are the
/// BreakResult row the ItemBreakInfo row names
/// (BreakResultID_1, or the group row BreakResultID_2), each rolled on its own at Rate/10000. What this
/// handler cannot do, it refuses without consuming anything: an empty 25-byte answer (outcome 0) would make the
/// client print "Disassembly failed and the equipment and disassembly stone is consumed" although nothing was
/// consumed.
/// </para>
/// </remarks>
public class BreakDownItemHandler : ZoneRmiHandlerBase<BreakDownItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBreakDownItem;
    public override string Name => "BreakDownItem";

    /// <summary>Request blob length: 31 bytes.</summary>
    private const int BlobLength = 31;

    /// <summary>Request blob+12: bag slot of the item.</summary>
    private const int SlotOffset = 12;

    /// <summary>Request blob+13: bag (container) id.</summary>
    private const int BagOffset = 13;

    /// <summary>Request blob+14: u64 uid of the item being broken down.</summary>
    private const int SourceUidOffset = 14;

    /// <summary>Request blob+22: remaining stack count, echoed in the answer.</summary>
    private const int RemainingOffset = 22;

    /// <summary>Request blob+23: u64 uid of the break-down stone.</summary>
    private const int SecondUidOffset = 23;

    /// <summary>InventoryItem.StorageType of a bag row; worn, bank and bag-window rows use other values.</summary>
    private const byte BagStorageType = 0;

    /// <summary>2329 OnUseItem useSource of the item consumed by a break-down: 3.</summary>
    private const byte UseSourceBreakDown = 3;

    /// <summary>126-byte item record, offset +14: the u8 stack count. Count 0 makes the client empty the cell.</summary>
    private const int ItemRecordCountOffset = 14;

    /// <summary>BreakResult output rates are out of 10000 (a Rate of 10000 always drops).</summary>
    private const int RateScale = 10000;

    /// <summary>Stack size of an output item the item table does not describe.</summary>
    private const byte DefaultStackMax = 1;

    public BreakDownItemHandler(IServiceScopeFactory s, ILogger<BreakDownItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        // Strict: the declared blob length must equal the bytes that follow (31 == 31).
        if (!body.TryGetCompactBlob(CompactLengthCheck.Strict, out var blob) || blob.Length < BlobLength)
        {
            Logger.LogWarning("Session {Id}: BreakDownItem body is {Len} bytes, not the 33-byte compact frame retail sends",
                session.SessionId, body.Length);
            return;
        }

        byte slot = blob[SlotOffset];
        byte bag = blob[BagOffset];
        ulong sourceUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(SourceUidOffset, 8));
        byte remaining = blob[RemainingOffset];
        ulong secondUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(SecondUidOffset, 8));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // Validation. A refusal is a 2417 with result 22648, the request echoed and no output records,
        // and nothing consumed (no 2329, no 2379 before it).
        var source = await inventory.GetByIdAsync(sourceUid);
        var stone = secondUid != 0 && secondUid != sourceUid ? await inventory.GetByIdAsync(secondUid) : null;
        if (!InBag(source, player) || !InBag(stone, player))
        {
            await RefuseAsync(session, player, slot, bag, sourceUid, remaining, "the item or the stone is not in the bag");
            return;
        }
        uint sourceItemId = source.ItemId, stoneItemId = stone.ItemId;
        // ItemBreakInfo can hold several rows for one item, one per usable stone, so the stone the player
        // put in picks the row.
        var rule = store.ItemBreakInfo.FirstOrDefault(r => r.Item_ID == sourceItemId && r.BreakNeedItemID == stoneItemId);
        if (rule is null)
        {
            await RefuseAsync(session, player, slot, bag, sourceUid, remaining,
                $"no ItemBreakInfo row for item {sourceItemId} with stone {stoneItemId}");
            return;
        }
        var result = ResolveBreakResult(rule, store.BreakResult);
        if (result is null)
        {
            await RefuseAsync(session, player, slot, bag, sourceUid, remaining,
                $"item {sourceItemId}'s BreakResult row ({rule.BreakResultID_1}/{rule.BreakResultID_2}) is not in the data");
            return;
        }

        // The stones come from every bag stack of the stone's template, lowest cell first, not only from the
        // stack the request names: the named stone picks the ItemBreakInfo row, BreakNeedItemNum is taken
        // from the bag total.
        var bagRows = await inventory.GetByCharacterAsync(player.CharacterId);
        var stoneTakes = PlanStoneTake(bagRows, stoneItemId, rule.BreakNeedItemNum, sourceUid);
        if (stoneTakes is null)
        {
            await RefuseAsync(session, player, slot, bag, sourceUid, remaining,
                $"needs {rule.BreakNeedItemNum}x stone {stoneItemId}, the bag holds fewer");
            return;
        }

        // Consume in order: the item (2329, remaining = count after, echoing the request's slot/bag), then
        // the stones (one 2379 per stack touched, in cell order, each carrying the stack's record as it now
        // stands; count 0 clears the cell).
        int sourceLeft = source.Count - 1;
        if (sourceLeft <= 0) { await inventory.RemoveItemAsync(source.Id); sourceLeft = 0; }
        else { source.Count = sourceLeft; await inventory.UpdateItemAsync(source); }
        var useBody = CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, source.Id,
            (byte)Math.Clamp(sourceLeft, 0, byte.MaxValue), useSource: UseSourceBreakDown);
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(useBody));

        // A source with a skill (ItemInfo Skill_ID_1 != 0, i.e. the potions) also gets the user's own 2330
        // OnUseItemNotify between the 2329 and the first 2379. Unicast to the user. sourceItemId was read
        // before the source row may have been deleted.
        if (store.GetItem(sourceItemId) is { Skill_ID_1: not 0 })
        {
            var notifyBody = LootNotifyPackets.BuildUseItemNotify2330(player.EntityId, sourceItemId);
            await session.SendRmiAsync(ZoneS2COpcodes.OnUseItemNotify, writer => writer.WriteBytes(notifyBody));
        }

        foreach (var (stack, left) in stoneTakes)
        {
            byte[] stoneRecord;
            if (left <= 0)
            {
                stoneRecord = WorldEntryService.BuildItemRecord126(stack);
                stoneRecord[ItemRecordCountOffset] = 0;   // count 0 -> the client empties the cell (the record writer clamps to >= 1)
                await inventory.RemoveItemAsync(stack.Id);
            }
            else
            {
                stack.Count = left;
                await inventory.UpdateItemAsync(stack);
                stoneRecord = WorldEntryService.BuildItemRecord126(stack);
            }
            var stoneBody = ShopItemPackets.BuildDestroyItem2379(player.EntityId, stoneRecord);
            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem, writer => writer.WriteBytes(stoneBody));
        }

        // Roll every output of the BreakResult row independently at Rate/RateScale, then stack each into the
        // bag; the answer carries the stack as it now stands.
        var outputs = new (uint Item, int Num, int Rate)[]
        {
            (result.BreakResultItemID1, result.BreakResultItemNum1, result.BreakResultRate1),
            (result.BreakResultItemID2, result.BreakResultItemNum2, result.BreakResultRate2),
            (result.BreakResultItemID3, result.BreakResultItemNum3, result.BreakResultRate3),
            (result.BreakResultItemID4, result.BreakResultItemNum4, result.BreakResultRate4),
        };
        var records = new List<byte[]>();
        var rng = Random.Shared;
        foreach (var (item, num, rate) in outputs)
        {
            if (item == 0 || num <= 0 || rng.Next(RateScale) >= rate) continue;
            var placed = await InventoryStacking.AddAsync(inventory, player.CharacterId, item, num,
                store.GetItem(item)?.Stack_Max ?? DefaultStackMax);
            foreach (var p in placed) records.Add(WorldEntryService.BuildItemRecord126(p.Stack));
        }

        // OnBreakDownItem (2417): result 0, then the request's slot/bag/uid/remaining echo and the output
        // records. The outcome byte is the record count: non-zero selects the success text, 0 the "failed,
        // item and stone consumed" text, which is accurate here since both were consumed above.
        var breakBody = ShopItemPackets.BuildBreakDownItem2417(player.EntityId, 0, slot, bag, sourceUid, remaining, records);
        await session.SendRmiAsync(ZoneS2COpcodes.OnBreakDownItem,
            writer => writer.WriteBytes(breakBody));

        Logger.LogInformation("{Name} broke down '{Item}' ({Uid}) with {StoneCount}x stone {Stone} from {Stacks} stack(s): {K} output stack(s) from result {Result}",
            player.Name, store.GetItem(sourceItemId)?.Name ?? sourceItemId.ToString(), sourceUid,
            rule.BreakNeedItemNum, stoneItemId, stoneTakes.Count, records.Count, result.Result_ID);
    }

    /// <summary>
    /// The BreakResult row an ItemBreakInfo row rolls: <c>BreakResultID_1</c> when set, else the group in
    /// <c>BreakResultID_2</c>. Null when the data holds no such row.
    /// </summary>
    /// <remarks>
/// Every ItemBreakInfo row sets exactly one of the two; the ID_2 values are BreakResult rows as well: the table
/// keeps the groups as its low Result_IDs next to the 25001+ and 850001+ rows ID_1 names. A group row is what
/// potion break-downs use (see HandleAsync), so it is rolled the same way as an ID_1 row.
    /// </remarks>
    internal static BreakResult? ResolveBreakResult(ItemBreakInfo rule, IEnumerable<BreakResult> results)
    {
        uint id = rule.BreakResultID_1 != 0 ? rule.BreakResultID_1 : rule.BreakResultID_2;
        return id == 0 ? null : results.FirstOrDefault(r => r.Result_ID == id);
    }

    /// <summary>
    /// The bag stacks <paramref name="need"/> stones of <paramref name="stoneItemId"/> come from and the count
    /// each is left with, lowest cell first, or null when the bag holds fewer than <paramref name="need"/>.
    /// Only the stacks actually touched are listed (retail sends a 2379 for each of those and no other).
    /// </summary>
    internal static List<(InventoryItem Stack, int Left)>? PlanStoneTake(IEnumerable<InventoryItem> bagRows,
        uint stoneItemId, int need, ulong sourceUid)
    {
        // The take order (lowest cell first, exclude the source uid) is the economy planner's shared primitive; the
        // host maps its rows in and resolves the chosen stack ids back to the live inventory rows it mutates.
        var rows = bagRows.Where(i => i.StorageType == BagStorageType).ToList();
        var plan = BagStackPlanner.TakeFromStacks(
            rows.Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList(), stoneItemId, need, sourceUid);
        if (plan is null) return null;
        var byId = rows.ToDictionary(i => i.Id);
        return plan.Select(t => (byId[t.StackId], t.Left)).ToList();
    }

    /// <summary>2417 result code for a refused break-down (the request echoed, k = 0, and no 2329/2379 before it).</summary>
    private const ushort ResultRefused = 22648;

    private static bool InBag([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] InventoryItem? it,
        PlayerState player) =>
        it is not null && it.CharacterId == player.CharacterId && it.StorageType == BagStorageType;

    private async Task RefuseAsync(ProudSession session, PlayerState player, byte slot, byte bag, ulong sourceUid,
        byte remaining, string why)
    {
        Logger.LogInformation("{Name} break-down of uid {Uid} refused: {Why}", player.Name, sourceUid, why);
        var body = ShopItemPackets.BuildBreakDownItem2417(player.EntityId, ResultRefused, slot, bag, sourceUid,
            remaining, System.Array.Empty<byte[]>());
        await session.SendRmiAsync(ZoneS2COpcodes.OnBreakDownItem, writer => writer.WriteBytes(body));
    }
}
