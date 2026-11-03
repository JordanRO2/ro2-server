using System.Text;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Characterization of the pure recruit/apply board rules (paging boundaries, the two distinct Matches, the
/// category validation only the apply board does, the 20-char title cap) that the 2621/2622 tests leave implicit.
/// </summary>
public class RecruitmentRulesTests
{
    private static List<PartyMatchingListing> RecruitRows(int n)
        => Enumerable.Range(1, n).Select(i => new PartyMatchingListing(1, 0, 0, 0, 0, (ulong)i, "n", "t")).ToList();

    private static List<PartyApplyListing> ApplyRows(int n)
        => Enumerable.Range(1, n).Select(i => new PartyApplyListing(0, 1, 0, 1, 0, "n", "t", (ulong)i)).ToList();

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(10, 1, 1, 1)]
    [InlineData(11, 1, 2, 1)]
    [InlineData(11, 0, 2, 1)]
    [InlineData(20, 2, 2, 2)]
    [InlineData(21, ushort.MaxValue, 3, 3)]
    public void Page_BoundariesAndClamp_AreIdenticalOnBothBoards(int count, ushort requested, int pages, int page)
    {
        var (rp, rn, recruit) = PartyMatchingBoard.Page(RecruitRows(count), requested);
        var (ap, an, apply) = PartyApplyBoard.Page(ApplyRows(count), requested);

        Assert.Equal(((ushort)page, (ushort)pages), (rp, rn));
        Assert.Equal(((ushort)page, (ushort)pages), (ap, an));
        Assert.Equal(recruit.Select(r => r.PosterGid), apply.Select(r => r.PosterGid));
        Assert.True(recruit.Count <= 10);
    }

    [Fact]
    public void RecruitMatches_ChecksDifficultyCategoryDestinationOnly()
    {
        var p = new PartyMatchingBoard.Posting(1, Category: 2, Difficulty: 3, Destination: 11, Title: "t");

        Assert.True(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(3, 2, 1, 11)));
        Assert.True(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(0, 0, 9, 0)));   // page ignored
        Assert.False(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(3, 2, 1, 12)));
    }

    [Fact]
    public void ApplyMatches_RoleFilterIsABitIntersect_AndAPostingWithNoRoleNeverMatchesARoleFilter()
    {
        var f = new PartyApplyBoard.SearchFilter(0, 7, 0, 1, 0);

        Assert.True(PartyApplyBoard.Matches(new PartyApplyBoard.Posting(1, 0, 1, 0, 0, "t"), f));
        Assert.True(PartyApplyBoard.Matches(new PartyApplyBoard.Posting(1, 0, 4, 0, 0, "t"), f));
        Assert.False(PartyApplyBoard.Matches(new PartyApplyBoard.Posting(1, 0, 0, 0, 0, "t"), f));
        Assert.True(PartyApplyBoard.Matches(new PartyApplyBoard.Posting(1, 0, 0, 0, 0, "t"),
            new PartyApplyBoard.SearchFilter(0, 0, 0, 1, 0)));
    }

    [Fact]
    public void RecruitBoard_KeepsACategoryTheApplyBoardRefuses()
    {
        var body = new byte[PartyMatchingBoard.InviteMsgBytes];
        body[0] = 200;    // category outside the client's table
        body[1] = 200;    // difficulty outside the client's table

        Assert.NotNull(new PartyMatchingBoard().Post(1, body));
        Assert.False(PartyApplyBoard.IsListable(200, 200));
    }

    [Fact]
    public void Title_IsCappedAtTwentyChars_OnBothBoards()
    {
        var wide = Encoding.Unicode.GetBytes(new string('x', 21));

        var recruit = new byte[PartyMatchingBoard.InviteMsgBytes];
        wide.CopyTo(recruit.AsSpan(6));
        Assert.True(PartyMatchingBoard.TryParseInviteMsg(recruit, out _, out _, out _, out var rt));
        Assert.Equal(new string('x', 20), rt);

        var apply = new byte[PartyApplyBoard.ApplyMsgBytes];
        wide.CopyTo(apply.AsSpan(7));
        Assert.True(PartyApplyBoard.TryParseApplyMsg(apply, out _, out _, out _, out _, out var at));
        Assert.Equal(new string('x', 20), at);
    }
}
