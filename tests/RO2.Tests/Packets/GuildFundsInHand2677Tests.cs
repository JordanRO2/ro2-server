using System.Text.Json;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Guild vault zeny and title deletion packets: 1487, 1488, 2672, 2673, 2677, 1467, 2632 and 2636.</summary>
/// <remarks>
/// Layouts for 1487, 2672, 1467 and 2632 follow the client reader and have no reference frame.
/// </remarks>
public class GuildFundsInHand2677Tests
{
 /// <summary>Guild GUID and the 200,000,000 amount used by the 1488 test vectors.</summary>
    private const string Dump1488 = "31451c46dada3547b1f1a3158007cfd400c2eb0b00000000";
    private static readonly byte[] DumpGuid = Convert.FromHexString("31451c46dada3547b1f1a3158007cfd4");

    private const long TenBillion = 10_000_000_000;

 /// <summary>Locates the repository's data/ folder by walking up from the test binaries.</summary>
    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Guild_Skill_Level.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Guild_Skill_Level.json");
    }

    private static async Task<List<T>> LoadAsync<T>(string file)
    {
        await using var fs = File.OpenRead(Path.Combine(DataDir(), file));
        return (await JsonSerializer.DeserializeAsync<List<T>>(fs))!;
    }

    
    [Fact]
    public void Request1488_ReadsTheDumpFrame()
    {
        var guild = new Guild { Guid16 = (byte[])DumpGuid.Clone() };

        Assert.True(GuildFundsInHandPackets.TryParseRequest(Convert.FromHexString(Dump1488), guild, out var amount, out var matches));
        Assert.Equal(200_000_000UL, amount);
        Assert.True(matches);
        Assert.Equal(GuildFundsInHandPackets.RequestLength, Convert.FromHexString(Dump1488).Length);

        var other = new Guild { Guid16 = new byte[16] };
        Assert.True(GuildFundsInHandPackets.TryParseRequest(Convert.FromHexString(Dump1488), other, out _, out matches));
        Assert.False(matches);

        Assert.False(GuildFundsInHandPackets.TryParseRequest(Convert.FromHexString(Dump1488)[..23], guild, out _, out _));
    }

    
    [Theory]
    
    [InlineData(467_254_901L, "000075bed91b00000000")]
    [InlineData(667_254_901L, "00007580c52700000000")]
    
    [InlineData(6_999_999_142L, "0000a6823ba101000000")]
    public void Withdraw2673_Success_ReproducesDumpFrames(long walletAfter, string dumpHex)
    {
        var body = GuildFundsInHandPackets.BuildAnsWithdraw2673(GuildFundsInHandPackets.ResultOk, walletAfter);
        Assert.Equal(dumpHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void Withdraw2673_Refusal_CarriesNoWallet()
    {
        
        
        var body = GuildFundsInHandPackets.BuildAnsWithdraw2673(GuildFundsInHandPackets.ResultWalletLimit, 9_867_250_497);
        Assert.Equal("27000000000000000000", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void Funds2677_ReproducesDumpFrames()
    {
        
        Assert.Equal("0000000000000000", Convert.ToHexString(GuildFundsInHandPackets.BuildNfyFundsInHand2677(0)).ToLowerInvariant());
    }

    [Fact]
    public void Funds2677_IsOneU64_PerTheClientReader()
    {
        
        
        Assert.Equal("00c2eb0b00000000", Convert.ToHexString(GuildFundsInHandPackets.BuildNfyFundsInHand2677(200_000_000)).ToLowerInvariant());
        Assert.Equal("0000000000000000", Convert.ToHexString(GuildFundsInHandPackets.BuildNfyFundsInHand2677(-5)).ToLowerInvariant());
    }

    [Fact]
    public void Deposit2672_IsResultThenWallet_PerTheClientReader()
    {
        
        
        Assert.Equal("000075bed91b00000000",
            Convert.ToHexString(GuildFundsInHandPackets.BuildAnsDeposit2672(GuildFundsInHandPackets.ResultOk, 467_254_901)).ToLowerInvariant());
        Assert.Equal("21000000000000000000",
            Convert.ToHexString(GuildFundsInHandPackets.BuildAnsDeposit2672(GuildFundsInHandPackets.ResultStorageLimit, 467_254_901)).ToLowerInvariant());
        Assert.Equal("2e000000000000000000",
            Convert.ToHexString(GuildFundsInHandPackets.BuildAnsDeposit2672(GuildFundsInHandPackets.ResultWalletShort, 5)).ToLowerInvariant());
    }

    
    [Theory]
    
    [InlineData(9_867_250_497L, 200_000_000UL, GuildFundsInHandPackets.ResultWalletLimit)]
    
    [InlineData(9_800_000_000L, 200_000_000UL, GuildFundsInHandPackets.ResultOk)]
    
    [InlineData(TenBillion, 200_000_000UL, GuildFundsInHandPackets.ResultWalletLimit)]
    
    [InlineData(9_867_250_497L, 99_999_999_000UL, GuildFundsInHandPackets.ResultWalletLimit)]
    [InlineData(0L, ulong.MaxValue, GuildFundsInHandPackets.ResultWalletLimit)]
    public void WithdrawWalletRule_MatchesTheDump(long wallet, ulong amount, ushort expected)
        => Assert.Equal(expected, GuildFundsInHandPackets.WithdrawWalletRefusal(wallet, amount, TenBillion));

    [Fact]
    public async Task WalletCap_IsConfigDef91()
    {
        var config = await LoadAsync<ConfigDef>("Config_Def.json");
        Assert.Equal(TenBillion, GuildFundsInHandPackets.WalletCap(config));
        Assert.Equal(long.MaxValue, GuildFundsInHandPackets.WalletCap([]));
    }

    [Fact]
    public void DepositWalletRule_RefusesMoreThanTheWallet()
    {
        Assert.Equal(GuildFundsInHandPackets.ResultOk, GuildFundsInHandPackets.DepositWalletRefusal(1_000, 1_000));
        Assert.Equal(GuildFundsInHandPackets.ResultWalletShort, GuildFundsInHandPackets.DepositWalletRefusal(1_000, 1_001));
        Assert.Equal(GuildFundsInHandPackets.ResultWalletShort, GuildFundsInHandPackets.DepositWalletRefusal(1_000, ulong.MaxValue));
    }

    [Fact]
    public async Task StorageLimit_IsTheLearnedProsperityLevel()
    {
        
        var table = GuildSkillLearnPackets.Index(await LoadAsync<GuildSkillLevel>("Guild_Skill_Level.json"));

        Assert.Equal(0, GuildFundsInHandPackets.StorageLimit([], table));
        Assert.Equal(10_000_000, GuildFundsInHandPackets.StorageLimit([120401], table));
        Assert.Equal(50_000_000, GuildFundsInHandPackets.StorageLimit([120401, 120402, 120403], table));
        Assert.Equal(200_000_000, GuildFundsInHandPackets.StorageLimit([120501, 120601, 120405], table));
        Assert.Equal(0, GuildFundsInHandPackets.StorageLimit([120501, 120601], table));
    }

    [Fact]
    public void Funds_AddStopsAtTheLimit_TakeStopsAtZero()
    {
        var g = new Guild { Gold = 150_000_000 };

        Assert.False(GuildService.TryAddFunds(g, 50_000_001, 200_000_000, out var after));
        Assert.Equal(150_000_000, after);
        Assert.True(GuildService.TryAddFunds(g, 50_000_000, 200_000_000, out after));
        Assert.Equal(200_000_000, after);

        Assert.False(GuildService.TryTakeFunds(g, 200_000_001, out after));
        Assert.Equal(200_000_000, after);
        
        Assert.True(GuildService.TryTakeFunds(g, 200_000_000, out after));
        Assert.Equal(0, after);
        Assert.Equal(0, g.Gold);
    }

    
    private static Guild GuildWithTitles(params byte[] indices)
    {
        var g = new Guild { MasterCharId = 1 };
        foreach (var i in indices) g.Ranks.Add(new GuildRank { Index = i, Name = "t" + i });
        g.Members.Add(new GuildMember { CharId = 1, AuthId = GuildService.MasterRankIndex });
        return g;
    }

    [Fact]
    public void DeleteTitle_RemovesAnUnusedTitle()
    {
        var g = GuildWithTitles(0, 1, 9);
        g.Members.Add(new GuildMember { CharId = 2, AuthId = 9 });

        Assert.Equal(GuildService.DeleteRankRefusal.None, GuildService.TryDeleteRank(g, 1, 1));
        Assert.Equal([0, 9], g.Ranks.Select(r => (int)r.Index));
    }

    [Theory]
    [InlineData(2u, (byte)1, GuildService.DeleteRankRefusal.NotMaster)]
    [InlineData(1u, (byte)0, GuildService.DeleteRankRefusal.MasterTitle)]
    [InlineData(1u, (byte)10, GuildService.DeleteRankRefusal.OutOfRange)]
    [InlineData(1u, (byte)5, GuildService.DeleteRankRefusal.NoSuchTitle)]
    [InlineData(1u, (byte)9, GuildService.DeleteRankRefusal.TitleInUse)]
    public void DeleteTitle_Refusals_LeaveTheTable(uint requester, byte index, GuildService.DeleteRankRefusal expected)
    {
        var g = GuildWithTitles(0, 1, 9);
        g.Members.Add(new GuildMember { CharId = 2, AuthId = 9 });

        Assert.Equal(expected, GuildService.TryDeleteRank(g, requester, index));
        Assert.Equal(3, g.Ranks.Count);
    }

    [Fact]
    public void DeleteTitle_KeepsTwoTitles()
    {
        var g = GuildWithTitles(0, 1);
        Assert.Equal(GuildService.DeleteRankRefusal.TooFewTitles, GuildService.TryDeleteRank(g, 1, 1));
        Assert.Equal(2, g.Ranks.Count);
    }

    [Fact]
    public void DeleteTitle_CodesAreTheClientsMessages()
    {
        
        
        Assert.Equal(0, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.None));
        Assert.Equal(7, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.NotRegistered));
        Assert.Equal(17, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.NotMaster));
        Assert.Equal(18, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.TitleInUse));
        Assert.Equal(21, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.OutOfRange));
        Assert.Equal(22, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.TooFewTitles));
        Assert.Equal(25, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.MasterTitle));
        Assert.Equal(37, GuildDelAuthorityHandler.ResultOf(GuildService.DeleteRankRefusal.NoSuchTitle));
    }

    [Fact]
    public void DelTitle2636_ReproducesTheDumpFrame()
    {
        
        Assert.Equal("01", Convert.ToHexString(GuildDelAuthorityHandler.BuildNfyGuildDelAuthority2636(1)).ToLowerInvariant());
    }

    [Fact]
    public void DelTitle2632_IsOneU16_PerTheClientReader()
    {
        
        Assert.Equal("0000", Convert.ToHexString(GuildDelAuthorityHandler.BuildAnsGuildDelAuthority2632(0)).ToLowerInvariant());
        Assert.Equal("1200", Convert.ToHexString(GuildDelAuthorityHandler.BuildAnsGuildDelAuthority2632(18)).ToLowerInvariant());
    }
}
