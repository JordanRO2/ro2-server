using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// Byte-level tests for the 2964 AnsRankingList and 2965 AnsRanking bodies built by RankingPackets. Each body is
/// compared against a reference frame, from the bytes after the RMI header.
/// </summary>
public class RankingPacketsTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>The 2965 response for the type-1 board: rank 16, previous rank 18, class 13, profession 3, score 19, in a guild.</summary>
    internal const string Retail2965Type1Ranked =
        "4000000010000000120000000d031300000000000000070052004900430048006c0047004f000e0057006500410069006e0074" +
        "0046006f00720074006c006500730073000000";

 /// <summary>The 2965 response for the same character on the type-2 board: unranked, score 0, shown without a guild although it belongs to one.</summary>
    internal const string Retail2965Type2Unranked =
        "2400000000000000000000000d030000000000000000070052004900430048006c0047004f0000000000";

 /// <summary>The five-row class-13 board: global ranks 27, 93, 102, 107 and 114.</summary>
    internal const string Retail2964Type3Class13 =
        "0105240000001b000000140000000d040f0300000000000007004e00750063006c00650075007a000000200000005d0000000000" +
        "00000d015100000000000000050073006b00750079003100000042000000660000006c0000000d04490000000000000008004e" +
        "0061006d0061007a0075006b006f000e0057006500410069006e00740046006f00720074006c006500730073002c0000006b00" +
        "0000000000000d0443000000000000000b00420072007500740061006c0052006f0067007500650000004000000072000000000000" +
        "000d033800000000000000070052004900430048006c0047004f000e0057006500410069006e00740046006f00720074006c0065" +
        "00730073000000";

    [Fact]
    public void Build2965_Ranked_MatchesRetail()
    {
        var self = new RankRow(16, 18, 13, 3, 19, "RICHlGO", "WeAintFortless");
        Assert.Equal(Retail2965Type1Ranked, Hex(RankingPackets.Build2965(self)));
    }

    [Fact]
    public void Build2965_Unranked_MatchesRetail()
    {
        
        var self = RankingService.Unranked(13, 3, 0, "RICHlGO");
        Assert.Equal(Retail2965Type2Unranked, Hex(RankingPackets.Build2965(self)));
    }

    [Fact]
    public void Build2964_FilteredBoard_MatchesRetail()
    {
        var rows = new[]
        {
            new RankRow(27, 20, 13, 4, 783, "Nucleuz", ""),
            new RankRow(93, 0, 13, 1, 81, "skuy1", ""),
            new RankRow(102, 108, 13, 4, 73, "Namazuko", "WeAintFortless"),
            new RankRow(107, 0, 13, 4, 67, "BrutalRogue", ""),
            new RankRow(114, 0, 13, 3, 56, "RICHlGO", "WeAintFortless"),
        };
        Assert.Equal(Retail2964Type3Class13, Hex(RankingPackets.Build2964(rows)));
    }

    [Fact]
    public void Build2964_TenRowBoard_MatchesRetail()
    {
        
        const string retail =
            "010a3000000001000000020000000c02400000000000000005004d007200560065006b0008004c006f006e0065004d007900" +
            "740068003a000000020000000a000000110425000000000000000e004500750064006f007200430072006500630065006e00" +
            "74006900610004005400570052004f0030000000030000002100000011032400000000000000050041006c00530045006c0008" +
            "004e0065006d00650073006900730027003a0000000400000026000000110423000000000000000a0044004e00410046006100" +
            "72006d0065007200340008004e0065006b006f004e006f006f006b003a000000050000002d000000110221000000000000000a" +
            "0044004e0041004600610072006d0065007200310008004e0065006b006f004e006f006f006b00360000000600000039000000" +
            "120320000000000000000c0055007200610053006f0075006c006d0061006b006500720004005400570052004f003a00000007" +
            "0000002f00000011021f000000000000000a0044004e0041004600610072006d0065007200330008004e0065006b006f004e00" +
            "6f006f006b003a000000080000002e00000011021e000000000000000a0044004e0041004600610072006d0065007200320008" +
            "004e0065006b006f004e006f006f006b0032000000090000003c00000009021c000000000000000a005300650069006b006100" +
            "4d00610074006f00750004005400570052004f00360000000a0000000000000006031a000000000000000700720068006500" +
            "650079006e006e000900470072006900660069006e0064006f0072000000";
        var rows = new[]
        {
            new RankRow(1, 2, 12, 2, 64, "MrVek", "LoneMyth"),
            new RankRow(2, 10, 17, 4, 37, "EudorCrecentia", "TWRO"),
            new RankRow(3, 33, 17, 3, 36, "AlSEl", "Nemesis'"),
            new RankRow(4, 38, 17, 4, 35, "DNAFarmer4", "NekoNook"),
            new RankRow(5, 45, 17, 2, 33, "DNAFarmer1", "NekoNook"),
            new RankRow(6, 57, 18, 3, 32, "UraSoulmaker", "TWRO"),
            new RankRow(7, 47, 17, 2, 31, "DNAFarmer3", "NekoNook"),
            new RankRow(8, 46, 17, 2, 30, "DNAFarmer2", "NekoNook"),
            new RankRow(9, 60, 9, 2, 28, "SeikaMatou", "TWRO"),
            new RankRow(10, 0, 6, 3, 26, "rheeynn", "Grifindor"),
        };
        Assert.Equal(retail, Hex(RankingPackets.Build2964(rows)));
    }

    [Fact]
    public void Build2964_Empty_IsRetailsEmptyBoard()
    {
        
        
        Assert.Equal("01000100", Hex(RankingPackets.Build2964(Array.Empty<RankRow>())));
    }

    [Fact]
    public void Build2964_ShowsAtMostTenRows()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(i => new RankRow((uint)i, 0, 6, 1, (ulong)(100 - i), "c" + i, ""))
            .ToArray();
        var body = RankingPackets.Build2964(rows);
        Assert.Equal(new byte[] { 0x01, 0x0a }, body[..2]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, body[^2..]);
        Assert.Equal(Hex(RankingPackets.Build2964(rows.Take(10).ToArray())), Hex(body));
    }
}
