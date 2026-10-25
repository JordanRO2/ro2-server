using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Item-change exchange for the Pandora's items: a key item used on its target consumes one of each and gains
/// one entry from the key's random set. Handles 1584 ReqExtendCacheChangeItem and builds the 2851
/// AnsExtendCacheChangeItem answer.
/// </summary>
/// <remarks>
/// <para>
/// An exchange gains one stack from the key's random set, weighted by ratio. The gained stack is placed on an
/// existing stack of the same template when it fits, otherwise in a new stack on a free cell. Only the key
/// types that appear in the shipped data are performed; the other types are refused.
/// </para>
/// <para>
/// The answer carries the counts left on both items and the gained record. Its ability value field is taken
/// from the gained template.
/// </para>
/// </remarks>
internal static class ItemChangeExchange
{
 /// <summary>1584 body length.</summary>
    public const int RequestLength = 20;

 /// <summary>2851 body length: a 21-byte head plus one 126-byte record.</summary>
    public const int AnswerLength = HeadLength + RecordLength;

    /// <summary>Length of the 2851 head that precedes the gained item record.</summary>
    private const int HeadLength = 21;

    /// <summary>Length of the item record that ends the 2851 body.</summary>
    private const int RecordLength = 126;

    // 1584 request offsets: [u8 bagA][u8 slotA][u64 uidA][u8 bagB][u8 slotB][u64 uidB].
    private const int ReqBagA = 0;
    private const int ReqSlotA = 1;
    private const int ReqUidA = 2;
    private const int ReqBagB = 10;
    private const int ReqSlotB = 11;
    private const int ReqUidB = 12;

    // 2851 head offsets: [u16 result][u8 change type][u8 key count left][u64 uidA][u64 uidB][u8 target count left].
    private const int AnsChangeType = 2;
    private const int AnsKeyLeft = 3;
    private const int AnsUidA = 4;
    private const int AnsUidB = 12;
    private const int AnsTargetLeft = 20;

    /// <summary>Items of the key stack consumed by one exchange.</summary>
    private const int KeyUse = 1;

    /// <summary>InventoryItem.StorageType of the main bag.</summary>
    private const byte BagStorageType = 0;

    /// <summary>Durability recorded for a gained template that has no item data.</summary>
    private const short NoDurability = -1;

    /// <summary>Placement cell of a gain that tops up an existing stack (no new cell is allocated).</summary>
    private const short NoCell = -1;

    // 2851 result codes. A non-zero code makes the client show the matching message and ignore the rest of the
    // body.
    public const ushort ResultOk = 0;
 /// <summary>Result 1: server system error.</summary>
    public const ushort ResultSystemError = 1;
 /// <summary>Result 2: there is no item in that position.</summary>
    public const ushort ResultNoItem = 2;
 /// <summary>Result 3: the item cannot be changed.</summary>
    public const ushort ResultCannotChange = 3;
 /// <summary>Result 5: not enough of the required items.</summary>
    public const ushort ResultNotEnough = 5;
 /// <summary>Result 8: there is no space in the bag.</summary>
    public const ushort ResultBagFull = 8;
 /// <summary>Result 10: the item cannot be used with the current target.</summary>
    public const ushort ResultWrongMalletTarget = 10;
 /// <summary>Result 11: the key cannot be used on this target.</summary>
    public const ushort ResultWrongKeyTarget = 11;

 /// <summary>Change_Type of the Pandora's Hammer.</summary>
    public const byte ChangeTypeHammer = 3;

 /// <summary>Change_Type of the Pandora's Keys.</summary>
    public const byte ChangeTypeKey = 4;

 /// <summary>The random set NationEnable bit the draw requires.</summary>
    public const byte NationBit = 0x01;

 /// <summary>
 /// A 1584 request: <c>[u8 bagA][u8 slotA][u64 uidA][u8 bagB][u8 slotB][u64 uidB]</c>, with no envelope.
 /// Bags are the client containers 1 to 4; the items are found by uid.
 /// </summary>
    public readonly record struct Request(byte BagA, byte SlotA, ulong UidA, byte BagB, byte SlotB, ulong UidB);

