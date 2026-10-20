using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout tests for the quick-slot (skill bar) acknowledgements.
/// </summary>
/// <remarks>
/// Regression gate for a client crash: placing a skill on the bar crashed the client because the 2473 reply lacked the compact-length envelope, and the consumer dereferences the blob without a null check.
/// </remarks>
public class QuickSlotPacketsTests
{
    
    private const string Retail2473 = "010a0a00e6e3119734090000";
    private const string Retail2474 = "010a0a00e4e311a3f7060000";
    private const string Retail2475 = "010a0a00e2e3115f70070000";

    
    private const string Retail1319 = "011c1c00e5e311014b0600070200296c0600050000000000000000000000";

    [Theory]
    [InlineData(Retail2473, QuickSlotPackets.MsgTagAddQuickSlot, 0x09349711u)]
    [InlineData(Retail2474, QuickSlotPackets.MsgTagDelQuickSlot, 0x06F7A311u)]
    [InlineData(Retail2475, QuickSlotPackets.MsgTagSwapQuickSlot, 0x07705F11u)]
    public void ReproducesTheRealRetailAckByteForByte(string hex, ushort tag, uint handle)
    {
        var expected = Convert.FromHexString(hex);
        Assert.Equal(expected, QuickSlotPackets.BuildAck(tag, handle));
    }

    [Fact]
    public void AckCarriesTheEnvelopeTheClientRequires()
    {
        var body = QuickSlotPackets.BuildAck(QuickSlotPackets.MsgTagAddQuickSlot, 0x00100011u);

        
        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(QuickSlotPackets.AckBodyLen, body.Length);

        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(blob.Length, body[1]);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(QuickSlotPackets.MsgTagAddQuickSlot,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
        Assert.Equal(0x00100011u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(8)));
    }

    [Fact]
    public void AckEchoesNoSlotTypeOrId()
    {
        
        
        var body = QuickSlotPackets.BuildAck(QuickSlotPackets.MsgTagAddQuickSlot, 0x00100011u);
        Assert.Equal(12, body.Length);
    }

    [Fact]
    public void RequestHandleIsAtBlobPlus4_NotWhereASkip4Lands()
    {
        
        
        var req = Convert.FromHexString(Retail1319);
        Assert.True(QuickSlotPackets.TryReadActorHandle(req, out var handle));
        Assert.Equal(0x064B0111u, handle);
        Assert.Equal(0x11, (byte)(handle & 0xFF));   

        
        var whatSkip4Read = BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(4));
        Assert.Equal(58341, whatSkip4Read);          
    }

    [Fact]
    public void RequestParsingToleratesTrailingTransportBytes()
    {
        var req = Convert.FromHexString(Retail1319).Concat(new byte[] { 0x4E, 0x04, 0x65, 0xA6 })
            .ToArray();
        Assert.True(QuickSlotPackets.TryReadActorHandle(req, out var handle));
        Assert.Equal(0x064B0111u, handle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("011c1c00")]         
    public void MalformedRequestsAreRejected(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.False(QuickSlotPackets.TryReadActorHandle(bytes, out _));
    }

    [Fact]
    public void RequestExposesTheBarPositionAndItsContents()
    {
        
        
        
        var req = Convert.FromHexString(Retail1319);
        Assert.True(QuickSlotPackets.TryReadRequest(req, out var handle, out var q));

        Assert.Equal(0x064B0111u, handle);
        Assert.Equal(0, q.Page);
        Assert.Equal(7, q.Slot);
        Assert.Equal(2, q.EntryType);
        Assert.Equal(420905u, q.EntryId);   
        Assert.Equal(5, q.Amount);
        Assert.Equal(0UL, q.ItemUid);   
    }

    [Fact]
    public void ShortRequestsStillYieldTheHandle()
    {
        
        
        var req = Convert.FromHexString(Retail1319).AsSpan(0, 14).ToArray();
        Assert.True(QuickSlotPackets.TryReadRequest(req, out var handle, out var q));
        Assert.Equal(0x064B0111u, handle);
        Assert.Equal(default, q);
    }
}
