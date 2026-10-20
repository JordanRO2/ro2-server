using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Body layouts of the dungeon finder party role round. 2831, 2834 and 1568 are checked against reference
/// frames; 2822 and 2830 have no reference frame, so those tests pin the length the client reader fixes.
/// </summary>
public class FindPartySelectRole2831Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>Reference body of 2831 (<c>0200000000</c>).</summary>
    [Fact]
    public void RoundFailed2831_MatchesRetail()
    {
        Assert.Equal("0200000000", Hex(FindPartyRolePackets.BuildRoundFailed2831()));
    }

 /// <summary>Reference bodies of 2834: <c>0000</c> and <c>0800</c>.</summary>
    [Fact]
    public void SelectRoleAnswer2834_MatchesRetail()
    {
        Assert.Equal("0000", Hex(FindPartyRolePackets.BuildSelectRoleAnswer2834(FindPartyRolePackets.SelectRoleAccepted)));
        Assert.Equal("0800", Hex(FindPartyRolePackets.BuildSelectRoleAnswer2834(FindPartyRolePackets.SelectRoleSlotTaken)));
    }

 /// <summary>Reference bodies of 1568: <c>04</c> (healer) and <c>01</c> (tank).</summary>
    [Theory]
    [InlineData("04", DungeonFinderPackets.RoleHealer)]
    [InlineData("01", DungeonFinderPackets.RoleTank)]
    public void SelectRole1568_ParsesRetailBodies(string body, byte expected)
    {
        Assert.True(FindPartyRolePackets.TryParseSelectRole1568(Convert.FromHexString(body), out var flags));
        Assert.Equal(expected, flags);
    }

 /// <summary>read_side is one U8: any other length is not a valid 1568.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0400")]
    public void SelectRole1568_RejectsOtherLengths(string body)
    {
        Assert.False(FindPartyRolePackets.TryParseSelectRole1568(Convert.FromHexString(body), out _));
    }

 /// <summary>
 /// 2822: no read_side field, per the client reader; the consumer takes no message parameter.
 /// Not verified against a reference frame.
 /// </summary>
    [Fact]
    public void SelectRolePlease2822_IsEmpty()
    {
        Assert.True(FindPartyRolePackets.SelectRolePlease2822.IsEmpty);
    }

 /// <summary>
 /// 2830: one U16, which the client only logs before hiding its window. No reference frame exists;
 /// this pins the length, not the value.
 /// </summary>
    [Fact]
    public void SelectRoleCancelAnswer2830_IsOneU16()
    {
        Assert.Equal(2, PartyStatePackets.BuildAnsFindPartySelectRoleCancel().Length);
    }
}
