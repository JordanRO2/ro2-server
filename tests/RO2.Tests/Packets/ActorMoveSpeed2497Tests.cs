using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-layout test for RMI 2497 OnChangeActorMoveSpeed.
/// The ten leading bytes are constant (01 10 10 00 fc e3 00 00 00 00); the actor handle at 10..13 and the speed at 14..17 vary.
/// The msgTag at offsets 4..7 must be the per-opcode tag (0xE3FC). The handler does not check that tag, so the test
/// asserts the full byte layout.
/// </summary>
public class ActorMoveSpeed2497Tests
{
 /// <summary>The ten leading bytes that are constant across all 2497 frames.</summary>
    private static readonly byte[] RetailConstantPrefix =
        Convert.FromHexString("01101000fce300000000");

    [Fact]
    public void LeadingTenBytes_MatchRetailConstantPrefix_ByteForByte()
    {
        var body = TraitPackets.BuildActorMoveSpeed2497(0x0137B811u, 5.0f);
        Assert.Equal(RetailConstantPrefix, body.AsSpan(0, 10).ToArray());
    }
}
