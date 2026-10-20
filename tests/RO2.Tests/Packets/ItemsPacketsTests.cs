using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte layout tests for the item family: RMI 2503 OnUpdateItemDurability and RMI 2326 OnItemInfo.</summary>
/// <remarks>
/// Every vector is a reference body. Both opcodes are enveloped, and a malformed envelope crashes the remote client, so each builder is checked against the first-body-byte rule as well as byte-exact reproduction.
/// </remarks>
public class ItemsPacketsTests
{
 /// <summary>
 /// The blob is everything after the compact marker and its length bytes. The marker byte is the number of length bytes that follow it: 0x01 skips 2, 0x02 skips 3.
 /// </summary>
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

 /// <summary>
 /// The first body byte must be a legal compact marker, and the declared selfLen must equal the blob length. When the client's decoder rejects a body, the per-opcode stub passes NULL to a consumer that dereferences it.
 /// </summary>
    private static void AssertWellFormed(byte[] body, int len, ushort tag)
    {
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(len, body.Length);
        var blob = Blob(body);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(tag, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    
 /// <summary>
 /// Reference 2503 body, byte for byte. This is the armour case: the uid resolves through the 2326/2541 item map to ItemInfo id 16702317, Equip_Slot 3.
 /// </summary>
    [Fact]
    public void ItemDurability2503_ReproducesARealRetailFrame_Armour()
    {
        var expected = Convert.FromHexString("01121200cde3000000004d0b2504a83d040e7c15");
        var body = ItemPackets.BuildItemDurability2503(
            itemUid: 1010000008271170381UL, durability: 5500);
        Assert.Equal(expected, body);
    }

 /// <summary>
 /// Second reference body, the weapon case: uid resolves to ItemInfo id 16702297, Equip_Slot 6. Two independent vectors keep the test from passing on a single lucky body.
 /// </summary>
    [Fact]
    public void ItemDurability2503_ReproducesARealRetailFrame_Weapon()
    {
        var expected = Convert.FromHexString("01121200cde300000000cc615e06a83d040ed824");
        var body = ItemPackets.BuildItemDurability2503(
            itemUid: 1010000008308482508UL, durability: 9432);
        Assert.Equal(expected, body);
    }

 /// <summary>
 /// The envelope and the four fixed leading fields, which are constant across the reference population.
 /// </summary>
    [Fact]
    public void ItemDurability2503_IsEnvelopedAndFieldsSitWhereTheConsumerReadsThem()
    {
        var body = ItemPackets.BuildItemDurability2503(1010000008271170381UL, 5500);
        AssertWellFormed(body, ItemPackets.UpdateItemDurabilityBodyLen, ItemPackets.MsgTagUpdateItemDurability);

        var blob = Blob(body);
        
        
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        
        Assert.Equal(1010000008271170381UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8)));
        
        Assert.Equal((ushort)5500, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(16)));
    }

 /// <summary>
 /// The uid band: the uid divided by 10^16 is 101 for the vast majority of items. This asserts the value the caller passes rather than the encoding, and documents what a valid server-side item uid looks like.
 /// </summary>
    [Fact]
    public void ItemDurability2503_UidKeepsItsBandThroughTheEncoder()
    {
        const ulong uid = 1010000008271170381UL;
        Assert.Equal(101UL, uid / 10_000_000_000_000_000UL);

        var body = ItemPackets.BuildItemDurability2503(uid, 5500);
        var roundTrip = BinaryPrimitives.ReadUInt64LittleEndian(Blob(body).Slice(8));
        Assert.Equal(uid, roundTrip);
        Assert.Equal(101UL, roundTrip / 10_000_000_000_000_000UL);
    }

 /// <summary>The full-durability constant and the observed durability ceiling.</summary>
    [Fact]
    public void ItemDurability2503_FullDurabilityMatchesTheItemTableCeiling()
    {
        Assert.Equal((ushort)10000, ItemPackets.DurabilityMax);
        var body = ItemPackets.BuildItemDurability2503(1010000008271170381UL, ItemPackets.DurabilityMax);
        Assert.Equal((ushort)10000, BinaryPrimitives.ReadUInt16LittleEndian(Blob(body).Slice(16)));
        Assert.Equal(1, body[0]);
    }

 /// <summary>
 /// The compact marker stays 0x01 whatever the payload values are, because the body length is fixed at 20. A widened marker would crash the client.
 /// </summary>
    [Theory]
    [InlineData(0UL, 0)]
    [InlineData(ulong.MaxValue, 65535)]
    [InlineData(1010000008308482508UL, 1)]
    public void ItemDurability2503_FirstBodyByteIsAlwaysALegalCompactMarker(ulong uid, int durability)
    {
        var body = ItemPackets.BuildItemDurability2503(uid, (ushort)durability);
        Assert.Equal(1, body[0]);
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        AssertWellFormed(body, ItemPackets.UpdateItemDurabilityBodyLen, ItemPackets.MsgTagUpdateItemDurability);
    }

    
 /// <summary>
 /// Reference 2326 item record (126 bytes): ItemInfo id 16702290, uid 1010000008400789751, durability 10000.
 /// </summary>
    private const string RetailItemRecordHex =
        "f7e0de0ba83d040e52dbfe00102701051300000000000000000000000000000000" +
        "06000025a9530106000025a953010000000000000000000000000000000000000000000001030200620010005003" +
        "903ebd036b1be6ff903e3dfd6b1b6ffd00000000000000000000000000000000000000000000000000000000000000";

 /// <summary>
 /// Reference 2326 body, byte for byte. This exact body is the client's canonical form, so the match is not a coincidence of a repeated template.
 /// </summary>
    [Fact]
    public void ItemInfo2326_ReproducesARealRetailFrame()
    {
        var expected = Convert.FromHexString(
            "0288008800d0e2000000000000" + RetailItemRecordHex);
        var record = Convert.FromHexString(RetailItemRecordHex);
        var body = ItemPackets.BuildItemInfo2326(record);
        Assert.Equal(expected, body);
    }

 /// <summary>
 /// 2326 uses the two-byte compact marker (0x02), unlike 2503. Marker 0x01 followed by a two-byte length is an invalid envelope.
 /// </summary>
    [Fact]
    public void ItemInfo2326_UsesTheTwoByteCompactMarkerAndA136ByteBlob()
    {
        var body = ItemPackets.BuildItemInfo2326(Convert.FromHexString(RetailItemRecordHex));
        Assert.Equal(2, body[0]);
        AssertWellFormed(body, ItemPackets.ItemInfoBodyLen, ItemPackets.MsgTagItemInfo);
        Assert.Equal(136, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)));
        Assert.Equal(136, Blob(body).Length);
    }

 /// <summary>
 /// The item record starts at the fixed position the client reads it from; one byte of drift shifts every field of the item.
 /// </summary>
    [Fact]
    public void ItemInfo2326_RecordStartsAtBlobPlusTen()
    {
        var record = Convert.FromHexString(RetailItemRecordHex);
        var body = ItemPackets.BuildItemInfo2326(record);
        var blob = Blob(body);

        Assert.Equal(record, blob.Slice(10).ToArray());
        Assert.Equal(1010000008400789751UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(10)));
        Assert.Equal(16702290u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(18)));
        Assert.Equal((ushort)10000, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(22)));
    }

 /// <summary>
 /// The result code: a non-zero value makes the client show an error and return before parsing the record. The reference body carries 0.
 /// </summary>
    [Fact]
    public void ItemInfo2326_ResultCodeDefaultsToZeroAndLandsAtBlobPlusEight()
    {
        var record = Convert.FromHexString(RetailItemRecordHex);

        var ok = ItemPackets.BuildItemInfo2326(record);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(Blob(ok).Slice(8)));

        var refused = ItemPackets.BuildItemInfo2326(record, resultCode: 20152);
        Assert.Equal((ushort)20152, BinaryPrimitives.ReadUInt16LittleEndian(Blob(refused).Slice(8)));
        
        AssertWellFormed(refused, ItemPackets.ItemInfoBodyLen, ItemPackets.MsgTagItemInfo);
    }

 /// <summary>
 /// A record of the wrong length would change the envelope length and make the client read past the blob, so the builder rejects it.
 /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(125)]
    [InlineData(127)]
    public void ItemInfo2326_RejectsARecordThatIsNot126Bytes(int length)
    {
        Assert.Throws<ArgumentException>(() => ItemPackets.BuildItemInfo2326(new byte[length]));
    }

 /// <summary>
 /// The zero-filling convenience overload still produces a legal envelope and writes the three populated fields where the client reads them.
 /// </summary>
    [Fact]
    public void ItemInfo2326_FieldOverloadProducesALegalBody()
    {
        var body = ItemPackets.BuildItemInfo2326(
            itemUid: 1010000008400789751UL, itemId: 16702290u, durability: 10000);
        AssertWellFormed(body, ItemPackets.ItemInfoBodyLen, ItemPackets.MsgTagItemInfo);

        var blob = Blob(body);
        Assert.Equal(1010000008400789751UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(10)));
        Assert.Equal(16702290u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(18)));
        Assert.Equal((ushort)10000, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(22)));
    }

    
 /// <summary>
 /// Neither item message carries an actor handle, which is why 2503 must be unicast to the item's owner rather than broadcast. The test pins that the slot a broadcast would use for a handle is a constant zero.
 /// </summary>
    [Fact]
    public void ItemMessages_CarryNoActorHandle()
    {
        var durability = ItemPackets.BuildItemDurability2503(1010000008271170381UL, 5500);
        var handleSlot = BinaryPrimitives.ReadUInt32LittleEndian(Blob(durability).Slice(4));
        Assert.Equal(0u, handleSlot);
        
        
        Assert.NotEqual(0x11u, handleSlot & 0xFFu);
        Assert.NotEqual(0x13u, handleSlot & 0xFFu);

        var info = ItemPackets.BuildItemInfo2326(Convert.FromHexString(RetailItemRecordHex));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(Blob(info).Slice(4)));
    }
}
