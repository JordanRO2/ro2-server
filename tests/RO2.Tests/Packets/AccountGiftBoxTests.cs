using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the account gift box family: 2799 gift pages, 2798 and 2821 counts, and 2800 claim answers.
/// </summary>
public class AccountGiftBoxTests
{
    private const uint Account = 0x49DF5;
    private const ulong Gid = 0x0DE0B6B3A7B17CA8;

 /// <summary>Page 1 with 11 gifts lists the newest 7.</summary>
    private const string RetailPage1Of11 =
        "3f010000f59d0400a87cb1a7b3b6e00d01000b0007006fc1d3a9e2fc2f4699c7147b2b3f1e59e3879303010061ff3aad069ae64000000000069ae6400703000000821b2520c556384aa1ea1e2748cb0e9dda8893030300d5506937009ae64000000000009ae6400704000000bebf8aa03666ef468fedccca5f148e083a87930301004eb64672e299e64000000000e299e6400604000000a6d999fff0245c49b2f891a10bf30f4cb0f4fa0201003710e059ad99e64000000000ad99e6400404000000167b5105cc6c5047ba136365437ae137098893030100853fafb0a699e64000000000a699e64004030000009eeb21b5bc2afa4a90ccd78622584fdfb8202c0401009ab0aeb0a699e64000000000a699e64004030000006892e4430332e743b32b603c5b2d68d30988930301003becabb0a699e64000000000a699e6400403000000";

 /// <summary>Next day, page 1 again: a new gift is listed first and older gifts are one day closer to expiry.</summary>
    private const string RetailPage1Of12NextDay =
        "3f010000f59d0400a87cb1a7b3b6e00d01000c0007001ce59d206e62494ba1d8ed85eb7374f6e6889303030047d5269f229ae64000000000229ae64007040000006fc1d3a9e2fc2f4699c7147b2b3f1e59e3879303010061ff3aad069ae64000000000069ae6400603000000821b2520c556384aa1ea1e2748cb0e9dda8893030300d5506937009ae64000000000009ae6400604000000bebf8aa03666ef468fedccca5f148e083a87930301004eb64672e299e64000000000e299e6400504000000a6d999fff0245c49b2f891a10bf30f4cb0f4fa0201003710e059ad99e64000000000ad99e6400304000000167b5105cc6c5047ba136365437ae137098893030100853fafb0a699e64000000000a699e64003030000009eeb21b5bc2afa4a90ccd78622584fdfb8202c0401009ab0aeb0a699e64000000000a699e6400303000000";

