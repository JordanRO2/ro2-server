using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The low byte of an actor handle is a type tag that the client uses to resolve actors.</summary>
/// <remarks>
/// Player handles end in 0x11 and NPC handles end in 0x13. The attacker handle in 2694 NfyNormalAttackStart
/// (blob+12) is resolved by the client before the swing plays, so an untagged handle produces no attack animation.
/// </remarks>
public class ActorHandleTagTests
{
    [Theory]
    [InlineData(1u)]
    public void PlayerHandlesCarryThePcTag(uint sessionId)
    {
        var handle = PlayerStateManager.BuildActorHandle(sessionId);
        Assert.Equal(PlayerStateManager.PcActorTag, (byte)(handle & 0xFF));
        Assert.Equal(0x11, (byte)(handle & 0xFF));
    }

    [Fact]
    public void TheOldSchemePutTheSessionNumberWhereTheTagBelongs()
    {
        
        const uint old = 0x00100000u + 1u;
        Assert.Equal(0x01, (byte)(old & 0xFF));
        Assert.NotEqual(PlayerStateManager.PcActorTag, (byte)(old & 0xFF));
    }

    [Fact]
    public void PlayerAndNpcHandlesCannotCollide()
    {
        
        
        for (uint seq = 1; seq < 500; seq++)
        {
            var pc = PlayerStateManager.BuildActorHandle(seq);
            var npc = (seq << 8) | NpcStateManager.NpcActorTag;
            Assert.NotEqual(pc, npc);
        }
    }
}
