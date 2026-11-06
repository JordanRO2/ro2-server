using System.Buffers.Binary;
using RO2.Inventory.Domain;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The outfit look item id is written at item record +0x6D and in the 28-byte worn record of 2315.</summary>
public class OutfitLookRecordTests
{
    private const uint LookItemId = 16700123;

    private static InventoryItem Item(uint look) =>
        new() { Id = 1, ItemId = 16000001, Count = 1, StorageType = 1, Slot = 3, OutfitLookItemId = look };

    private static byte[] Record(InventoryItem item)
    {
        using var w = new PacketWriter();
        ItemPackets.WriteItemInstance(w, item);
        return w.ToArray();
    }

    [Fact]
    public void ItemRecord_CarriesTheOutfitLookAt0x6D()
    {
        Assert.Equal(LookItemId, BinaryPrimitives.ReadUInt32LittleEndian(Record(Item(LookItemId)).AsSpan(0x6D)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(Record(Item(0)).AsSpan(0x6D)));
    }

    [Fact]
    public void WornRecord28_CarriesTheOutfitLook()
    {
        using var w = new PacketWriter();
        EquipViewNotifyPackets.WriteEquipRecord28(w, Item(LookItemId));
        // slot(1) id(4) dye0(4) element(1) refine(1) dye1(4) dye2(4) = 19, then the outfit look
        Assert.Equal(LookItemId, BinaryPrimitives.ReadUInt32LittleEndian(w.ToArray().AsSpan(19)));
    }

    [Fact]
    public void CopyInstanceStateFrom_CopiesTheOutfitLook()
    {
        Assert.Equal(LookItemId, new InventoryItem().CopyInstanceStateFrom(Item(LookItemId)).OutfitLookItemId);
    }
}