 /// <summary>A gift as the store keeps it: GUID, item, count, source and filing time.</summary>
    private static List<AccountGift> GiftsOf(string retailPage)
    {
        var body = Convert.FromHexString(retailPage);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20));
        var gifts = new List<AccountGift>(n);
        for (int i = 0; i < n; i++)
        {
            var r = body.AsSpan(22 + 43 * i, 43);
            gifts.Add(new AccountGift
            {
                Guid = Convert.ToHexString(r[..16]),
                ItemId = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]),
                Count = BinaryPrimitives.ReadUInt16LittleEndian(r[20..]),
                ReceivedUtc = DateTime.FromOADate(BinaryPrimitives.ReadDoubleLittleEndian(r[22..])).ToUniversalTime(),
                Source = r[39],
            });
        }
        return gifts;
    }

 /// <summary>Gifts older than every listed one are not shown on page 1, only counted in the total.</summary>
    private static IEnumerable<AccountGift> Older(int count, DateTime beforeLocal)
        => Enumerable.Range(1, count).Select(i => AccountGiftStore.NewGift(60000265, 1, 3,
            beforeLocal.AddMinutes(-i).ToUniversalTime()));

    [Fact]
    public void Page1IsRetailsNewestSevenOfEleven()
    {
        var listed = GiftsOf(RetailPage1Of11);
        var now = new DateTime(2026, 9, 23, 7, 45, 38);
        var box = AccountGiftStore.NewestFirst(listed.Concat(Older(4, AccountGiftStore.ToLocal(listed[^1].ReceivedUtc))), now);

        Assert.Equal(Convert.FromHexString(RetailPage1Of11), AccountGiftPackets.Build2799(Account, Gid, 1, box, now));
    }

    [Fact]
    public void ANewDayCountsEveryGiftOneDayDown()
    {
        var listed = GiftsOf(RetailPage1Of12NextDay);
        var now = new DateTime(2026, 9, 24, 2, 6, 19);
        var box = AccountGiftStore.NewestFirst(listed.Concat(Older(5, AccountGiftStore.ToLocal(listed[^1].ReceivedUtc))), now);

        Assert.Equal(Convert.FromHexString(RetailPage1Of12NextDay), AccountGiftPackets.Build2799(Account, Gid, 1, box, now));
    }

    [Fact]
    public void AnEmptyBoxIsRetailsEmptyPage()
    {
        
        var body = AccountGiftPackets.Build2799(0x0019A124, 0x6F293C8FAB56AB4C, 0, new List<AccountGift>(),
            new DateTime(2026, 7, 2, 20, 0, 0));

        Assert.Equal(Convert.FromHexString("1200000024a119004cab56ab8f3c296f000000000000"), body);
    }

 /// <summary>Page P lists gifts from index 5 * (P - 1), floored at 0, up to 7 per page.</summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(4, 0, 0, 2)]
    [InlineData(11, 1, 0, 7)]
    [InlineData(13, 2, 5, 7)]
    [InlineData(26, 6, 25, 1)]
    public void ThePageWindowIsRetails(int total, int page, int start, int count)
        => Assert.Equal((start, count), AccountGiftStore.PageWindow(total, (ushort)page));

    [Fact]
    public void AGiftLastsSevenDaysAfterItsFilingDay()
    {
        var filed = new DateTime(2026, 9, 20, 23, 59, 0);

        Assert.Equal(7, AccountGiftStore.DaysLeft(filed, new DateTime(2026, 9, 20, 23, 59, 30)));
        Assert.Equal(6, AccountGiftStore.DaysLeft(filed, new DateTime(2026, 9, 21, 0, 0, 1)));
        Assert.Equal(0, AccountGiftStore.DaysLeft(filed, new DateTime(2026, 9, 27, 12, 0, 0)));
        var gift = AccountGiftStore.NewGift(66666666, 1, AccountGiftStore.SourcePlayTime, filed.ToUniversalTime());
        Assert.True(AccountGiftStore.IsLive(gift, new DateTime(2026, 9, 27, 23, 0, 0)));
        Assert.False(AccountGiftStore.IsLive(gift, new DateTime(2026, 9, 28, 0, 0, 1)));
    }

 /// <summary>The filing time is stored rounded down to the 45-minute slot after midnight.</summary>
    [Theory]
    [InlineData(20, 2, 11, 19, 30)]
    [InlineData(0, 9, 44, 0, 0)]
    [InlineData(10, 0, 47, 9, 45)]
    public void TheSlotTimeIsTheFilingTimeFlooredTo45Minutes(int h, int m, int s, int fh, int fm)
        => Assert.Equal(new DateTime(2026, 9, 23, fh, fm, 0),
            AccountGiftPackets.FloorTo45Minutes(new DateTime(2026, 9, 23, h, m, s)));

    [Fact]
    public void TheCountsAreBareIntegers()
    {
        Assert.Equal(Convert.FromHexString("0c000000"), AccountGiftPackets.Build2798(12));   
        Assert.Equal(Convert.FromHexString("0b00"), AccountGiftPackets.Build2821(11));       
        Assert.Equal(Convert.FromHexString("0000"), AccountGiftPackets.Build2821(0));
    }

 /// <summary>A single gift that lands on an existing stack of one.</summary>
    private const string RetailClaim =
        "24a11900000000004cab56ab8f3c296f7d66eaa9fc18384d81fa66629b70046baa40f90301000000000000000000000000000000000000010000001ae4840da83d040eaa40f903000001041300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

 /// <summary>A claim refused with result 3; the record is zeroed.</summary>
    private const string RetailBagFull =
        "3be24c000300000081d9e7a7b3b6e00d3e66bb9ab70d7244b5bc37a982a56af2e68893030400000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void AClaimCarriesTheBagRecordOfTheStackItLandedIn()
    {
        var retail = Convert.FromHexString(RetailClaim);

        var body = AccountGiftPackets.Build2800(0x0019A124, AccountGiftPackets.ClaimOk, 0x6F293C8FAB56AB4C,
            retail.AsSpan(16, 16), 66666666, 1, AccountGiftStore.SourcePlayTime, retail[59..]);

        Assert.Equal(retail, body);
    }

    [Fact]
    public void ARefusalKeepsTheGiftAndZeroesTheRecord()
    {
        var retail = Convert.FromHexString(RetailBagFull);

        var body = AccountGiftPackets.Build2800(0x004CE23B, AccountGiftPackets.ClaimBagFull, 0x0DE0B6B3A7E7D981,
            retail.AsSpan(16, 16), 60000486, 4, 4, record126: null);

        Assert.Equal(retail, body);
    }
}
