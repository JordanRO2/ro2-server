using System.Buffers.Binary;
using RO2.Network.Packets;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout tests for the auction and mail S2C compact-length ByteArray family (RMI 2445, 2447, 2449, 2454, 2455,
/// 2456, 2458, 2460). The body is [compactLen(L)][u16 selfLen=L][u16 tag 0xE35x][fields...], where L is the length
/// from selfLen to the end of the body. Bodies are built with <see cref="PacketWriter.WriteCompactBABody"/>.
/// The empty cases of 2454, 2455 and 2456 are asserted byte for byte; the other cases check framing only.
/// </summary>
public class AuctionMailCompactBATests
{
    
    private static byte[] Build(Action<PacketWriter> writePayload)
    {
        using var w = new PacketWriter(256);
        w.WriteCompactBABody(writePayload);
        return w.GetWrittenSpan().ToArray();
    }

 /// Decode a Proud compact length: returns (value, bytesConsumed).
    private static (long value, int consumed) ReadCompactLength(ReadOnlySpan<byte> b)
    {
        int n = b[0];
        Assert.Contains(n, new[] { 1, 2, 4, 8 }); 
        long v = n switch
        {
            1 => b[1],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(1)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(1)),
            _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(1)),
        };
        return (v, 1 + n);
    }

 /// Asserts (a) leading compact-len value == number of following bytes, (b) that value == u16
 /// selfLen, (c) the u16 tag right after selfLen matches. Returns the whole body for extra checks.
    private static void AssertWellFormed(byte[] body, ushort expectedTag)
    {
        var (len, consumed) = ReadCompactLength(body);
        int following = body.Length - consumed;
        Assert.Equal(following, (int)len);                              
        ushort selfLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(consumed));
        Assert.Equal((int)len, selfLen);                               
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(consumed + 2));
        Assert.Equal(expectedTag, tag);                                
    }

    
    [Fact]
    public void Auction2454_Open_ByteExact()
    {
        uint auctionId = 1;
        var body = Build(p =>
        {
            p.WriteUInt16(0xE35F);
            p.WriteUInt32(auctionId);
            p.WriteUInt16(0);
        });
        AssertWellFormed(body, 0xE35F);
        Assert.Equal("010a0a005fe3010000000000", Convert.ToHexString(body).ToLowerInvariant());
    }

    
    [Fact]
    public void Auction2455_SellList_ByteExact()
    {
        ushort txn = 0;
        var body = Build(p =>
        {
            p.WriteUInt16(0xE363);
            p.WriteByte(0x11);
            p.WriteUInt16(txn);
            p.WriteByte(0x07);
            p.WriteByte(0x01);
            p.WriteByte(0x00);
            p.WriteUInt16(0);
            p.WriteUInt16(0);
        });
        AssertWellFormed(body, 0xE363);
        Assert.Equal("010e0e0063e311000007010000000000", Convert.ToHexString(body).ToLowerInvariant());
    }

    
    [Fact]
    public void Auction2456_Search_ByteExact()
    {
        ushort txn = 0, page = 0;
        var body = Build(p =>
        {
            p.WriteUInt16(0xE361);
            p.WriteByte(0x11);
            p.WriteUInt16(txn);
            p.WriteByte(0x07);
            p.WriteUInt16(page);
            p.WriteUInt16(0x000C);
            p.WriteUInt16(0);
            p.WriteUInt16(0);
            p.WriteUInt16(0);
        });
        AssertWellFormed(body, 0xE361);
        Assert.Equal("0112120061e31100000700000c00000000000000", Convert.ToHexString(body).ToLowerInvariant());
    }
}
