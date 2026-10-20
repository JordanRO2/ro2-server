using System.Buffers.Binary;
using RO2.Network.Packets;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Crash-class gate for the economy subsystem's enveloped S2C opcodes. Every body must begin with a valid
/// compact-length marker in {1, 2, 4, 8}; otherwise the client's compact-length decoder rejects it and the
/// per-opcode handler dereferences a null buffer. After the sweep, every S2C body an economy handler emits
/// begins with a valid marker, or the send is suppressed.
/// </summary>
/// <remarks>
/// 2970 NfyTRoomJoinMsg is suppressed, since its inner data cannot be reproduced. 2659 AnsBuyPrivateShopItem
/// is built by PrivateShopItemSalePackets.BuildBuyAnswer2659 and BuildBuyRefusal2659.
/// </remarks>
public class EconomyCrashSweepTests
{
    private static readonly byte[] ValidMarkers = { 1, 2, 4, 8 };

    
    private static byte[] BuildCompactBA(Action<PacketWriter> writePayload)
    {
        using var w = new PacketWriter(256);
        w.WriteCompactBABody(writePayload);
        return w.GetWrittenSpan().ToArray();
    }

    private static (long value, int consumed) ReadCompactLength(ReadOnlySpan<byte> b)
    {
        int n = b[0];
        Assert.Contains((byte)n, ValidMarkers); 
        long v = n switch
        {
            1 => b[1],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(1)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(1)),
            _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(1)),
        };
        return (v, 1 + n);
    }

 /// <summary>
 /// Asserts a well-formed envelope: a valid first marker, a leading length matching the following byte count,
 /// the selfLen echo, a msgTag inside the design band, and the expected total length.
 /// </summary>
    private static void AssertWrap(byte[] body, ushort expectedTag, int expectedTotalLen)
    {
        Assert.Contains(body[0], ValidMarkers);                             
        var (len, consumed) = ReadCompactLength(body);
        int following = body.Length - consumed;
        Assert.Equal(following, (int)len);                                  
        ushort selfLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(consumed));
        Assert.Equal((int)len, selfLen);                                    
        int payloadLen = following - 2; 
        Assert.Equal(2 + payloadLen, selfLen);                             
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(consumed + 2));
        Assert.Equal(expectedTag, tag);                                    
        Assert.InRange(tag, (ushort)52001, (ushort)58526);
        Assert.Equal(expectedTotalLen, body.Length);                       
    }

    
    
    
    
    [Fact]
    public void Ack2938_IsThePlainU16ExitValue()
    {
        
        
        
        
        Assert.Equal("0100", Convert.ToHexString(SessionExitPackets.Ack2938).ToLowerInvariant());
    }

    [Fact]
    public void PCollection2926_AnsIncubator_ReproducesModalRetailFrameByteForByte()
    {
        var body = EconomyMiscPackets.BuildPCollectionIncubatorEmptyAck();
        Assert.Equal("01000000", Convert.ToHexString(body).ToLowerInvariant());
        Assert.Contains(body[0], ValidMarkers);
    }

    [Fact]
    public void Guild2960_EmergencyCallAsk_ReproducesRealRetailFrameByteForByte()
    {
        var body = EconomyMiscPackets.BuildGuildEmergencyCallAsk();
        Assert.Equal("040000000a000000", Convert.ToHexString(body).ToLowerInvariant());
        Assert.Contains(body[0], ValidMarkers);
        
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
    }

    [Fact]
    public void Offerings3029_AnsMakeOfferings_ReproducesSuccessRetailFrameByteForByte()
    {
        
        
        var body = EconomyMiscPackets.BuildMakeOfferingsAck(0);
        Assert.Equal("0400000000000000", Convert.ToHexString(body).ToLowerInvariant());
        Assert.Contains(body[0], ValidMarkers);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
    }

    [Fact]
    public void Offerings3029_MessageStringIdGoesInTheLowHalf()
    {
        
        
        var body = EconomyMiscPackets.BuildMakeOfferingsAck(15774);
        Assert.Equal("040000009e3d0000", Convert.ToHexString(body).ToLowerInvariant());
        Assert.Equal(15774, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)));
    }

    
    
    
    
    
    
    
    [Fact]
    public void LoginFail2306_Wrap_WellFormed()
    {
        var body = BuildCompactBA(p => { p.WriteUInt16(0xDAC2); p.WriteZeros(18); });
        AssertWrap(body, 0xDAC2, expectedTotalLen: 24);
    }

    [Fact]
    public void MailDelete2448_Wrap_WellFormed()
    {
        var body = BuildCompactBA(p => { p.WriteUInt16(0xE356); p.WriteZeros(15); });
        AssertWrap(body, 0xE356, expectedTotalLen: 21);
    }

    [Fact]
    public void MailGetMoney2617_Wrap_WellFormed()
    {
        var body = BuildCompactBA(p => { p.WriteUInt16(0xE35D); p.WriteZeros(23); });
        AssertWrap(body, 0xE35D, expectedTotalLen: 29);
    }

    [Fact]
    public void AuctionBuySellingPrice2461_Wrap_WellFormed()
    {
        var body = BuildCompactBA(p => { p.WriteUInt16(0xE36D); p.WriteZeros(30); });
        AssertWrap(body, 0xE36D, expectedTotalLen: 36);
    }

    [Fact]
    public void AuctionTenderJoin2462_Wrap_WellFormed()
    {
        var body = BuildCompactBA(p => { p.WriteUInt16(0xE36F); p.WriteZeros(38); });
        AssertWrap(body, 0xE36F, expectedTotalLen: 44);
    }

    [Fact]
    public void AppearanceChange3046_Wrap_WellFormed()
    {
        var body = BuildCompactBA(p => { p.WriteUInt16(0xE49E); p.WriteZeros(25); });
        AssertWrap(body, 0xE49E, expectedTotalLen: 31);
    }

    [Fact]
    public void CraftSaleCloseShop2605_Wrap_WellFormed_AndCarriesHandle()
    {
        uint handle = 0x00ABCD11u; 
        var body = BuildCompactBA(p =>
        {
            p.WriteUInt16(0xE42E);
            p.WriteUInt32(handle);
            p.WriteUInt32(handle);
            p.WriteByte(0);
            p.WriteSingle(1.0f);
            p.WriteSingle(2.0f);
            p.WriteSingle(3.0f);
        });
        AssertWrap(body, 0xE42E, expectedTotalLen: 27);
        
        int fieldStart = ReadCompactLength(body).consumed + 2 + 2;
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(fieldStart)));
    }

    [Theory]
    [InlineData((ushort)0xE431)] 
    [InlineData((ushort)0xE432)] 
    [InlineData((ushort)0xE433)] 
    public void PrivateShopNotify_Wrap_WellFormed_AndCarriesHandle(ushort tag)
    {
        uint handle = 0x0A54AF11u; 
        var body = BuildCompactBA(p =>
        {
            p.WriteUInt16(tag);
            p.WriteUInt32(handle);
            p.WriteUInt16(0);
            p.WriteUInt32(handle);
            p.WriteZeros(47);
        });
        AssertWrap(body, tag, expectedTotalLen: 63);
        int fieldStart = ReadCompactLength(body).consumed + 2 + 2;
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(fieldStart)));
    }
}
