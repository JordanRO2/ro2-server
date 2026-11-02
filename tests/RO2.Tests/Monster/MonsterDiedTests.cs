using RO2.Monster.Application;
using Xunit;

namespace RO2.Tests.Monster;

/// <summary>The MonsterDied fact: a value the damage paths build once from the kill flag and hand to the shared kill grant.</summary>
public class MonsterDiedTests
{
    [Fact]
    public void Carries_the_corpse_handle_the_catalog_id_and_the_killer()
    {
        var d = new MonsterDied(Handle: 0x1234, NpcId: 77, KillerHandle: 0x111);
        Assert.Equal(0x1234u, d.Handle);
        Assert.Equal(77u, d.NpcId);
        Assert.Equal(0x111u, d.KillerHandle);
    }

    [Fact]
    public void Two_facts_for_the_same_kill_are_equal()
        => Assert.Equal(new MonsterDied(1, 2, 3), new MonsterDied(1, 2, 3));
}
