using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout test for RMI 2505 OnPC_Out and the 1418 character-switch acknowledgement.
/// The 16-byte body carries the actor handle twice, at 6..9 and 12..15; the other bytes are constant.
/// A 4-byte body cannot be parsed by the client's compact-length reader, so the full layout is asserted byte for byte.
/// </summary>
public class ActorOutList2505Tests
{
    private const uint RetailHandle = 0x06F01511u;
    private static readonly byte[] RetailFrame =
        Convert.FromHexString("010e0e00cfe31115f00601001115f006");

    [Fact]
    public void SingleHandle_ReproducesTheRetailFrameByteForByte()
    {
        var body = PlayerSpawnService.BuildActorOutList(RetailHandle, new[] { RetailHandle });
        Assert.Equal(RetailFrame, body);
    }

    [Fact]
    public void MsgTag_SitsAtBlobPlus2()
    {
        var body = PlayerSpawnService.BuildActorOutList(RetailHandle, new[] { RetailHandle });
        
        Assert.Equal(0xE3CF, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        Assert.Equal(PlayerSpawnService.MsgTagActorOutList,
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 20)]
    [InlineData(3, 24)]
    [InlineData(5, 32)]
    public void Length_GrowsFourBytesPerHandle_MatchingRetailsObservedLengths(int count, int expected)
    {
        
        
        var handles = Enumerable.Range(0, count).Select(i => RetailHandle + (uint)i).ToArray();
        var body = PlayerSpawnService.BuildActorOutList(RetailHandle, handles);
        Assert.Equal(expected, body.Length);
        Assert.Equal(count, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(10)));
    }

    [Fact]
    public void CountAndHandles_SitAtBlobPlus8AndBlobPlus10()
    {
        
        var handles = new[] { 0x06F01511u, 0x06F01611u };
        var body = PlayerSpawnService.BuildActorOutList(0x06F01511u, handles);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(10)));   
        Assert.Equal(handles[0], BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(handles[1], BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void SubjectHandle_IsIndependentOfTheListEntries()
    {
        
        
        var body = PlayerSpawnService.BuildActorOutList(0x06AAAA11u, new[] { 0x06BBBB11u });
        Assert.Equal(0x06AAAA11u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));
        Assert.Equal(0x06BBBB11u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }
}
