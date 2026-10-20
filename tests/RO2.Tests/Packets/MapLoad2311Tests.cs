using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gate for RMI 2311 OnReceiveMapLoad, the message that places the client on a map.
/// </summary>
/// <remarks>
/// The body is 45 bytes. Constant bytes: [0]=01, [1..2]=2B2B, [3]=00, [4..5]=0xE294 (little endian), [6]=11, [38..39]=0000, [40]=11.
/// Bytes [12..25] are zero in almost every frame, and [26..37] are three floats.
///
/// Three builders for this opcode used to disagree: the world-entry builder (45 bytes), a copy in the chat teleport path, and a shorter
/// variant used by warp and skill teleport that went out on the encrypted path with the session id in place of the actor handle.
/// A 32-byte body cannot be the message the client expects, and the client drops the encrypted path for game RMIs.
/// All call sites now share one builder, and this test pins it.
///
/// The map id is a u16 at [10..11]. Writing it as u32 shifts every following field by two bytes.
/// </remarks>
public class MapLoad2311Tests
{
    
    private const uint Handle = 0x0637B811u;
    private const uint MapId = 1101u;

    private static byte[] Build() =>
        WorldEntryService.BuildMapLoad2311(Handle, MapId, 769f, 982f, 1.5f);

    [Fact]
    public void Body_Is45Bytes_AsInEveryRetailFrame()
    {
        Assert.Equal(45, Build().Length);
        Assert.Equal(WorldEntryService.MapLoad2311Length, Build().Length);
    }

    [Fact]
    public void ConstantHeader_MatchesRetailByteForByte()
    {
        var b = Build();
        Assert.Equal(Convert.FromHexString("012b2b0094e2"), b.AsSpan(0, 6).ToArray());
    }

    [Fact]
    public void MapId_IsAUInt16At10_NotAUInt32()
    {
        var b = Build();
        Assert.Equal((ushort)MapId, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10)));
        
        
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12)));
    }

    [Fact]
    public void ActorHandle_AppearsAt6_AndIsEchoedAt40()
    {
        var b = Build();
        Assert.Equal(Handle, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(6)));
        Assert.Equal(Handle, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)));
        
        
        Assert.Equal(0x11, b[6]);
        Assert.Equal(0x11, b[40]);
    }

    [Fact]
    public void ZeroRuns_AreWhereRetailHasThem()
    {
        var b = Build();
        for (int i = 12; i <= 25; i++)
            Assert.Equal(0, b[i]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(38)));
    }

    [Fact]
    public void Position_IsThreeFloatsAt26()
    {
        var b = Build();
        Assert.Equal(769f, BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(26)));
        Assert.Equal(982f, BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(30)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(34)));
    }

    [Fact]
    public void TrailingByte_IsTheKnownUnresolvedValue()
    {
        
        
        
        
        Assert.Equal(0xAB, Build()[44]);
    }
}
