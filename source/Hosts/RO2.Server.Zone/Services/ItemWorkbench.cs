using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Shared helpers for the item workbench handlers (socket opening, socket extraction, reinforcement transfer) and for
/// any handler that consumes a bag stack.
/// </summary>
internal static class ItemWorkbench
{
 /// <summary>
 /// Client (bag, slot) of an item, as written into the item record by <c>WorldEntryService.WriteItemInstance</c>.
 /// </summary>
 /// <remarks>
 /// The consumed stack's bag and slot are echoed back by OnUseItem (2329), so they must match the record writer's mapping.
 /// </remarks>
    public static (byte Bag, byte Slot) ClientBagSlot(InventoryItem it)
    {
        if (it.StorageType == 1) return (0, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, 49));
        if (it.StorageType == 2) return (5, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, BankExpansion.MaxBankSlots - 1));
        if (it.StorageType == 3) return (1, (byte)(70 + Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, 2)));
        var flat = it.Slot < 0 ? 0 : (int)it.Slot;
        if (flat < 20) return (1, (byte)(50 + flat));
        var i = flat - 20;
        return ((byte)Math.Clamp(2 + i / 35, 2, 4), (byte)(i % 35));
    }

    
 /// <summary>Record offset of the runehole block (5 holes of 7 bytes each).</summary>
    public const int RuneholeOffset = 33;

 /// <summary>Number of socket holes per item.</summary>
    public const int HoleCount = 5;

    private const int HoleSize = 7;

 /// <summary>
 /// Number of holes opened on the item. Each entry of <c>CardsJson</c> is one opened hole; an empty hole holds 0.
 /// </summary>
    public static int OpenedHoles(InventoryItem item) =>
        Math.Min(ItemSockets.ParseSockets(item.CardsJson).Length, HoleCount);

 /// <summary>
 /// Hole kind of the hole at <paramref name="index"/> (0-based) of an item template, taken from its ItemSocketGroup row.
 /// 0 means the item has no such hole.
 /// </summary>
    public static byte HoleKind(GameDataStore store, uint templateId, int index)
    {
        if ((uint)index >= HoleCount) return 0;
        var info = store.GetItem(templateId);
        if (info is null || info.Socket_GroupID == 0) return 0;
        uint groupId = info.Socket_GroupID;
        var group = store.ItemSocketGroup.FirstOrDefault(g => g.SocketGroupID == groupId);
        if (group is null) return 0;
        return index switch
        {
            0 => group.Socket_Group_Type1,
            1 => group.Socket_Group_Type2,
            2 => group.Socket_Group_Type3,
            3 => group.Socket_Group_Type4,
            _ => group.Socket_Group_Type5,
        };
    }

    private static Dictionary<uint, ushort>? _reinforceIdByItem;
    private static GameDataStore? _reinforceIdStore;

 /// <summary>
 /// Value stored for a filled hole: the Reinforce_ID of the ItemReinforceInfo row whose SourceItem_ID is the socketed item.
 /// Returns 0 when the item has no such row.
 /// </summary>
    public static ushort ReinforceId(GameDataStore store, uint socketedItemId)
    {
        if (socketedItemId == 0) return 0;
        var map = _reinforceIdByItem;
        if (map is null || !ReferenceEquals(_reinforceIdStore, store))
        {
            map = new Dictionary<uint, ushort>(store.ItemReinforceInfo.Count);
            foreach (var r in store.ItemReinforceInfo) map.TryAdd(r.SourceItem_ID, r.Reinforce_ID);
            _reinforceIdByItem = map;
            _reinforceIdStore = store;
        }
        return map.TryGetValue(socketedItemId, out var id) ? id : (ushort)0;
    }

 /// <summary>
 /// Writes the runehole block of <paramref name="item"/> into a 126-byte item record.
 /// </summary>
 /// <remarks>
 /// Each hole is <c>[u8 kind][u16 value][u32 puncher template id]</c>. Unopened holes are seven zero bytes, and opened
 /// holes are contiguous from hole 0. The puncher template id comes from <see cref="InventoryItem.SocketPunchersJson"/>;
 /// a hole without a recorded puncher is written as 0.
 /// </remarks>
    public static void ApplyRuneholes(byte[] record126, InventoryItem item, GameDataStore store)
    {
        if (record126.Length < RuneholeOffset + HoleCount * HoleSize) return;
        var block = record126.AsSpan(RuneholeOffset, HoleCount * HoleSize);
        block.Clear();
        var holes = ItemSockets.ParseSockets(item.CardsJson);
        var punchers = ItemSockets.ParseSockets(item.SocketPunchersJson);
        int opened = Math.Min(holes.Length, HoleCount);
        for (int i = 0; i < opened; i++)
        {
            var hole = block.Slice(i * HoleSize, HoleSize);
            hole[0] = HoleKind(store, item.ItemId, i);
            BinaryPrimitives.WriteUInt16LittleEndian(hole.Slice(1), ReinforceId(store, holes[i]));
            BinaryPrimitives.WriteUInt32LittleEndian(hole.Slice(3), i < punchers.Length ? punchers[i] : 0u); 
        }
    }

 /// <summary>Returns the item record with its runehole block filled in.</summary>
    public static byte[] RecordWithRuneholes(InventoryItem item, GameDataStore store)
    {
        var rec = ItemPackets.BuildItemRecord126(item);
        ApplyRuneholes(rec, item, store);
        return rec;
    }
}
