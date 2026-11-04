using RO2.Characters.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// A retail Lv50 character holds up to 375 bonus points while the level-only pool is 75. Login must keep that
/// allocation (it used to zero it); the head shows it as-is with the unspent pool clamped at 0. The login path itself
/// is covered end to end by the capture replay (2310 head+151 / head+163).
/// </summary>
public class BonusStatOverAllocationTests
{
    [Fact]
    public void Allocation_above_the_level_only_pool_is_shown_as_stored_with_zero_unspent()
    {
        var c = new Character { Level = 50, BonusVit = 375 };

        var (bonus5, unspent) = CharacterHeadRules.BonusStatBlock(c);

        Assert.Equal(new ushort[] { 0, 0, 0, 0, 375 }, bonus5);
        Assert.Equal(0, unspent);
        Assert.Equal(375, c.BonusVit);   // reading the head never mutates the allocation
    }
}
