using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Siege arm skill cooldown, kept per arm and per CoolTime_Group in SiegeService. Covers the refusal (2943) to a
/// request (1641) made inside a running window, and the 2937 seat answer that lists the arm's running windows.
/// Each test uses its own arm handle, since SiegeService is process-wide static state.
/// </summary>
public class SiegeArmsCooldownTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 7, 8, 42, 887, DateTimeKind.Utc);

    [Fact]
    public void Seat2937_WithTheArmsCooldown_ReproducesRetail_Mortar()
    {
        
        var entries = new[] { new SiegeService.ArmCooldownEntry(5050501, 1955, 5000) };
        Assert.Equal(Convert.FromHexString("14000000000013e24202010085104d00a307000088130000"),
            SiegePackets.BuildSiegeArmsUseStart2937(0x0242E213u, entries));
    }

    [Fact]
    public void Seat2937_WithTheArmsCooldown_ReproducesRetail_Charge()
    {
        
        var entries = new[] { new SiegeService.ArmCooldownEntry(5050201, 1777, 6000) };
        Assert.Equal(Convert.FromHexString("14000000000013ed5f000100590f4d00f106000070170000"),
            SiegePackets.BuildSiegeArmsUseStart2937(0x005FED13u, entries));
    }

    [Fact]
    public void Seat2937_WithNoCooldown_IsTheTwelveByteSuccess()
    {
        
        var bytes = SiegePackets.BuildSiegeArmsUseStart2937(0x0279C313u, Array.Empty<SiegeService.ArmCooldownEntry>());
        Assert.Equal(Convert.FromHexString("08000000000013c379020000"), bytes);
        Assert.Equal(EconomyMiscPackets.BuildSiegeArmsUseStartAck(0x0279C313u), bytes);
    }

    [Fact]
    public void Refusal2943_ReproducesRetail()
    {
        
        Assert.Equal(Convert.FromHexString("404e0000000000000000"),
            SiegePackets.BuildSiegeArmsUseSkillRefusal2943(SiegePackets.ResultArmSkillCoolingDown));
    }

    [Fact]
    public void Success2943_StillReproducesRetail()
    {
        
        Assert.Equal(Convert.FromHexString("000085104d000000a040"),
            EconomyMiscPackets.BuildSiegeArmsUseSkillAck(5050501, 5.0f));
    }

    [Fact]
    public void CooldownGroup_IsSharedByTheMortarSkills_AndFallsBackToTheSkillGroup()
    {
        var skills = LoadSkillTable();
        Assert.Equal(50504u, SiegeService.CooldownGroupOf(5050401, skills));
        Assert.Equal(50504u, SiegeService.CooldownGroupOf(5050501, skills));
        Assert.Equal(50504u, SiegeService.CooldownGroupOf(5050601, skills));
        
        Assert.Equal(50502u, SiegeService.CooldownGroupOf(5050201, skills));
        
        Assert.Equal(99999u, SiegeService.CooldownGroupOf(9999901, skills));
    }

    [Fact]
    public void ArmCooldown_RefusesEveryMortarSkillInsideTheWindow_AndARefusalDoesNotRestartIt()
    {
        
        
        const uint arm = 0x7A0B7613;
        Assert.True(SiegeService.TryStartArmCooldown(arm, 50504, 5050501, 5000, T0, out _));
        Assert.False(SiegeService.TryStartArmCooldown(arm, 50504, 5050501, 5000, T0.AddMilliseconds(143), out var r1));
        Assert.False(SiegeService.TryStartArmCooldown(arm, 50504, 5050401, 5000, T0.AddMilliseconds(1332), out var r2));

        
        Assert.Equal(new SiegeService.ArmCooldown(5050501, T0.AddMilliseconds(5000), 5000), r1);
        Assert.Equal(r1, r2);

        
        Assert.True(SiegeService.TryStartArmCooldown(arm, 50502, 5050201, 6000, T0.AddMilliseconds(1500), out _));

        
        Assert.False(SiegeService.TryStartArmCooldown(arm, 50504, 5050401, 5000, T0.AddMilliseconds(4999), out _));
        Assert.True(SiegeService.TryStartArmCooldown(arm, 50504, 5050401, 5000, T0.AddMilliseconds(5000), out var reopened));
        Assert.Equal(5050401u, reopened.SkillId);
    }

    [Fact]
    public void SeatAnswer_ListsTheArmsRunningWindow_WithTheTimeLeftRoundedUp()
    {
        
        
        
        const uint arm = 0x0242E213 ^ 0x7F000000;
        Assert.True(SiegeService.TryStartArmCooldown(arm, 50504, 5050501, 5000, T0, out _));
        var entries = SiegeService.ActiveCooldowns(arm, T0.AddTicks(30_445_000)); 
        Assert.Equal(new[] { new SiegeService.ArmCooldownEntry(5050501, 1956, 5000) }, entries);
        entries = SiegeService.ActiveCooldowns(arm, T0.AddMilliseconds(3045));
        Assert.Equal(new[] { new SiegeService.ArmCooldownEntry(5050501, 1955, 5000) }, entries);
        Assert.Equal(Convert.FromHexString("140000000000" + "13e2427d" + "010085104d00a307000088130000"),
            SiegePackets.BuildSiegeArmsUseStart2937(arm, entries));

        
        Assert.Empty(SiegeService.ActiveCooldowns(arm, T0.AddMilliseconds(5000)));
    }

    [Fact]
    public void Cooldown_BelongsToTheArm_NotToWhoeverFiredIt()
    {
        
        
        
        const uint arm = 0x7A7A0C13;
        const uint shooterSession = 0x7FFF0001, seaterSession = 0x7FFF0002;
        SiegeService.BeginOperate(shooterSession, arm, 0x0A4AC311);
        var shot = new DateTime(2026, 7, 21, 7, 25, 52, 538, DateTimeKind.Utc);
        Assert.True(SiegeService.TryStartArmCooldown(arm, 50504, 5050501, 5000, shot, out _));
        SiegeService.EndOperate(shooterSession);

        var seat = shot.AddMilliseconds(1820);
        Assert.Equal(new[] { new SiegeService.ArmCooldownEntry(5050501, 3180, 5000) },
            SiegeService.ActiveCooldowns(arm, seat));
        SiegeService.BeginOperate(seaterSession, arm, 0x0A4C2411);
        Assert.False(SiegeService.TryStartArmCooldown(arm, 50504, 5050401, 5000, seat.AddMilliseconds(100), out _));
        SiegeService.EndOperate(seaterSession);
    }

    [Fact]
    public void AnArmNeverSeen_HasNoCooldown()
    {
        Assert.Empty(SiegeService.ActiveCooldowns(0x7EEEEE13, T0));
    }

 /// <summary>Loads the Skill_Table_Base rows (Skill_Group to CoolTime_Group) from the data directory.</summary>
    private static Dictionary<uint, SkillData> LoadSkillTable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "Skill_Table_Base.json");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = dir.Parent;
        }
        Assert.True(path != null, "no ancestor of " + AppContext.BaseDirectory + " holds data/Skill_Table_Base.json");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path!));
        var skills = new Dictionary<uint, SkillData>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            uint group = row.GetProperty("Skill_Group").GetUInt32();
            uint coolTimeGroup = row.TryGetProperty("CoolTime_Group", out var ctg) && ctg.ValueKind == JsonValueKind.Number
                ? ctg.GetUInt32() : 0u;
            skills.TryAdd(group, new SkillData { Skill_Group = group, CoolTime_Group = coolTimeGroup });
        }
        return skills;
    }
}

