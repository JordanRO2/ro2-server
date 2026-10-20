using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Byte-layout test for RMI 2401 OnActorAutoHeal.</summary>
/// <remarks>
/// Body layout: actor handle (twice), curHp, curSp, maxHp, maxSp.
/// </remarks>
public class ActorAutoHeal2401Tests
{
    private const string RetailBodyHex =
        "011c1c00ebe211b3ec0611b3ec0683070100c32600007f120100bd290000";

    [Fact]
    public void ReproducesARealRetailFrameByteForByte()
    {
        var expected = Convert.FromHexString(RetailBodyHex);
        var body = ActorVitalsPackets.BuildActorAutoHeal2401(
            0x06ECB311u, curHp: 67459, curSp: 9923, maxHp: 70271, maxSp: 10685);
        Assert.Equal(expected, body);
    }

    [Fact]
    public void CarriesTheEnvelopeTheClientRequires()
    {
        var body = ActorVitalsPackets.BuildActorAutoHeal2401(0x00100011u, 10, 5, 100, 50);

        
        
        
        Assert.Contains(body[0], new byte[] { 1, 2, 4, 8 });
        Assert.Equal(ActorVitalsPackets.AutoHealBodyLen, body.Length);

        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(blob.Length, body[1]);
        Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob));
        Assert.Equal(ActorVitalsPackets.MsgTagActorAutoHeal,
            BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(2)));
    }

    [Fact]
    public void FieldOrderIsCurCurMaxMax_TheOrderTheWireDiscriminates()
    {
        
        
        
        var body = ActorVitalsPackets.BuildActorAutoHeal2401(
            0x00100011u, curHp: 30, curSp: 7, maxHp: 100, maxSp: 40);
        var blob = body.AsSpan(1 + body[0]);

        var a = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12));
        var b = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16));
        var c = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20));
        var d = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(24));

        Assert.Equal(30u, a);
        Assert.Equal(7u, b);
        Assert.Equal(100u, c);
        Assert.Equal(40u, d);

        Assert.True(a <= c && b <= d, "retail's cur<=max invariant must hold");
        Assert.False(a <= b && c <= d, "the alternating order must NOT be what we emit");
    }

    [Fact]
    public void HandleIsWrittenTwiceAndIsNotASessionId()
    {
        
        
        
        const uint handle = 0x06ECB311u;
        var body = ActorVitalsPackets.BuildActorAutoHeal2401(handle, 1, 2, 3, 4);
        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4)));
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8)));
        Assert.Equal(0x11, blob[4]);
    }

    [Fact]
    public void NegativeVitalsAreClampedRatherThanWrapped()
    {
        
        
        var body = ActorVitalsPackets.BuildActorAutoHeal2401(0x11u, -50, -1, 100, 40);
        var blob = body.AsSpan(1 + body[0]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16)));
    }
}
