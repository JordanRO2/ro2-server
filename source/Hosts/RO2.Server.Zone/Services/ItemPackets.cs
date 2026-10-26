using System.Buffers.Binary;
using RO2.Network.Packets;
using RO2.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the item family: 2503 OnUpdateItemDurability and 2326 OnItemInfo.
/// </summary>
/// <remarks>
/// <para>
/// Both messages are enveloped. A malformed envelope is rejected by the client, so the builders produce the
/// envelope and its self length themselves.
/// </para>
/// <para>
/// Offsets in the field comments are relative to the start of the blob that follows the compact length; the
/// first two blob bytes are the self length, which <see cref="PacketWriter.WriteCompactBABody"/> stamps.
/// </para>
/// </remarks>
public static class ItemPackets
{
    /// <summary>Value of a reserved dword that the client does not read.</summary>
    private const uint ReservedZero = 0;

    // ---- 2503 OnUpdateItemDurability ------------------------------------------------------------

    /// <summary>msgTag of 2503: 58317 (0xE3CD).</summary>
    internal const ushort MsgTagUpdateItemDurability = 58317;

    /// <summary>Body length of 2503: 1 marker byte + 1 length byte + 18 blob bytes.</summary>
    internal const int UpdateItemDurabilityBodyLen = 20;

    /// <summary>Full durability, as stored in the ItemInfo data table.</summary>
    public const ushort DurabilityMax = 10000;

