using RO2.Economy.Domain;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Wire tests for the auction search class filter (1400) and the own-record counts (2456).
/// Bodies are the RMI body after the opcode. The sort-order byte of 1400 is covered by
/// <see cref="AuctionSearchSortBitsTests"/>.
/// </summary>
public class AuctionSearchReauditTests
{
    
    
    
    [Fact]
    public void Retail1400_WithTheClassCheckbox_ParsesJobAndRawOrder()
    {
        
        
        
        Assert.True(AuctionPackets.TryParseSearch(Hex("17000000250060e3112a2b040100020000ff110000006400008002"), out var r));
        Assert.Equal((byte)2, r.HighCategory);
        Assert.Equal((byte)0x11, r.JobFilter);
        Assert.Equal((byte)0x80, (byte)r.Order);
        Assert.True(AuctionPackets.IsJobFilter(r.JobFilter));
    }

    [Fact]
    public void Retail1400_WithoutTheCheckbox_HasNoJobFilter()
    {
        
        Assert.True(AuctionPackets.TryParseSearch(Hex("17000000250060e311d8a90a01000a0000ffff00000064000002ff"), out var r));
        Assert.Equal(AuctionPackets.AnyCategory, r.JobFilter);
        Assert.False(AuctionPackets.IsJobFilter(r.JobFilter));
        Assert.True(AuctionPackets.PassesJobFilter(64, r.JobFilter));
    }

    [Theory]
    
    [InlineData(65536L, true)]
    [InlineData(196608L, true)]
    [InlineData(0L, true)]
    
    [InlineData(64L, false)]
    [InlineData(131072L, false)]
    public void ClassFilter_KeepsRequireJobZeroOrTheJobBit(long requireJob, bool passes)
        => Assert.Equal(passes, AuctionPackets.PassesJobFilter(requireJob, 0x11));

    
    
    
 /// <summary>
 /// Rebuilds a 2456 frame from its records and requires it to match byte for byte. The two counts are
 /// taken from the records owned by the requesting character.
 /// </summary>
    private static void AssertRetail2456(string frameHex, ulong requesterUid, ushort expect12, ushort expect14)
    {
        var frame = Hex(frameHex);
        int n = frame[0];
        var blob = frame.AsSpan(1 + n);
        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4));
        ushort page = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(8));
        ushort total = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(10));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(16));
        var entries = new List<byte[]>();
        ushort bids = 0, own = 0;
        for (int k = 0; k < count; k++)
        {
            var e = blob.Slice(18 + AuctionPackets.EntryLength * k, AuctionPackets.EntryLength).ToArray();
            if (BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(202)) == requesterUid) bids++;
            if (BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(194)) == requesterUid) own++;
            entries.Add(e);
        }
        Assert.Equal(expect12, bids);
        Assert.Equal(expect14, own);
        Assert.Equal(frameHex, ToHex(AuctionPackets.BuildSearch2456(handle, page, total, entries, bids, own)));
    }

    [Fact]
    public void Retail2456_OwnArticlesAt14()
    {
        
        
        
        AssertRetail2456(
            "02b601b60161e311168803010002000000010002005907e0677dd26e0e34447f0ea83d040ecaae5301000001670000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "000000000064b4c40400000000f0b7c404000000006f003000330030006f0000000000000000000000000000000000000000" +
            "00000000000000000000000000351d8d65f4a7b3b6e00d0000000000000000d703e0677dd26e0e679bb00ea83d040ecaae53" +
            "0100000667000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000000000e4334c0500000000f0524c050000000055007200610053006f0075006c006d0061006b" +
            "00650072000000000000000000000000000000000000003805786efaa7b3b6e00d0000000000000000",
            0x0DE0B6B3A7FA6E78UL, 0, 1);
    }

    [Fact]
    public void Retail2456_OwnTopBidsAt12()
    {
        
        
        
        AssertRetail2456(
            "02b601b60161e311a1be0501000200010000000200e10be0677dd26e0e2cb5080fa83d040eefd9fe00102701670000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008f93" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000de64080000000000808d5b00000000004400610072006b0065006e00670065006c006900670074000000000000" +
            "000000000000000000000000002a227345faa7b3b6e00d85f0fba7b3b6e00de909e0677dd26e0e37b9f40ea83d040e57dbfe" +
            "0010270167000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000759f010061000e0080028e3e1700641b75097842c3fe691b7ef900000000000000000000000000000000" +
            "00000000000000000000000000000000b5a3fa0100000000b5a3fa010000005700650061006b004f006e0065000000000000" +
            "0000000000000000000000000000000000000000000000180b83d2eea7b3b6e00d0000000000000000",
            0x0DE0B6B3A7FBF085UL, 1, 0);
    }

    [Fact]
    public void OwnCounts_ArePerPage_ByCharacterId()
    {
        
        const uint me = 42;
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var page = new List<AuctionArticle>
        {
            new() { Id = 1, Uid = AuctionPackets.MakeArticleUid(1), SellerCharId = me, ItemId = 1, StartPrice = 100, ExpiresAt = now.AddHours(1) },
            new() { Id = 2, Uid = AuctionPackets.MakeArticleUid(2), SellerCharId = 7, ItemId = 1, StartPrice = 100, ExpiresAt = now.AddHours(1),
                    TopBid = 110, TopBidderCharId = me },
            new() { Id = 3, Uid = AuctionPackets.MakeArticleUid(3), SellerCharId = me, ItemId = 1, StartPrice = 100, ExpiresAt = now.AddHours(1) },
            new() { Id = 4, Uid = AuctionPackets.MakeArticleUid(4), SellerCharId = 8, ItemId = 1, StartPrice = 100, ExpiresAt = now.AddHours(1),
                    TopBid = 110, TopBidderCharId = 9 },
            new() { Id = 5, Uid = AuctionPackets.MakeArticleUid(5), SellerCharId = 8, ItemId = 1, StartPrice = 100, ExpiresAt = now.AddHours(1) },
        };
        var (bids, own) = AuctionPackets.OwnCounts(page, me);
        Assert.Equal((ushort)1, bids);
        Assert.Equal((ushort)2, own);

        var entries = page.Select(a => AuctionPackets.BuildEntry210(a, now, withSellerName: true)).ToList();
        var body = AuctionPackets.BuildSearch2456(0x0AA9D811, 1, page.Count, entries, bids, own);
        int n = body[0];
        var blob = body.AsSpan(1 + n);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(12)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(14)));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(16)));
        Assert.Equal(18 + 5 * AuctionPackets.EntryLength, blob.Length);

        
        Assert.Equal((ushort)0, AuctionPackets.OwnCounts(page, 0).OwnTopBids);
    }
}