/// <summary>
/// Auction sell start: the 1403 request and its 2458 answer, covering the refusal, the registration fee and the
/// listing rules (AuctionSellStartRules.cs).
/// </summary>
public class AuctionSellStart2458Tests
{
    
    
    private const string DoubleSent1403 = "0128280066e3116baf014474ac0ca83d040e1d03ffa800e1f5050000000000e1f50500000000131f0000";
    private const uint Seller = 0x01AF6B11;
    private const long WalletAfterTheFirst = 7_400_713_249;

    [Fact]
    public void Refusal_ReproducesRetail()
    {
        
        var expected = Convert.FromHexString("02ef00ef0067e3116baf01b03f" + new string('0', 420) + "21ec1db901000000" + new string('0', 22));
        Assert.Equal(expected, AuctionPackets.BuildSellStartRefusal2458(Seller, AuctionPackets.ResultSellItemMismatch, WalletAfterTheFirst));
    }

    [Fact]
    public void TheDoubleSentCopy_IsRefusedAsAnItemNoLongerInTheBag()
    {
        Assert.True(AuctionPackets.TryParseSellStart(Convert.FromHexString(DoubleSent1403), out var req));
        Assert.Equal(0x0E043DA80CAC7444ul, req.ItemUid);
        Assert.Equal((byte)255, req.Count);
        Assert.Equal((byte)168, req.Hours);
        Assert.Equal(100_000_000ul, req.StartPrice);
        Assert.Equal(100_000_000ul, req.BuyoutPrice);

        var stack = new InventoryItem { Id = req.ItemUid, CharacterId = 7, ItemId = 22300201, Count = 255, StorageType = 0 };
        
        Assert.Equal((ushort)0, AuctionService.CheckSellStart(req, stack, 7, 10_000_000_000, 1, WalletAfterTheFirst + 1, 0));
        
        Assert.Equal(AuctionPackets.ResultSellItemMismatch,
            AuctionService.CheckSellStart(req, null, 7, 10_000_000_000, 0, WalletAfterTheFirst, 1));
    }

