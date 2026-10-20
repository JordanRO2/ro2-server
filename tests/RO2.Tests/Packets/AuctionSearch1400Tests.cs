using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests 1400 ReqAuctionArticleList against the 2456 OnAuctionSearch answer. Bodies are the RMI body after the opcode.
/// </summary>
public class AuctionSearch1400Tests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    
    private const string NameSearch =
        "5b000000250060e311d8a90a0100ffffffffff000000641100256b4201266b4201276b4201286b4201296b4201d4e55201" +
        "a1525701a2525701a3525701a4525701498cf7014a8cf7014b8cf7014c8cf7014d8cf7018e8cf7010a222c0402ff";

    [Fact]
    public void NameSearch_ParsesCategoriesIdsAndOrder()
    {
        Assert.True(AuctionPackets.TryParseSearch(Hex(NameSearch), out var r));
        Assert.Equal((ushort)1, r.Page);
        Assert.Equal((byte)0xFF, r.HighCategory);
        Assert.Equal((byte)0xFF, r.MediumCategory);
        Assert.Equal((byte)0xFF, r.LowCategory);
        Assert.NotNull(r.ItemIds);
        Assert.Equal(17, r.ItemIds!.Length);
        Assert.Equal(21130021u, r.ItemIds[0]);
        Assert.Equal(70001162u, r.ItemIds[16]);
        Assert.Equal(AuctionPackets.SearchOrder.Default, r.Order);
    }

    [Fact]
    public void CategorySearch_HasNoIdList()
    {
        
        Assert.True(AuctionPackets.TryParseSearch(Hex("17000000250060e311d8a90a01000a0000ffff00000064000002ff"), out var r));
        Assert.Equal((byte)10, r.HighCategory);
        Assert.Equal((byte)0, r.MediumCategory);
        Assert.Null(r.ItemIds);
        Assert.Equal(AuctionPackets.SearchOrder.Default, r.Order);
    }

    [Theory]
    [InlineData("17000000250060e3115c50060100010200ffff0000009600006aff", 1, 2, AuctionPackets.SearchOrder.InstantPriceDesc)]
    [InlineData("17000000250060e31130a4060100060000ffff0000ffff000046ff", 6, 0, AuctionPackets.SearchOrder.StartPriceDesc)]
    
    
    [InlineData("17000000250060e31109d70601000a0000ffff0000ffff000062ff", 10, 0, AuctionPackets.SearchOrder.InstantPriceAsc)]
    public void ArchiveSearches_ParseOrderByte(string hex, int high, int medium, AuctionPackets.SearchOrder order)
    {
        Assert.True(AuctionPackets.TryParseSearch(Hex(hex), out var r));
        Assert.Equal((byte)high, r.HighCategory);
        Assert.Equal((byte)medium, r.MediumCategory);
        Assert.Equal(order, r.Order);
    }

    [Fact]
    public void WrongTagOrTruncatedIdList_IsRejected()
    {
        Assert.False(AuctionPackets.TryParseSearch(Hex("17000000250062e311d8a90a01000a0000ffff00000064000002ff"), out _));
        
        Assert.False(AuctionPackets.TryParseSearch(Hex("17000000250060e311d8a90a01000a0000ffff00000064110002ff"), out _));
    }

    [Fact]
    public void EmptyResult_IsTheReferenceFrame()
    {
        var body = AuctionPackets.BuildSearch2456(0x0AA9D811, 1, 0, Array.Empty<byte[]>());
        Assert.Equal("0112120061e311d8a90a01000000000000000000", Convert.ToHexString(body).ToLowerInvariant());
    }

    
    private const string OneRecord =
        "02e400e40061e311d8a90a010001000000000001007d4be0677dd26e0ecf249d11a83d040e816f5401102714670000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000084a107000000000085a107000000000068007500610070006f0031003200330000000000000000000000000000" +
        "00000000000000000000000000630abe88fea7b3b6e00d0000000000000000";

    [Fact]
    public void OneRecord_HeaderAndFramingAreByteExact()
    {
        var reference = Hex(OneRecord);
        var entry = reference.AsSpan(3 + 18, AuctionPackets.EntryLength).ToArray();
        var body = AuctionPackets.BuildSearch2456(0x0AA9D811, 1, 1, new[] { entry });
        Assert.Equal(OneRecord, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void SearchEntry_CarriesTheSellerName()
    {
        
        var now = new DateTime(2026, 9, 22, 1, 57, 28, 300, DateTimeKind.Utc);
        var a = new AuctionArticle
        {
            Uid = 1040000000024660861UL,
            ItemUid = 0x0E043DA8119D24CFUL,
            ItemId = 22310785,
            Count = 20,
            StartPrice = 500100,
            BuyoutPrice = 500101,
            SellerName = "huapo123",
            SellerCharUid = 0x0DE0B6B3A7FE88BEUL,
            ExpiresAt = now.AddMinutes(2659),
        };
        var e = AuctionPackets.BuildEntry210(a, now, withSellerName: true);
        var r = Hex(OneRecord).AsSpan(3 + 18, AuctionPackets.EntryLength).ToArray();
        string Part(byte[] b, int off, int len) => Convert.ToHexString(b, off, len);

        Assert.Equal(AuctionPackets.EntryLength, e.Length);
        Assert.Equal(Part(r, 0, 8), Part(e, 0, 8));       
        Assert.Equal(Part(r, 8, 12), Part(e, 8, 12));     
        Assert.Equal(r[8 + 14], e[8 + 14]);               
        Assert.Equal(AuctionPackets.ItemContainer, e[8 + 15]);
        Assert.Equal(Part(r, 134, 76), Part(e, 134, 76)); 
        Assert.Equal((ushort)2659, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(192)));

        var own = AuctionPackets.BuildEntry210(a, now, withSellerName: false);
        Assert.All(own.AsSpan(150, 42).ToArray(), b => Assert.Equal((byte)0, b));
    }
}
