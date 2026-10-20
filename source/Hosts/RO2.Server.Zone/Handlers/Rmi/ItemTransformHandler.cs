using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1535 ReqItemTransform: turns one item into the transform target the player chose.</summary>
/// <remarks>
/// <para>
/// Request body (14 bytes, no envelope): [u64 source item uid][u32 target item id][u16 target count].
/// Each target item id is an ItemTransform.TargetItemID.
/// </para>
/// <para>
/// Reply sequence: 2329 OnUseItem for the source, 2335 OnCreateItem with the target stack's record, then
/// 2772 AnsItemTransform with result 0 followed by the 14 request bytes.
/// </para>
/// <para>
/// A request whose target has no room is dropped with nothing consumed and nothing sent.
/// </para>
/// <para>
/// Room: the target fits when it tops up an existing stack of its template (count + TargetItemCount &lt;= Stack_Max),
/// or when the bag has a free usable cell for each new stack (<see cref="BagCapacity.FreeCells"/>). The source's own
/// cell counts as free when the transform consumes its last unit. New stacks go to the lowest such cell.
/// </para>
/// </remarks>
public class ItemTransformHandler : ZoneRmiHandlerBase<ItemTransformHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqItemTransform;
    public override string Name => "ItemTransform";

    /// <summary>Request body offset 0: u64 uid of the bag item to transform.</summary>
    private const int RequestSourceUidOffset = 0;

    /// <summary>Request body offset 8: u32 template id of the chosen target (an ItemTransform.TargetItemID).</summary>
    private const int RequestTargetItemIdOffset = 8;

    /// <summary>Request body offset 12: u16 target count the client displays; the data row decides the real count.</summary>
    private const int RequestTargetCountOffset = 12;

    /// <summary>Request body length: 14 bytes.</summary>
    private const int RequestLength = RequestTargetCountOffset + sizeof(ushort);

    /// <summary>InventoryItem.StorageType of a bag row; worn, bank and bag-window rows use other values.</summary>
    private const byte BagStorageType = 0;

    /// <summary>Units of the source item consumed by one transform.</summary>
    private const int SourceUnitsConsumed = 1;

    /// <summary>Stack size assumed for a target template the item table does not describe.</summary>
    private const byte DefaultStackMax = 1;

    /// <summary>126-byte item record, offset +15: the bag (container) id.</summary>
    private const int ItemRecordBagOffset = 15;

    /// <summary>126-byte item record, offset +16: the cell within the bag.</summary>
    private const int ItemRecordSlotOffset = 16;

    /// <summary>Result code 0 (success) of 2772 AnsItemTransform and of the 2335 result guard.</summary>
    private const int ResultOk = 0;

    /// <summary>2335 OnCreateItem message tag (58027, 0xE2AB).</summary>
    internal const ushort MsgTagCreateItem = 58027;

    public ItemTransformHandler(IServiceScopeFactory s, ILogger<ItemTransformHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestLength) return;
        var request = payload.Span.Slice(0, RequestLength).ToArray();
        ulong sourceUid = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(RequestSourceUidOffset));
        uint targetItemId = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(RequestTargetItemIdOffset));
        ushort targetCount = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(RequestTargetCountOffset));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // The source must be the player's own bag item, and the pair (source template -> target) must be a row of
        // ItemTransform.json; the row, not the request, decides how many targets are granted. An invalid request
        // consumes nothing and gets no answer.
        var source = await inventory.GetByIdAsync(sourceUid);
        if (source is null || source.CharacterId != player.CharacterId || source.StorageType != BagStorageType || source.Count <= 0)
        {
            Logger.LogInformation("{Name} item transform refused: uid {Uid} is not in the bag", player.Name, sourceUid);
            return;
        }
        uint sourceItemId = source.ItemId;
        var row = store.ItemTransform.FirstOrDefault(r => r.ID == sourceItemId && r.TargetItemID == targetItemId);
        if (row is null || row.TargetItemCount == 0)
        {
            Logger.LogInformation("{Name} item transform refused: no ItemTransform row {Src} -> {Dst}",
                player.Name, sourceItemId, targetItemId);
            return;
        }

        // Room for the target, decided before anything is consumed.
        var owned = await inventory.GetByCharacterAsync(player.CharacterId);
        byte stackMax = store.GetItem(row.TargetItemID)?.Stack_Max ?? DefaultStackMax;
        var cells = TargetCells(owned, store.Items, source, row.TargetItemID, row.TargetItemCount, stackMax);
        if (cells is null)
        {
            Logger.LogInformation("{Name} item transform refused: no bag room for {Count}x {Dst} (uid {Uid} kept)",
                player.Name, row.TargetItemCount, row.TargetItemID, sourceUid);
            return;
        }

        // 2329: consume one source. (slot, bag) are taken from the record the client was given for it, so they are
        // the cell the client shows; remaining = count after, 0 clears the cell.
        var sourceRecord = WorldEntryService.BuildItemRecord126(source);
        byte bag = sourceRecord[ItemRecordBagOffset], slot = sourceRecord[ItemRecordSlotOffset];
        int remaining = source.Count - SourceUnitsConsumed;
        if (remaining <= 0) { await inventory.RemoveItemAsync(source.Id); remaining = 0; }
        else { source.Count = remaining; await inventory.UpdateItemAsync(source); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, sourceUid, (byte)Math.Clamp(remaining, 0, byte.MaxValue))));

        // 2335: the target, stacked like a purchase, one 2335 per stack it landed in.
        var placed = await InventoryStacking.AddAsync(new CellPlacingInventory(inventory, cells), player.CharacterId,
            row.TargetItemID, row.TargetItemCount, stackMax);
        foreach (var p in placed)
        {
            var body = BuildCreateItem2335(player.EntityId, WorldEntryService.BuildItemRecord126(p.Stack));
            await session.SendRmiAsync(ZoneS2COpcodes.OnCreateItem, writer => writer.WriteBytes(body));
        }

        // 2772: [u32 result][the 14 request bytes].
        await session.SendRmiAsync(ZoneS2COpcodes.AnsItemTransform, writer =>
        {
            writer.WriteUInt32(ResultOk);
            writer.WriteBytes(request);
        });

        Logger.LogDebug("{Name} transformed uid {Uid} ({Src}) into {Count}x {Dst} (client asked {Asked})",
            player.Name, sourceUid, sourceItemId, row.TargetItemCount, row.TargetItemID, targetCount);
    }

    /// <summary>
    /// Builds 2335 OnCreateItem: compact length, inner size 136, msgTag 58027, PC handle, i16 result 0 and the
    /// 126-byte item record. The client reads the result guard and the item record; the handle is not read.
    /// </summary>
    internal static byte[] BuildCreateItem2335(uint actorHandle, ReadOnlySpan<byte> itemRecord126)
    {
        if (itemRecord126.Length != ItemPackets.ItemRecordLength)
            throw new ArgumentException($"item record must be {ItemPackets.ItemRecordLength} bytes", nameof(itemRecord126));
        var record = itemRecord126.ToArray();
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagCreateItem);  // blob+2
            p.WriteUInt32(actorHandle);       // blob+4
            p.WriteInt16(ResultOk);           // blob+8  result guard
            p.WriteBytes(record);             // blob+10
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Returns the bag cells that new target stacks may take, lowest first, or null when the target has no room.
    /// A target tops up an existing stack when count + targetCount &lt;= Stack_Max (as InventoryStacking decides);
    /// otherwise it needs one free usable cell per new stack of stackMax. The source's cell counts as free when this
    /// transform takes its last unit.
    /// </summary>
    internal static List<short>? TargetCells(IReadOnlyList<InventoryItem> owned, IReadOnlyDictionary<uint, ItemData> items,
        InventoryItem source, uint targetItemId, int targetCount, byte stackMax)
    {
        int perStack = stackMax < 1 ? 1 : stackMax;
        bool topsUp = perStack > 1 && owned.Any(i => i.StorageType == BagStorageType && i.ItemId == targetItemId && i.Count > 0
                                                     && i.Count + targetCount <= perStack);
        int newStacks = topsUp ? 0 : (targetCount + perStack - 1) / perStack;
        bool sourceCellFrees = source.Count <= SourceUnitsConsumed;
        var free = BagCapacity.FreeCells(sourceCellFrees ? owned.Where(i => i.Id != source.Id) : owned, items);
        return free.Count >= newStacks ? free : null;
    }

    /// <summary>
    /// Pass-through <see cref="IInventoryRepository"/> that places each new row added without a slot on the next of
    /// the given cells, so that a new stack lands on a cell the client has.
    /// </summary>
    private sealed class CellPlacingInventory(IInventoryRepository inner, IReadOnlyList<short> cells) : IInventoryRepository
    {
        private int _next;

        public Task AddItemAsync(InventoryItem item)
        {
            if (item.Slot < 0 && item.StorageType == BagStorageType && _next < cells.Count)
                item.Slot = cells[_next++];
            return inner.AddItemAsync(item);
        }

        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) => inner.GetByCharacterAsync(characterId);
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => inner.GetEquipmentAsync(characterId);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => inner.GetByIdAsync(id);
        public Task RemoveItemAsync(ulong itemId) => inner.RemoveItemAsync(itemId);
        public Task UpdateItemAsync(InventoryItem item) => inner.UpdateItemAsync(item);
        public Task<ulong> GetNextIdAsync() => inner.GetNextIdAsync();
        public Task<int> RepairSlotsAsync(uint characterId) => inner.RepairSlotsAsync(characterId);
    }
}