    [Theory]
    
    [InlineData(96_000u, 1, 24, 2_880L)]      
    [InlineData(809u, 255, 168, 30_944L)]     
    [InlineData(2_008u, 2, 168, 602L)]        
    [InlineData(96_000u, 1, 168, 14_400L)]    
    [InlineData(10u, 99, 168, 149L)]          
    [InlineData(100u, 1, 168, 15L)]           
    [InlineData(10u, 8, 168, 12L)]            
    [InlineData(120_000u, 1, 168, 18_000L)]   
    [InlineData(0u, 1, 168, 1L)]              
    [InlineData(100u, 1, 72, 7L)]             
    public void RegistrationFee_IsTheClientsFormula(uint priceBuy, int count, int hours, long fee)
    {
        Assert.Equal(fee, AuctionService.RegistrationFee(new ItemData { Price_Buy = priceBuy }, count, hours));
    }

    [Fact]
    public void RegistrationFeeRate_FollowsTheDurationChoice()
    {
        Assert.Equal(0.03f, AuctionService.RegistrationFeeRate(24));
        Assert.Equal(0.07f, AuctionService.RegistrationFeeRate(72));
        Assert.Equal(0.15f, AuctionService.RegistrationFeeRate(168));
        Assert.Equal(0.03f, AuctionService.RegistrationFeeRate(48)); 
    }

    [Fact]
    public void CheckSellStart_MapsEachClientCheckToItsCode()
    {
        Assert.True(AuctionPackets.TryParseSellStart(Convert.FromHexString(DoubleSent1403), out var ok));
        var item = new InventoryItem { Id = ok.ItemUid, CharacterId = 7, ItemId = 22300201, Count = 255, StorageType = 0 };
        const long cap = 10_000_000_000;
        ushort Check(AuctionPackets.SellStartRequest r, InventoryItem? i = null, long fee = 10, long wallet = 1_000, int live = 0)
            => AuctionService.CheckSellStart(r, i ?? item, 7, cap, fee, wallet, live);

        Assert.Equal((ushort)0, Check(ok));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(ok, new InventoryItem { Id = ok.ItemUid, CharacterId = 8, Count = 255 }));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(ok, new InventoryItem { Id = ok.ItemUid, CharacterId = 7, Count = 255, StorageType = 2 }));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(ok, new InventoryItem { Id = ok.ItemUid, CharacterId = 7, Count = 254 }));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(ok with { Count = 0 }));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(ok with { StartPrice = 0 }));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(ok with { BuyoutPrice = 0 }));
        Assert.Equal(AuctionPackets.ResultSellBuyoutBelowStart, Check(ok with { StartPrice = 100_000_001 }));
        Assert.Equal(AuctionPackets.ResultSellItemMismatch, Check(ok with { BuyoutPrice = 10_000_000_001 }));
        Assert.Equal((ushort)0, Check(ok with { StartPrice = 10_000_000_000, BuyoutPrice = 10_000_000_000 }));
        Assert.Equal(AuctionPackets.ResultNotEnoughMoney, Check(ok, fee: 1_001));
        Assert.Equal((ushort)0, Check(ok, fee: 1_000));
        Assert.Equal(AuctionPackets.ResultSellQuantityLimit, Check(ok, live: AuctionPackets.MaxLiveArticles));
        Assert.Equal((ushort)0, Check(ok, live: AuctionPackets.MaxLiveArticles - 1));
    }
}
