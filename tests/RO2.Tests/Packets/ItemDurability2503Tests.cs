using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact gates for RMI 2503 OnUpdateItemDurability as the durability system drives it. The builder is shared with <see cref="ItemsPacketsTests"/>. These tests pin the first body byte (a legal compact marker) and byte-for-byte reproduction of a reference body, so the weapon-wear path cannot produce a body that crashes the client.
/// </summary>
/// <remarks>
/// For this family a malformed envelope crashes the remote client. The compact decoder accepts only marker bytes 1, 2, 4 and 8; on rejection the per-opcode stub passes the consumer a NULL blob.
/// </remarks>
public class ItemDurability2503Tests
{
 /// <summary>The blob is everything after the compact marker and its length bytes; 0x01 skips 2.</summary>
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

    private static void AssertWellFormed(byte[] body)
    {
        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(ItemPackets.UpdateItemDurabilityBodyLen, body.Length);
        var blob = Blob(body);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(ItemPackets.MsgTagUpdateItemDurability,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

 /// <summary>
 /// Weapon case: a body whose uid resolves through the 2326/2541 item map to ItemInfo id 16702297, Equip_Slot 6, the slot <see cref="DurabilityService"/> wears.
 /// </summary>
    [Fact]
    public void BuildItemDurability2503_MatchesARealRetailWeaponFrame()
    {
        var expected = Convert.FromHexString("01121200cde300000000cc615e06a83d040ed824");
        var body = ItemPackets.BuildItemDurability2503(
            itemUid: 1010000008308482508UL, durability: 9432);
        Assert.Equal(expected, body);
        Assert.Equal(1, body[0]);
        AssertWellFormed(body);
    }

 /// <summary>A second independent reference body, so the match cannot pass on a single lucky vector.</summary>
    [Fact]
    public void BuildItemDurability2503_MatchesASecondRealRetailFrame()
    {
        var expected = Convert.FromHexString("01121200cde3000000004d0b2504a83d040e7c15");
        var body = ItemPackets.BuildItemDurability2503(
            itemUid: 1010000008271170381UL, durability: 5500);
        Assert.Equal(expected, body);
        AssertWellFormed(body);
    }

 /// <summary>
 /// The uid (u64) and durability (u16) fields as the handler reads them. The u32 field the handler never reads is left zero.
 /// </summary>
    [Fact]
    public void BuildItemDurability2503_PlacesUidAndDurabilityWhereTheConsumerReadsThem()
    {
        const ulong uid = 1010000008308482508UL;
        const ushort dur = 9431; 
        var body = ItemPackets.BuildItemDurability2503(uid, dur);
        AssertWellFormed(body);

        var blob = Blob(body);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal(uid, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8)));
        Assert.Equal(dur, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(16)));
    }

 /// <summary>
 /// The body length is fixed at 20, so the compact marker can never widen past 0x01 whatever the
 /// values are. A regression that widened it would crash the client.
 /// </summary>
    [Theory]
    [InlineData(0UL, 0)]
    [InlineData(1010000008308482508UL, 9432)]
    [InlineData(ulong.MaxValue, ushort.MaxValue)]
    public void BuildItemDurability2503_FirstBodyByteIsAlwaysALegalCompactMarker(ulong uid, int durability)
    {
        var body = ItemPackets.BuildItemDurability2503(uid, (ushort)durability);
        Assert.Equal(1, body[0]);
        AssertWellFormed(body);
    }

 /// <summary>
 /// The durability lost per wear step is one point, the value the service applies to weapons. Guards the constant the wiring depends on rather than re-testing the encoder.
 /// </summary>
    [Fact]
    public void DurabilityService_WearsTheWeaponByExactlyOnePoint()
    {
        Assert.Equal(1, DurabilityService.WeaponWearPerHit);
        Assert.Equal((ushort)6, DurabilityService.WeaponMainHandSlot);
        Assert.Equal((ushort)10000, DurabilityService.DefaultMaxDurability);
    }
}