    public static bool TryParseRequest(ReadOnlySpan<byte> body, out Request request)
    {
        request = default;
        if (body.Length < RequestLength) return false;
        request = new Request(body[ReqBagA], body[ReqSlotA], BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ReqUidA)),
            body[ReqBagB], body[ReqSlotB], BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ReqUidB)));
        return true;
    }

 /// <summary>The result of one exchange: the 2851 body to send and, on success, the gained item.</summary>
    public sealed record Outcome(ushort Result, byte[] Body, byte ChangeType, uint GainedItemId, int GainedCount);

    // One exchange per character at a time: it reads both items and the bag, then writes all three, so
    // overlapping requests would otherwise each gain an item for one consumption.
    private static readonly ConcurrentDictionary<uint, SemaphoreSlim> Gates = new();

 /// <summary>
 /// Performs the exchange of key <paramref name="uidA"/> on target <paramref name="uidB"/> for
 /// <paramref name="characterId"/> and builds the 2851 body. <paramref name="nextInt64"/> draws a value in
 /// [0, max).
 /// </summary>
    public static async Task<Outcome> ExchangeAsync(IInventoryRepository inventory, GameDataStore store,
        uint characterId, ulong uidA, ulong uidB, Func<long, long> nextInt64)
    {
        var gate = Gates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await ExchangeCoreAsync(inventory, store, characterId, uidA, uidB, nextInt64);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Outcome> ExchangeCoreAsync(IInventoryRepository inventory, GameDataStore store,
        uint characterId, ulong uidA, ulong uidB, Func<long, long> nextInt64)
    {
        Outcome Refuse(ushort code, byte changeType = 0) => new(code, BuildRefusal2851(code, uidA), changeType, 0, 0);

        var a = uidA == uidB ? null : await inventory.GetByIdAsync(uidA);
        var b = uidA == uidB ? null : await inventory.GetByIdAsync(uidB);
        if (!InBag(a, characterId) || !InBag(b, characterId)) return Refuse(ResultNoItem);

        var row = store.ItemChangeInfo.FirstOrDefault(r => r.ID == a.ItemId);
        var check = Check(row, b.ItemId, b.Count);
        if (check != ResultOk || row is null) return Refuse(check, row?.Change_Type ?? 0);

        var candidates = Candidates(store.RandomSet, store.GetItem(a.ItemId)?.RandomSet_ID ?? 0);
        long totalRatio = candidates.Sum(r => (long)r.Ratio);
        var drawn = totalRatio > 0 ? Pick(candidates, nextInt64(totalRatio)) : null;
        if (drawn is null) return Refuse(ResultCannotChange, row.Change_Type);

        var template = store.GetItem(drawn.Item);
        int perStack = Math.Max(1, (int)(template?.Stack_Max ?? 1));
        int quantity = Math.Max(1, (int)drawn.Package);
        // One 2851 carries one record, so the gain must fit one stack.
        if (quantity > perStack) return Refuse(ResultCannotChange, row.Change_Type);

        int bUse = TargetUse(row);
        bool aEmpties = a.Count <= KeyUse;
        bool bEmpties = b.Count <= bUse;
        var owned = await inventory.GetByCharacterAsync(characterId);
        var plan = PlanGain(owned, store.Items, a, aEmpties, b, bEmpties, drawn.Item, quantity, perStack);
        if (plan is null) return Refuse(ResultBagFull, row.Change_Type);

        // Consume first, then gain: a failed write can lose the exchange but never duplicate the item.
        int aLeft = await ConsumeAsync(inventory, a, KeyUse);
        int bLeft = await ConsumeAsync(inventory, b, bUse);

        InventoryItem stack;
        if (plan.Restack is { } target)
        {
            target.Count += quantity;
            await inventory.UpdateItemAsync(target);
            stack = target;
        }
        else
        {
            stack = new InventoryItem
            {
                CharacterId = characterId,
                ItemId = drawn.Item,
                Count = quantity,
                Slot = plan.NewCell,                 // a real free cell, never -1
                StorageType = BagStorageType,
                // A fresh stack carries the template's durability.
                Durability = template is null ? NoDurability : (short)Math.Min(template.Durability, (uint)short.MaxValue),
            };
            await inventory.AddItemAsync(stack);   // stamps the uid; the slot is kept
        }

        var body = BuildPerformed2851(row.Change_Type, aLeft, a.Id, b.Id, bLeft, WorldEntryService.BuildItemRecord126(stack));
        return new Outcome(ResultOk, body, row.Change_Type, drawn.Item, quantity);
    }

    private static bool InBag([NotNullWhen(true)] InventoryItem? item, uint characterId)
        => item is not null && item.CharacterId == characterId && item.StorageType == BagStorageType && item.Count >= 1;

 /// <summary>
 /// Returns the refusal result for a key row on a target of the given template and count, or <c>ResultOk</c>
 /// when the exchange may proceed.
 /// </summary>
 /// <remarks>
 /// Only change types 3 and 4 are performed. The other types are refused with the cannot-change result.
 /// </remarks>
    internal static ushort Check(ItemChangeInfo? row, uint targetItemId, int targetCount)
    {
        if (row is null) return ResultCannotChange;
        if (row.Change_Type is not (ChangeTypeHammer or ChangeTypeKey)) return ResultCannotChange;
        if (row.Target_ItemID != targetItemId)
            return row.Change_Type == ChangeTypeHammer ? ResultWrongMalletTarget : ResultWrongKeyTarget;
        if (targetCount < TargetUse(row)) return ResultNotEnough;
        return ResultOk;
    }

 /// <summary>Number of target items one exchange uses: the row's target count, at least 1.</summary>
    private static int TargetUse(ItemChangeInfo row) => Math.Max(1, (int)row.Target_ItemCount);

 /// <summary>
 /// Returns the rows of random set <paramref name="setId"/> that the exchange draws from: those enabled for the
 /// nation and with a positive ratio, in table order.
 /// </summary>
    internal static List<RandomSet> Candidates(IReadOnlyList<RandomSet> table, uint setId)
    {
        var rows = new List<RandomSet>();
        if (setId == 0) return rows;
        foreach (var r in table)
            if (r.ID == setId && (r.NationEnable & NationBit) != 0 && r.Ratio > 0) rows.Add(r);
        return rows;
    }

 /// <summary>
 /// Returns the row whose ratio band holds <paramref name="draw"/>, where 0 is at most draw and draw is less
 /// than the sum of ratios. Each row is drawn with probability ratio divided by the sum. Null past the last band.
 /// </summary>
    internal static RandomSet? Pick(IReadOnlyList<RandomSet> candidates, long draw)
    {
        if (draw < 0) return null;
        foreach (var r in candidates)
        {
            if (draw < r.Ratio) return r;
            draw -= r.Ratio;
        }
        return null;
    }

 /// <summary>
 /// Where the gained stack goes: an existing stack it tops up (<paramref name="Restack"/>), or the free cell of
 /// a new stack (<paramref name="NewCell"/>).
 /// </summary>
    internal sealed record Placement(InventoryItem? Restack, short NewCell);

 /// <summary>
 /// Plans where <paramref name="quantity"/> of <paramref name="gainId"/> goes, without writing anything. Returns
 /// null when the bag has no room.
 /// </summary>
 /// <remarks>
 /// The key and target stacks are never topped up, so one stack is not reported twice with two counts.
 /// </remarks>
    internal static Placement? PlanGain(IReadOnlyList<InventoryItem> owned, IReadOnlyDictionary<uint, ItemData> items,
        InventoryItem a, bool aEmpties, InventoryItem b, bool bEmpties, uint gainId, int quantity, int perStack)
    {
        if (perStack > 1)
        {
            var target = owned
                .Where(i => i.StorageType == BagStorageType && i.ItemId == gainId && i.Count > 0 && i.Count + quantity <= perStack
                            && i.Id != a.Id && i.Id != b.Id)
                .OrderBy(i => i.Slot)
                .FirstOrDefault();
            if (target is not null) return new Placement(target, NoCell);
        }
        var free = BagCapacity.FreeCells(
            owned.Where(i => !(aEmpties && i.Id == a.Id) && !(bEmpties && i.Id == b.Id)), items);
        return free.Count == 0 ? null : new Placement(null, free[0]);
    }

 /// <summary>
 /// Takes <paramref name="use"/> from the item's stack, deleting the stack at zero. Returns the count left.
 /// </summary>
    private static async Task<int> ConsumeAsync(IInventoryRepository inventory, InventoryItem item, int use)
    {
        int left = item.Count - use;
        if (left <= 0)
        {
            await inventory.RemoveItemAsync(item.Id);
            return 0;
        }
        item.Count = left;
        await inventory.UpdateItemAsync(item);
        return left;
    }

 /// <summary>
 /// Builds the 2851 body of a performed exchange, 147 bytes: <c>[u16 0][u8 change type][u8 key count left][u64 uidA][u64 uidB][u8 target count left][126-byte record of the gained stack]</c>.
 /// </summary>
    public static byte[] BuildPerformed2851(byte changeType, int aLeft, ulong uidA, ulong uidB, int bLeft,
        ReadOnlySpan<byte> gainedRecord126)
    {
        var body = new byte[AnswerLength];
        var s = body.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, ResultOk);
        s[AnsChangeType] = changeType;
        s[AnsKeyLeft] = (byte)Math.Clamp(aLeft, 0, byte.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(AnsUidA), uidA);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(AnsUidB), uidB);
        s[AnsTargetLeft] = (byte)Math.Clamp(bLeft, 0, byte.MaxValue);
        gainedRecord126[..Math.Min(gainedRecord126.Length, RecordLength)].CopyTo(s.Slice(HeadLength));
        return body;
    }

 /// <summary>
 /// Builds the 2851 body of a refusal: <c>[u16 code][u16 0][u64 uidA]</c>, zero-padded to 147 bytes.
 /// </summary>
    public static byte[] BuildRefusal2851(ushort code, ulong uidA)
    {
        var body = new byte[AnswerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, code);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(AnsUidA), uidA);
        return body;
    }
}
