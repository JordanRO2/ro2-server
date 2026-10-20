using System.Buffers.Binary;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Party-matching apply board: 1458, 1460 and 2622, including a populated AnsPartyApplySearch.
/// </summary>
public class PartyApplyBoard2622Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>A 1458 body. Stale stack bytes follow the title's NUL terminator.</summary>
    private const string Retail1458 =
        "0002000000000057004c0020004f00700065006e002000310039003a003000380001f80000000000000000000201000030";

 /// <summary>The 2622 answer to a later 1460 request in the same session.</summary>
    private const string Retail2622Namazuko =
        "016a" + "0100" + "0100" + "01"                                 
        + "00" + "02" + "00" + "0d" + "00" + "00000000"                 
        + "4e0061006d0061007a0075006b006f00000000000000000000000000000000285b1ec6ffffffffffffff" 
        + "57004c0020004f00700065006e002000310039003a003000380001f8000009e53100000000904eec1c00" 
        + "81d9e7a7b3b6e00d";                                           

 /// <summary>A notice sent by another player.</summary>
    private const string Retail2622OBerry =
        "016a" + "0100" + "0100" + "01"
        + "00" + "06" + "00" + "09" + "00" + "00000000"
        + "4f004200650072007200790000000000ffffffffffffff530a000001fffffffeffffffffffffff000000" 
        + "5a00650072006f00200046006c0061007600610020005200650063007200750069007400730001f80000" 
        + "4ba7f2a7b3b6e00d";                                           

    
    private const int RowAt = 2 + 5;
    private const int NameAt = RowAt + 9;
    private const int TitleAt = RowAt + 51;

 /// <summary>Zeroes what follows the first NUL wchar of a 21-wchar buffer: stale bytes the client never reads.</summary>
    private static void ClearAfterNul(byte[] body, int start)
    {
        var i = start;
        while (i < start + 42 && (body[i] != 0 || body[i + 1] != 0)) i += 2;
        for (var j = i; j < start + 42; j++) body[j] = 0;
    }

    [Fact]
    public void RetailFlow_The1458PostsTheRowThe2622Lists()
    {
        var board = new PartyApplyBoard();
        var posting = board.Post(70, Convert.FromHexString(Retail1458));
        Assert.Equal(new PartyApplyBoard.Posting(70, 0, 2, 0, 0, "WL Open 19:08"), posting);

        
        
        var poster = new PlayerState { CharacterId = 70, CharacterUid = 0x0DE0B6B3A7E7D981UL, Name = "Namazuko", Job = 13 };
        Assert.True(PartyApplyBoard.TryParseSearch(Convert.FromHexString("000000010000000000"), out var filter));
        var rows = board.ResolveListings(id => id == 70 ? poster : null, filter);
        var (page, pages, pageRows) = PartyApplyBoard.Page(rows, filter.Page);

        var built = PartyApplySearchPackets.BuildAnsPartyApplySearch(page, pages, pageRows);

        var retail = Convert.FromHexString(Retail2622Namazuko);
        ClearAfterNul(retail, NameAt);
        ClearAfterNul(retail, TitleAt);
        Assert.Equal(Hex(retail), Hex(built));
        Assert.Equal(108, built.Length);
    }

    [Fact]
    public void AnotherPlayersRow_MatchesRetail_ExceptStaleNameTail()
    {
        
        var row = new PartyApplyListing(0, 6, 0, 9, 0, "OBerry", "Zero Flava Recruits", 0x0DE0B6B3A7F2A74BUL);

        var built = PartyApplySearchPackets.BuildAnsPartyApplySearch(1, 1, [row]);

        var retail = Convert.FromHexString(Retail2622OBerry);
        ClearAfterNul(retail, NameAt);
        Assert.Equal(Hex(retail), Hex(built));
    }

    [Fact]
    public void EmptyBoard_IsRetailEmptyConstant()
    {
        
        var board = new PartyApplyBoard();
        var (page, pages, rows) = PartyApplyBoard.Page(board.ResolveListings(_ => null, default), 1);
        var built = PartyApplySearchPackets.BuildAnsPartyApplySearch(page, pages, rows);

        Assert.Equal("01050100010000", Hex(built));
        Assert.Equal(Hex(PartyStatePackets.BuildAnsPartyApplySearchEmpty()), Hex(built));
    }

    [Fact]
    public void RetailSearchBody_IsAllFiltersOff_PageOne()
    {
        
        Assert.True(PartyApplyBoard.TryParseSearch(Convert.FromHexString("000000010000000000"), out var f));
        Assert.Equal(new PartyApplyBoard.SearchFilter(0, 0, 0, 1, 0), f);
        Assert.False(PartyApplyBoard.TryParseSearch(new byte[8], out _));
    }

    [Fact]
    public void Post_ReplacesThePostersPreviousNotice()
    {
        var board = new PartyApplyBoard();
        board.Post(42, ApplyMsg(2, 1, 3, 11, "first"));
        board.Post(42, ApplyMsg(0, 4, 0, 0, "second"));

        Assert.Equal(1, board.Count);
        Assert.Equal(new PartyApplyBoard.Posting(42, 0, 4, 0, 0, "second"), board.Snapshot().Single());
        Assert.Null(board.Post(43, new byte[PartyApplyBoard.ApplyMsgBytes - 1]));
    }

    [Fact]
    public void Post_RefusesWhatNoViewerCouldDraw()
    {
        
        
        
        
        var board = new PartyApplyBoard();

        Assert.Null(board.Post(1, ApplyMsg(4, 1, 0, 0, "t")));
        Assert.Null(board.Post(1, ApplyMsg(5, 1, 0, 0, "t")));
        Assert.Null(board.Post(1, ApplyMsg(255, 1, 0, 0, "t")));
        Assert.Null(board.Post(1, ApplyMsg(0, 1, 4, 0, "t")));
        Assert.Equal(0, board.Count);

        Assert.NotNull(board.Post(1, ApplyMsg(3, 1, 3, 0, "t")));
        for (var c = 0; c <= byte.MaxValue; c++)
            Assert.Equal(c <= 3, PartyApplyBoard.IsListable((byte)c, 0));
        for (var d = 0; d <= byte.MaxValue; d++)
            Assert.Equal(d < PartyApplyBoard.DifficultyCount, PartyApplyBoard.IsListable(0, (byte)d));
    }

    [Fact]
    public void ApplyMsg_TitleStopsAtNul_AndKeepsRoomForTheTerminator()
    {
        var body = ApplyMsg(0, 1, 0, 0, "abc");
        body[7 + 8] = 0x41;   
        Assert.True(PartyApplyBoard.TryParseApplyMsg(body, out _, out _, out _, out _, out var title));
        Assert.Equal("abc", title);

        var full = ApplyMsg(0, 1, 0, 0, new string('x', 21));
        Assert.True(PartyApplyBoard.TryParseApplyMsg(full, out _, out _, out _, out _, out var capped));
        Assert.Equal(20, capped.Length);
    }

    [Fact]
    public void Matches_TreatsZeroAsAll_AndRolesAsAnOverlap()
    {
        var p = new PartyApplyBoard.Posting(1, Category: 2, RoleFlags: 6, Difficulty: 3, Destination: 11, Title: "t");

        Assert.True(PartyApplyBoard.Matches(p, new PartyApplyBoard.SearchFilter(0, 0, 0, 1, 0)));
        Assert.True(PartyApplyBoard.Matches(p, new PartyApplyBoard.SearchFilter(2, 4, 3, 1, 11)));
        Assert.True(PartyApplyBoard.Matches(p, new PartyApplyBoard.SearchFilter(0, 3, 0, 1, 0)));
        Assert.False(PartyApplyBoard.Matches(p, new PartyApplyBoard.SearchFilter(0, 1, 0, 1, 0)));
        Assert.False(PartyApplyBoard.Matches(p, new PartyApplyBoard.SearchFilter(1, 0, 0, 1, 0)));
        Assert.False(PartyApplyBoard.Matches(p, new PartyApplyBoard.SearchFilter(0, 0, 2, 1, 0)));
        Assert.False(PartyApplyBoard.Matches(p, new PartyApplyBoard.SearchFilter(0, 0, 0, 1, 10)));
    }

    [Fact]
    public void ResolveListings_ReadsThePosterNow_AndDropsAnAbsentOne()
    {
        var board = new PartyApplyBoard();
        board.Post(1, ApplyMsg(0, 1, 0, 0, "here"));
        board.Post(2, ApplyMsg(0, 1, 0, 0, "gone"));
        var here = new PlayerState { CharacterId = 1, CharacterUid = 7, Name = "Before", Job = 9 };
        here.Name = "After";   

        var rows = board.ResolveListings(id => id == 1 ? here : null, default);

        var row = Assert.Single(rows);
        Assert.Equal(new PartyApplyListing(0, 1, 0, 9, 0, "After", "here", 7), row);
        Assert.Equal(1, board.Count);   
    }

    [Fact]
    public void ConditionalRemove_KeepsANewerPosting()
    {
        var board = new PartyApplyBoard();
        var old = board.Post(7, ApplyMsg(0, 1, 0, 0, "old"))!;
        board.Post(7, ApplyMsg(0, 1, 0, 0, "new"));

        Assert.False(board.Remove(old));
        Assert.Equal("new", board.Snapshot().Single().Title);
        Assert.True(board.Remove(7u));   
        Assert.Equal(0, board.Count);
    }

    [Fact]
    public void Page_OrdersByGidDescending_AndCapsAtTenRows()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(i => new PartyApplyListing(0, 1, 0, 1, 0, "n" + i, "t", (ulong)i)).ToList();

        var (page, pages, first) = PartyApplyBoard.Page(rows, 1);
        Assert.Equal((ushort)1, page);
        Assert.Equal((ushort)2, pages);
        Assert.Equal(10, first.Count);
        Assert.Equal(12UL, first[0].PosterGid);

        var (lastPage, _, last) = PartyApplyBoard.Page(rows, 9);
        Assert.Equal((ushort)2, lastPage);
        Assert.Equal(new ulong[] { 2, 1 }, last.Select(r => r.PosterGid));

        
        var body = PartyApplySearchPackets.BuildAnsPartyApplySearch(1, 2, rows);
        Assert.Equal(10, body[3 + 4]);
        Assert.Equal(3 + 5 + 10 * PartyApplySearchPackets.RowBytes, body.Length);
        Assert.Equal(5 + 10 * 101, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)));
    }

 /// <summary>A 1458 body laid out as the client builder writes it: [u8 category][u8 roles][u8 difficulty][u32 destination][wchar_t[21]].</summary>
    private static byte[] ApplyMsg(byte category, byte roles, byte difficulty, uint destination, string title)
    {
        var b = new byte[PartyApplyBoard.ApplyMsgBytes];
        b[0] = category;
        b[1] = roles;
        b[2] = difficulty;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(3), destination);
        var t = System.Text.Encoding.Unicode.GetBytes(title);
        t.AsSpan(0, Math.Min(t.Length, 42)).CopyTo(b.AsSpan(7));
        return b;
    }
}
