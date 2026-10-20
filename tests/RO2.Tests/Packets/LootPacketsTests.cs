using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout tests for the loot and item-use notify family: RMI 2333 OnPickupItem and RMI 2330 OnUseItemNotify.
/// </summary>
/// <remarks>
/// Vectors are reference frame bodies. 2330 is an enveloped body with a compact-length first byte (0x01), so the {1,2,4,8} marker rule applies.
/// 2333 is bare: a flat 130-byte <c>ReadBytesAligned</c> with no envelope, so its first byte is 0x00 and the marker rule must not be applied.
/// </remarks>
public class LootPacketsTests
{
    
    [Fact]
    public void PickupItem2333_ReproducesARealRetailFrame()
    {
        
        var expected = Convert.FromHexString(
            "00000000e211840da83d040e634b5d050000010134000000000000000000000000000000000000000000000000" +
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000");
        Assert.Equal(LootNotifyPackets.PickupItemBodyLen, expected.Length);

        var record = expected.AsSpan(4).ToArray(); 
        var body = LootNotifyPackets.BuildPickupItem2333(record);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void PickupItem2333_IsBareWithResultAndKindHeader()
    {
        var record = new byte[LootNotifyPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0), 0x0E043DA80D8411E2UL); 
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), 90000227u);            
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 0);                   

        var body = LootNotifyPackets.BuildPickupItem2333(record, resultCode: 0, pickupKind: 0);

        
        
        Assert.Equal(130, body.Length);
        Assert.Equal(0x00, body[0]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0)));  
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)));  
        Assert.Equal(0x0E043DA80D8411E2UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(4)));  
        Assert.Equal(90000227u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));            
    }

    [Fact]
    public void PickupItem2333_ResultCodeAbortsWhenNonZero()
    {
        
        
        var record = new byte[LootNotifyPackets.ItemRecordLength];
        var body = LootNotifyPackets.BuildPickupItem2333(record, resultCode: 0x4E34, pickupKind: 5);
        Assert.Equal(0x4E34, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0)));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)));
    }

    [Fact]
    public void PickupItem2333_RejectsWrongRecordLength()
    {
        Assert.Throws<ArgumentException>(() =>
            LootNotifyPackets.BuildPickupItem2333(new byte[125]));
        Assert.Throws<ArgumentException>(() =>
            LootNotifyPackets.BuildPickupItem2333(new byte[127]));
    }

    
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

    [Fact]
    public void UseItemNotify2330_ReproducesARealRetailFrame()
    {
        
        var expected = Convert.FromHexString("01141400a7e20000000011a8e1066a38420111a8e106");
        var body = LootNotifyPackets.BuildUseItemNotify2330(
            actorHandle: 0x06E1A811u, itemTemplateId: 21117034u);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void UseItemNotify2330_IsAWellFormedEnvelope()
    {
        var body = LootNotifyPackets.BuildUseItemNotify2330(0x06E1A811u, 21117034u);

        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(LootNotifyPackets.UseItemNotifyBodyLen, body.Length);
        var blob = Blob(body);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(LootNotifyPackets.MsgTagUseItemNotify, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    [Fact]
    public void UseItemNotify2330_ActorHandleAppearsAtBlob8And16AndIsPc()
    {
        var body = LootNotifyPackets.BuildUseItemNotify2330(0x06E1A811u, 21117034u);
        var blob = Blob(body);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));          
        Assert.Equal(0x06E1A811u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8))); 
        Assert.Equal(21117034u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)));  
        Assert.Equal(0x06E1A811u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16)));

        
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)) & 0xFFu);
    }
}
