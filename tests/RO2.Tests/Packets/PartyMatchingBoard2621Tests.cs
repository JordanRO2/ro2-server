using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Party-matching recruit board: the 1554 and 1560 requests and the populated 2621 member-search answer.
/// </summary>
public class PartyMatchingBoard2621Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>
 /// A solo recruit row whose name and title are NUL-padded, so the whole 110-byte body is reproduced byte for byte.
 /// </summary>
    [Fact]
    public void PopulatedAnswer_MatchesRetailFrame_ByteForByte()
    {
        const string retail =
            "016c0100010001010000000000000000000007ccb8a7b3b6e00d"
            + "4a0030004b00330052007a00750066006100660061006800000000000000000000000000000000000000"
            + "52006b0072007500740020004700750069006c00640020004d006d0062007200200077006d0001f80000";
        var row = new PartyMatchingListing(1, 0, 0, 0, 0, 0x0DE0B6B3A7B8CC07UL, "J0K3Rzufafah",
            "Rkrut Guild Mmbr wm\uF801");

        var body = PartyStatePackets.BuildAnsPartyMemberSearch(1, 1, [row]);

        Assert.Equal(retail, Hex(body));
    }

 /// <summary>
 /// A party recruit row (three members, party handle with the 0x15 group tag). The client never reads the name
 /// buffer past its NUL terminator, so only the other fields must match.
 /// </summary>
    [Fact]
    public void PartyRow_MatchesRetailFrame_ExceptStaleNameTail()
    {
        var retail = Convert.FromHexString(
            "016c010001000103000015b4040000000000eae5760dc161a637"
            + "4300720061007a0079005200690063006800000000004250010000000030a3431d0000000070ac001200"
            + "4c00460020004700720069006e006400200043006f006e0064006f007200200001f80000000000000000");
        
        
        const int nameStart = 2 + 5 + 19;
        for (var i = nameStart + ("CrazyRich".Length + 1) * 2; i < nameStart + 42; i++) retail[i] = 0;

        var row = new PartyMatchingListing(3, 0, 0, 0x0004B415u, 0, 0x37A661C10D76E5EAUL, "CrazyRich",
            "LF Grind Condor \uF801");

        Assert.Equal(Hex(retail), Hex(PartyStatePackets.BuildAnsPartyMemberSearch(1, 1, [row])));
    }

    [Fact]
    public void EmptyBoard_IsRetailEmptyConstant()
    {
        
        Assert.Equal("01050100010000", Hex(PartyStatePackets.BuildAnsPartyMemberSearch(1, 1, [])));
        Assert.Equal(Hex(PartyStatePackets.BuildAnsPartyMemberSearchEmpty()),
            Hex(PartyStatePackets.BuildAnsPartyMemberSearch(1, 1, [])));
    }

    [Fact]
    public void TwoByteCompactMarker_FromRetailWidth()
    {
        
        var row = new PartyMatchingListing(1, 0, 0, 0, 0, 1, "a", "b");
        var body = PartyStatePackets.BuildAnsPartyMemberSearch(1, 1, [row, row]);
        Assert.Equal("02d3000100010002", Hex(body)[..16]);
        Assert.Equal(3 + 211, body.Length);
    }

    [Fact]
    public void Page_OrdersByGidDescending_AndCapsAtTenRows()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(i => new PartyMatchingListing(1, 0, 0, 0, 0, (ulong)i, "n" + i, "t")).ToList();

        var (page, pages, first) = PartyMatchingBoard.Page(rows, 1);
        Assert.Equal((ushort)1, page);
        Assert.Equal((ushort)2, pages);
        Assert.Equal(10, first.Count);
        Assert.Equal(12UL, first[0].PosterGid);
        Assert.Equal(3UL, first[9].PosterGid);

        
        var (lastPage, _, last) = PartyMatchingBoard.Page(rows, 9);
        Assert.Equal((ushort)2, lastPage);
        Assert.Equal(new ulong[] { 2, 1 }, last.Select(r => r.PosterGid));

        
        var (p0, n0, r0) = PartyMatchingBoard.Page([], 0);
        Assert.Equal((ushort)1, p0);
        Assert.Equal((ushort)1, n0);
        Assert.Empty(r0);

        
        
        var body = PartyStatePackets.BuildAnsPartyMemberSearch(1, 2, rows);
        Assert.Equal(10, body[3 + 4]);
        Assert.Equal(3 + 5 + 10 * PartyStatePackets.PartyMemberSearchRowBytes, body.Length);
    }

    [Fact]
    public void RetailSearchBody_IsAllFiltersOff_PageOne()
    {
        
        Assert.True(PartyMatchingBoard.TryParseSearch(Convert.FromHexString("0000010000000000"), out var f));
        Assert.Equal(new PartyMatchingBoard.SearchFilter(0, 0, 1, 0), f);
        Assert.False(PartyMatchingBoard.TryParseSearch(new byte[7], out _));
    }

    [Fact]
    public void InviteMsg_IsParsedAndReplacesThePostersPreviousPosting()
    {
        var board = new PartyMatchingBoard();
        var body = InviteMsg(category: 2, difficulty: 3, destination: 11, title: "for kara");

        var p = board.Post(42, body);

        Assert.NotNull(p);
        Assert.Equal(new PartyMatchingBoard.Posting(42, 2, 3, 11, "for kara"), p);
        board.Post(42, InviteMsg(0, 0, 0, "second"));
        Assert.Equal(1, board.Count);
        Assert.Equal("second", board.Snapshot().Single().Title);
        Assert.Null(board.Post(43, new byte[47]));
    }

 /// <summary>
 /// The client's own 1554 posting of the title "Test" with all categories set to "All". Posting it produces a 2621 row
 /// carrying the same three fields and the title.
 /// </summary>
    [Fact]
    public void CapturedInviteMsg_PostsTheTitleAndFieldsTheRowCarries()
    {
        var body = Convert.FromHexString(
            "000000000000540065007300740001f800009e0a9c659a0a1060008000000000000000000000000002010000789ae637");
        var board = new PartyMatchingBoard();

        var p = board.Post(5, body);

        Assert.Equal(new PartyMatchingBoard.Posting(5, 0, 0, 0, "Test"), p);
        Assert.True(PartyMatchingBoard.Matches(p!, new PartyMatchingBoard.SearchFilter(0, 0, 1, 0)));
        var row = new PartyMatchingListing(1, p!.Category, p.Difficulty, 0, p.Destination, 1, "n", p.Title);
        var built = PartyStatePackets.BuildAnsPartyMemberSearch(1, 1, [row]);
        
        Assert.Equal("540065007300740001f80000", Hex(built).Substring((2 + 5 + 61) * 2, 24));
    }

    [Fact]
    public void InviteMsg_TitleStopsAtNul_AndKeepsRoomForTheTerminator()
    {
        var body = InviteMsg(0, 0, 0, "abc");
        
        body[6 + 8] = 0x41;
        Assert.True(PartyMatchingBoard.TryParseInviteMsg(body, out _, out _, out _, out var title));
        Assert.Equal("abc", title);

        var full = InviteMsg(0, 0, 0, new string('x', 21));
        Assert.True(PartyMatchingBoard.TryParseInviteMsg(full, out _, out _, out _, out var capped));
        Assert.Equal(20, capped.Length);
    }

    [Fact]
    public void Matches_TreatsZeroAsAll()
    {
        var p = new PartyMatchingBoard.Posting(1, Category: 2, Difficulty: 3, Destination: 11, Title: "t");

        Assert.True(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(0, 0, 1, 0)));
        Assert.True(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(3, 2, 1, 11)));
        Assert.False(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(2, 0, 1, 0)));
        Assert.False(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(0, 1, 1, 0)));
        Assert.False(PartyMatchingBoard.Matches(p, new PartyMatchingBoard.SearchFilter(0, 0, 1, 10)));
    }

    [Fact]
    public void ConditionalRemove_KeepsANewerPosting()
    {
        var board = new PartyMatchingBoard();
        var old = board.Post(7, InviteMsg(0, 0, 0, "old"))!;
        board.Post(7, InviteMsg(0, 0, 0, "new"));

        Assert.False(board.Remove(old));
        Assert.Equal("new", board.Snapshot().Single().Title);
    }

 /// <summary>A 1554 body as the client builds it: [u8 category][u8 difficulty][u32 destination][wchar_t[21]].</summary>
    private static byte[] InviteMsg(byte category, byte difficulty, uint destination, string title)
    {
        var b = new byte[PartyMatchingBoard.InviteMsgBytes];
        b[0] = category;
        b[1] = difficulty;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), destination);
        var t = System.Text.Encoding.Unicode.GetBytes(title);
        t.AsSpan(0, Math.Min(t.Length, 42)).CopyTo(b.AsSpan(6));
        return b;
    }
}