    /// <summary>
    /// Builds RMI 2503 OnUpdateItemDurability: one worn item's new absolute durability.
    /// </summary>
    /// <param name="itemUid">The 64-bit item instance uid, as the item record carries it. Not the ItemInfo template id.</param>
    /// <param name="durability">The item's durability after the wear, absolute, from 0 to 10000. Not a delta.</param>
    /// <remarks>
    /// <para>
    /// Layout: <c>[u8 01][u8 0x12][u16 selfLen=18][u16 msgTag][u32 0][u64 itemUid][u16 durability]</c>.
    /// </para>
    /// <para>
    /// The message carries no actor handle, so it is sent only to the owner's own session.
    /// </para>
    /// </remarks>
    public static byte[] BuildItemDurability2503(ulong itemUid, ushort durability)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagUpdateItemDurability); // blob+2  msgTag
            p.WriteUInt32(ReservedZero);               // blob+4  reserved, not read by the client
            p.WriteUInt64(itemUid);                    // blob+8  item instance uid
            p.WriteUInt16(durability);                 // blob+16 absolute durability, 0..DurabilityMax
        });
        return pw.ToArray();
    }

    // ---- 2326 OnItemInfo -------------------------------------------------------------------------

    /// <summary>msgTag of 2326: 58064 (0xE2D0).</summary>
    internal const ushort MsgTagItemInfo = 58064;

    /// <summary>Body length of 2326: 3 envelope bytes (marker + u16 length) + 10 header bytes + the 126-byte record.</summary>
    internal const int ItemInfoBodyLen = 139;

    /// <summary>Length of the item record carried by 2326.</summary>
    public const int ItemRecordLength = 126;

    /// <summary>Item record offset of the u64 item instance uid.</summary>
    private const int RecordUidOffset = 0;

    /// <summary>Item record offset of the u32 ItemInfo template id.</summary>
    private const int RecordItemIdOffset = 8;

    /// <summary>Item record offset of the u16 durability.</summary>
    private const int RecordDurabilityOffset = 12;

    /// <summary>Result code 0 of 2326: the item is shown. Any other value makes the client show a result message instead.</summary>
    private const ushort ResultOk = 0;

    /// <summary>
    /// Builds RMI 2326 OnItemInfo, the answer to 1326 ReqItemDetailInfo, carrying one 126-byte item record.
    /// </summary>
    /// <param name="itemRecord126">
    /// The 126-byte item record, as the inventory list writes it. Must be exactly 126 bytes.
    /// </param>
    /// <param name="resultCode">Result code. Must be 0 for the item to be shown.</param>
    /// <remarks>
    /// <para>
    /// Layout: <c>[u8 02][u16 136][u16 selfLen=136][u16 msgTag][u32 0][u16 resultCode][126-byte item record]</c>.
    /// </para>
    /// <para>
    /// The message is sent only to the session that asked.
    /// </para>
    /// </remarks>
    public static byte[] BuildItemInfo2326(ReadOnlySpan<byte> itemRecord126, ushort resultCode = ResultOk)
    {
        if (itemRecord126.Length != ItemRecordLength)
        {
            throw new ArgumentException(
                $"2326 carries exactly {ItemRecordLength} bytes of item record; got {itemRecord126.Length}. " +
                "A short record makes the client read past the end of the blob.",
                nameof(itemRecord126));
        }

        // The lambda cannot close over a ReadOnlySpan<byte>, so copy once.
        var record = itemRecord126.ToArray();
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagItemInfo); // blob+2  msgTag
            p.WriteUInt32(ReservedZero);   // blob+4  reserved
            p.WriteUInt16(resultCode);     // blob+8  result code; non-zero aborts the client handler
            p.WriteBytes(record);          // blob+10 the item record
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Builds RMI 2326 OnItemInfo from the three known record fields (uid, template id and durability), with the
    /// other 112 record bytes zero-filled.
    /// </summary>
    /// <remarks>
    /// Use this only where no complete item record is available. Zeroed fields such as sockets, enhancement and
    /// dye colours show as empty in the client's tooltip.
    /// </remarks>
    public static byte[] BuildItemInfo2326(ulong itemUid, uint itemId, ushort durability)
    {
        var record = new byte[ItemRecordLength];
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(RecordUidOffset), itemUid);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(RecordItemIdOffset), itemId);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(RecordDurabilityOffset), durability);
        return BuildItemInfo2326(record);
    }

    // ---- item records (moved from WorldEntryService, B5 Phase-0 S2) -------------------------------

 /// <summary>Largest stack count an item record carries.</summary>
    private const int MaxRecordStackCount = 255;
 /// <summary>Length of the grinding statistics block of an item record: 5 values of 4 bytes.</summary>
    private const int GrindingStatsLength = 5 * 4;

 /// <summary>Storage type of a worn item.</summary>
    private const byte StorageWorn = 1;
 /// <summary>Storage type of an item in the bank.</summary>
    private const byte StorageBank = 2;
 /// <summary>Storage type of a bag worn in the character bag window.</summary>
    private const byte StorageEquippedBag = 3;

 /// <summary>Bag id of worn gear; its slots are equipment slots.</summary>
    private const byte BagWorn = 0;
 /// <summary>Bag id of the main inventory bag and the character bag window.</summary>
    private const byte BagMain = 1;
 /// <summary>Bag id of the first extra bag; extra bags run up to <see cref="BagLastExtra"/>.</summary>
    private const int BagFirstExtra = 2;
 /// <summary>Bag id of the last extra bag.</summary>
    private const int BagLastExtra = 4;
 /// <summary>Bag id of the bank.</summary>
    private const byte BagBank = 5;
 /// <summary>Highest equipment slot of worn gear (slots 0 to 49).</summary>
    private const int EquipSlotMax = 49;
 /// <summary>First slot of the main inventory bag (slots 50 to 69).</summary>
    private const int MainBagFirstSlot = 50;
 /// <summary>Number of items the main inventory bag holds before items overflow into the extra bags.</summary>
    private const int MainBagSlotCount = 20;
 /// <summary>Number of slots of each extra bag (slots 0 to 34).</summary>
    private const int ExtraBagSlotCount = 35;
 /// <summary>First slot of the character bag window, which holds the worn bags (slots 70 to 72).</summary>
    private const int CharacterBagFirstSlot = 70;
 /// <summary>Number of slots of the character bag window.</summary>
    private const int CharacterBagSlotCount = 3;

 /// <summary>
 /// Builds one 126-byte item record, for messages that carry a single item rather than a list.
 /// </summary>
 /// <remarks>
 /// Shares the inventory item writer so that every message encodes items the same way. Only the uid, item id and durability offsets are confirmed; the rest is the current model of the record.
 /// </remarks>
    internal static byte[] BuildItemRecord126(RO2.Inventory.Domain.InventoryItem item)
    {
        using var w = new PacketWriter(ItemPackets.ItemRecordLength);
        WriteItemInstance(w, item);
        return w.GetWrittenSpan().ToArray();
    }

 /// <summary>
 /// Writes a single 126-byte item instance, in the same format as the item information message.
 /// </summary>
    internal static void WriteItemInstance(PacketWriter writer, RO2.Inventory.Domain.InventoryItem item)
    {
        var startPos = writer.Length;

        writer.WriteUInt64(item.Id);  // +0x00 item uid
        writer.WriteUInt32(item.ItemId);  // +0x08 template id
        
        
        
        
        
        
        writer.WriteUInt16(item.Durability >= 0 ? (ushort)item.Durability : UnsetRecordDurability(item.ItemId));  // +0x0C durability
        
        
        
        
        writer.WriteByte((byte)Math.Clamp(item.Count <= 0 ? 1 : item.Count, 1, MaxRecordStackCount));  // +0x0E stack count, at least 1
        
        
        
        
        
        
        byte bagIx, slotIx;
        if (item.StorageType == StorageWorn)
        {
            // Worn gear: bag 0, the slot is the equipment slot.
            
            
            
            
            
            
            bagIx = BagWorn;
            slotIx = (byte)Math.Clamp(item.Slot < 0 ? 0 : (int)item.Slot, 0, EquipSlotMax);
        }
        else if (item.StorageType == StorageBank)
        {
            // Bank: its own bag id, slots run up to the largest bank.
            
            
            
            
            bagIx = BagBank;
            slotIx = (byte)Math.Clamp(item.Slot < 0 ? 0 : (int)item.Slot, 0, BankExpansion.MaxBankSlots - 1);
        }
        else if (item.StorageType == StorageEquippedBag)
        {
            // Worn bag: shown in the character bag window, slots 70 to 72 of the main bag id.
            
            
            
            
            bagIx = BagMain;
            slotIx = (byte)(CharacterBagFirstSlot + Math.Clamp(item.Slot < 0 ? 0 : (int)item.Slot, 0, CharacterBagSlotCount - 1));
        }
        else
        {
            
            var flat = item.Slot < 0 ? 0 : (int)item.Slot;
            if (flat < MainBagSlotCount) { bagIx = BagMain; slotIx = (byte)(MainBagFirstSlot + flat); }
            else { var i = flat - MainBagSlotCount; bagIx = (byte)Math.Clamp(BagFirstExtra + i / ExtraBagSlotCount, BagFirstExtra, BagLastExtra); slotIx = (byte)(i % ExtraBagSlotCount); }
        }
        writer.WriteByte(bagIx);  // +0x0F bag
        writer.WriteByte(slotIx);  // +0x10 slot
        
        
        
        
        
        writer.WriteUInt16(item.EnchantReinforceId);  // +0x11 timed enchant id
        writer.WriteUInt16(item.EnchantSeconds);  // +0x13 timed enchant seconds left
        writer.WriteZeros(2);  // +0x15 not modelled
        
        writer.WriteUInt32(item.DyeColor);  // +0x17 dye colour of slot 0
        writer.WriteByte(RecordBindFlags(item));  // +0x1B bind flags
        writer.WriteByte(0);  // +0x1C zero
        writer.WriteByte(item.Refine);  // +0x1D refine level
        writer.WriteZeros(3);  // +0x1E not modelled
        WriteRuneholes(writer, item);  // +0x21 rune holes, 5 x 7 bytes
        
        
        
        writer.WriteByte(item.Element);  // +0x44 bestowed element
        
        
        
        
        
        writer.WriteUInt32(RecordAbilityValue(item));  // +0x45 ability value
        writer.WriteUInt16(0);  // +0x49 card set id
        writer.WriteZeros(GrindingStatsLength);  // +0x4B grinding stats
        writer.WriteByte(0);  // +0x5F accessory trait
        writer.WriteByte(0);  // +0x60 accessory trait sub
        writer.WriteSingle(0f);  // +0x61 accessory value
        
        
        
        
        writer.WriteUInt32(item.DyeColor2);  // +0x65 dye colour of slot 1
        writer.WriteUInt32(item.DyeColor3);  // +0x69 dye colour of slot 2
        writer.WriteUInt32(0);  // +0x6D outfit look template, not modelled
        
        
        writer.WriteUInt32(item.FxEnchantIndex);  // +0x71 FX enchant index

        
        var written = writer.Length - startPos;
        if (written < ItemPackets.ItemRecordLength)
            writer.WriteZeros(ItemPackets.ItemRecordLength - written);
    }

 /// <summary>Bind flag bit of an item record, set when the item is bound to its owner.</summary>
    internal const byte RecordBoundFlag = 0x02;

 /// <summary>
 /// The bind flags of an item record: the bound flag when the item is bound by its template or by having been worn; otherwise 0.
 /// </summary>
 /// <remarks>
 /// The flags are read from the template's bind type and the item's storage location and history.
 /// The template comes from the loaded game data. Without loaded game data the value stays 0.
 /// </remarks>
    internal static byte RecordBindFlags(RO2.Inventory.Domain.InventoryItem item)
        => GameDataStore.Loaded?.GetItem(item.ItemId) is { } template
            ? RecordBindFlags(template.Bind_Type, item.StorageType, item.Bound)
            : (byte)0;

 /// <summary>
 /// The bind flags of an item record, given its bind type, storage type and whether it was ever worn.
 /// </summary>
 /// <param name="bindType">Bind type of the item template.</param>
 /// <param name="storageType">Storage location of the item: 1 for worn, 3 for an equipped bag.</param>
 /// <param name="everWorn">Whether the item was worn at some point.</param>
    internal static byte RecordBindFlags(byte bindType, byte storageType, bool everWorn = false)
        => RO2.Inventory.Domain.ItemTradeRules.IsBound(bindType, storageType, everWorn) ? RecordBoundFlag : (byte)0;

 /// <summary>
 /// The ability value of an item record: the item's own value when it has one, otherwise the template's value.
 /// </summary>
 /// <remarks>
 /// Items bought from an NPC stack store 0 for their own value, which takes precedence over the template value.
 /// Returns 0 when no loaded game data knows the template.
 /// </remarks>
    internal static uint RecordAbilityValue(RO2.Inventory.Domain.InventoryItem item)
        => RecordAbilityValue(item.ItemId, item.AbilityValue);

 /// <summary>
 /// The ability value of an item record for a template and an optional instance value.
 /// </summary>
 /// <param name="templateId">Item template id.</param>
 /// <param name="instanceValue">The item's own value, or null to use the template's value.</param>
    internal static uint RecordAbilityValue(uint templateId, uint? instanceValue)
        => instanceValue ?? (GameDataStore.Loaded?.GetItem(templateId) is { } template ? (uint)Math.Max(0, template.Abili_val) : 0u);

 /// <summary>
 /// Durability of an item record whose durability is unset: the template's durability, or the default maximum when the template is unknown.
 /// </summary>
    private static ushort UnsetRecordDurability(uint templateId)
        => GameDataStore.Loaded?.GetItem(templateId) is { } template
            ? (ushort)Math.Min(template.Durability, ushort.MaxValue)
            : ItemPackets.DurabilityMax;

 /// <summary>
 /// Writes the 35-byte runehole block of an item record from the item's opened holes.
 /// </summary>
 /// <remarks>
 /// Every item record carries this block, so later records of an item keep its holes.
 /// Before the game data is loaded the block stays zero.
 /// </remarks>
    private static void WriteRuneholes(PacketWriter writer, RO2.Inventory.Domain.InventoryItem item)
    {
        const int blockLength = ItemWorkbench.HoleCount * 7;
        var store = GameDataStore.Loaded;
        if (store is null || string.IsNullOrWhiteSpace(item.CardsJson))
        {
            writer.WriteZeros(blockLength);
            return;
        }
        var record = new byte[ItemPackets.ItemRecordLength];
        ItemWorkbench.ApplyRuneholes(record, item, store);
        writer.WriteBytes(record.AsSpan(ItemWorkbench.RuneholeOffset, blockLength));
    }

}
