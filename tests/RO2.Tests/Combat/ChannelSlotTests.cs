using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="ChannelSlot"/> (the channel state moved out of PlayerState in B4 S14).</summary>
public class ChannelSlotTests
{
    [Fact]
    public void Begin_SetsActive_TryEnd_OnlyEndsTheMatchingSkill()
    {
        var slot = new ChannelSlot();
        Assert.Equal(0u, slot.Active);

        slot.Begin(630410);
        Assert.Equal(630410u, slot.Active);

        Assert.False(slot.TryEnd(630405));   // a different skill leaves it untouched
        Assert.Equal(630410u, slot.Active);

        Assert.True(slot.TryEnd(630410));     // the matching skill ends it
        Assert.Equal(0u, slot.Active);

        Assert.False(slot.TryEnd(630410));    // already ended
        Assert.False(slot.TryEnd(0));         // 0 never ends anything
    }
}
