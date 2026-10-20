using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Layouts of the small party-matching answers 2810, 1556, 2805, 2827 and 2832, pinned to the client's readers
/// and builders.
/// </summary>
public class PartyMatchingAnswersA13Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>
 /// 2810 body for a list-21 join whose instance move failed: a single 0x03 byte.
 /// </summary>
    [Fact]
    public void JoinFail2810_MatchesRetailDumpFrames()
    {
        Assert.Equal("03", Hex(DungeonFinderPackets.BuildJoinFail2810(DungeonFinderPackets.JoinFailMoveFailed)));
    }

 /// <summary>
 /// 1554 body: [u8 category][u8 difficulty][u32 destination][wchar_t[21] title]. The title is NUL-terminated.
 /// </summary>
    [Fact]
    public void InviteMsg1554_DumpFrame_ParsesTheTitleAtPlus6()
    {
        var body = Convert.FromHexString(
            "00000000000057004c0020004f00700065006e002000310034003a003200300001f8"
            + "00000000000002010000e837a339");

        Assert.Equal(48, body.Length);
        Assert.True(PartyMatchingBoard.TryParseInviteMsg(body, out var category, out var difficulty,
            out var destination, out var title));
        Assert.Equal(0, category);
        Assert.Equal(0, difficulty);
        Assert.Equal(0u, destination);
        Assert.Equal("WL Open 14:20", title);
        Assert.Equal("0101", Hex(PartyStatePackets.BuildAnsPartyMatchingMsg(result: 1, flag: 1)));
    }

 /// <summary>
 /// 1556 as built by the client: u16 roles | (category shifted left by 8). Example: a dealer (2) searching category 3, Random Dungeon (Hard).
 /// </summary>
    [Fact]
    public void FindDungeonRandom1556_IsRoleThenCategory()
    {
        Assert.True(DungeonFinderPackets.TryParseFindDungeonRandom1556([0x02, 0x03], out var roles, out var category));
        Assert.Equal(DungeonFinderPackets.RoleDealer, roles);
        Assert.Equal(3, category);
        Assert.False(DungeonFinderPackets.TryParseFindDungeonRandom1556([0x02], out _, out _));
        Assert.False(DungeonFinderPackets.TryParseFindDungeonRandom1556([0x02, 0x03, 0x00], out _, out _));
    }

 /// <summary>2805: a single u16 result code.</summary>
    [Theory]
    [InlineData(DungeonFinderPackets.ResultAccepted, "0000")]
    [InlineData(DungeonFinderPackets.ResultAlreadyRegistered, "0900")]
    [InlineData(DungeonFinderPackets.ResultLevel, "1100")]
    public void FindDungeonRandomAnswer2805_IsOneU16(ushort result, string hex)
    {
        Assert.Equal(hex, Hex(DungeonFinderPackets.BuildFindDungeonRandomAnswer2805(result)));
    }

 /// <summary>2827: a single u16 value. The client closes window 44 for any value.</summary>
    [Fact]
    public void AnsPartyKickVote2827_IsTheNeutralU16()
    {
        Assert.Equal("0000", Hex(PartyKickVoteHandler.BuildAnsPartyKickVote2827()));
    }

 /// <summary>2832: a single u16 value, which the client only logs.</summary>
    [Fact]
    public void AnsOutDungeon2832_IsTheNeutralU16()
    {
        Assert.Equal("0000", Hex(PartyMatchingOutDungeonHandler.BuildAnsOutDungeon2832()));
    }
}
