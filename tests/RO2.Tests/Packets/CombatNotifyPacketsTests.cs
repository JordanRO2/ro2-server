using System.Buffers.Binary;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout gates for the combat and economy notifies that must use the compact ByteArray envelope.
/// </summary>
/// <remarks>
/// An unframed body is rejected by the client decoder, which then hands the consumer a null blob.
/// </remarks>
public class CombatNotifyPacketsTests
{
    private static ReadOnlySpan<byte> Blob(byte[] body) => body.AsSpan(1 + body[0]);

    private static void AssertWellFormed(byte[] body, int expectedLen, ushort expectedTag)
    {
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });   
        Assert.Equal(expectedLen, body.Length);
        var blob = Blob(body);
        Assert.Equal(blob.Length, body[1]);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(expectedTag, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    
    [Fact]
    public void DelEffect2494_ReproducesARealRetailFrame()
    {
        
        
        var expected = Convert.FromHexString("01181800c9e3000000002deb000011c7f0060000000000000000");
        var body = ActorVitalsPackets.BuildDelEffect2494(60205, 0x06F0C711u);
        Assert.Equal(expected, body);
    }

    
    [Fact]
    public void ZenyChange2435_ReproducesARealRetailFrame()
    {
        
        
        var expected = Convert.FromHexString("01141400c6e31115f006f18100000000000000000000");
        var body = ActorVitalsPackets.BuildZenyChange2435(0x06F01511u, 33265);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void ZenyChange2435_MoneyIs64BitAndTheHandleSitsAtBlobPlus4()
    {
        
        
        var body = ActorVitalsPackets.BuildZenyChange2435(0x06F01511u, 0x0000_0002_0000_0BB8L);
        AssertWellFormed(body, ActorVitalsPackets.ZenyChangeBodyLen,
            ActorVitalsPackets.MsgTagZenyChange);
        var blob = Blob(body);
        Assert.Equal(0x06F01511u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal(0x0000_0002_0000_0BB8UL, BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)));  
    }

    [Fact]
    public void ZenyChange2435_SendsTheBalanceAndAppliesTheDelta()
    {
        
        
        
        var player = new PlayerState { EntityId = 0x06F01511u, Gold = 1000 };

        var body = ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, -250);
        Assert.Equal(750, player.Gold);
        Assert.Equal(750UL, BinaryPrimitives.ReadUInt64LittleEndian(Blob(body).Slice(8)));

        
        
        var body2 = ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, -10_000);
        Assert.Equal(750, player.Gold);
        Assert.Equal(750UL, BinaryPrimitives.ReadUInt64LittleEndian(Blob(body2).Slice(8)));
    }

    
    [Fact]
    public void DeadNotify2392_ReproducesARealRetailFrame()
    {
        
        
        var expected = Convert.FromHexString("01141400cbe20000000013399e01111c0f0790070100");
        var body = ActorVitalsPackets.BuildDeadNotify2392(0x019E3913u, 0x070F1C11u, 67472);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void DeadNotify2392_CarriesBothActorHandlesWithTheirTypeTags()
    {
        
        var body = ActorVitalsPackets.BuildDeadNotify2392(0x019E3913u, 0x070F1C11u);
        AssertWellFormed(body, ActorVitalsPackets.DeadNotifyBodyLen,
            ActorVitalsPackets.MsgTagDeadNotify);
        var blob = Blob(body);
        Assert.Equal(0x13, blob[8]);    
        Assert.Equal(0x11, blob[12]);   
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16)));
    }
}
