using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// The play-time reward chain behind requests 1642 and 1643 and responses 2944 and 2945
/// (<see cref="PlayTimeRewardChain"/>, one per account, persisted by <see cref="PlayTimeRewardStore"/>).
/// Expected bodies use the standard packet framing, and the elapsed times fed to the chain are fixed test values.
/// </summary>
public class PlayTimeReward2944Tests
{
 /// <summary>The Type 1 rows of the attendance table (IDs 7 to 10, Time 600, 1800, 3600 and 7200, item 66666666 x1), plus a Type 0 row that must not join the chain.</summary>
    private static readonly Attendance[] Rows =
    {
        new() { ID = 1, Type = 0, Day = 1, Time = 0, Reward_item1 = 60000083, Item_count1 = 5 },
        new() { ID = 7, Type = 1, Time = 600, Reward_item1 = 66666666, Item_count1 = 1 },
        new() { ID = 8, Type = 1, Time = 1800, Reward_item1 = 66666666, Item_count1 = 1 },
        new() { ID = 9, Type = 1, Time = 3600, Reward_item1 = 66666666, Item_count1 = 1 },
        new() { ID = 10, Type = 1, Time = 7200, Reward_item1 = 66666666, Item_count1 = 1 },
    };

    private static readonly Attendance[] Chain = PlayTimeRewardChain.Chain(Rows);

 /// <summary>Reset instant for the test day: 12:00:00 UTC.</summary>
    private static readonly DateTime ResetInstant = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private static PlayTimeRewardChain.ClaimResult ClaimAt(PlayTimeRewardProgress p, uint slot, DateTime login, DateTime now)
    {
        var result = PlayTimeRewardChain.Claim(p, Chain, slot, login, now, _ => true);
        Assert.NotNull(result);
        return result.Value;
    }

    private static (uint, uint) PeekAt(PlayTimeRewardProgress p, DateTime login, DateTime now)
    {
        var answer = PlayTimeRewardChain.Peek(p, Chain, login, now);
        Assert.NotNull(answer);
        return answer.Value;
    }

    private static byte[] Body(PlayTimeRewardChain.ClaimResult c) => PlayTimeRewardChain.BuildAnswer(c.Result, c.Slot, c.Seconds);

    private static byte[] Body((uint Slot, uint Seconds) a) => PlayTimeRewardChain.BuildAnswer(PlayTimeRewardChain.ResultOk, a.Slot, a.Seconds);

    [Fact]
    public void Chain_IsTheTypeOneRowsInIdOrder()
    {
        Assert.Equal(new uint[] { 7, 8, 9, 10 }, Chain.Select(r => (uint)r.ID).ToArray());
    }

    [Fact]
    public void ResetPush_ReproducesRetailsUnrequested2944()
    {
        
        var login = ResetInstant.AddMinutes(-2.5);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        p.PeriodStartUtc = ResetInstant.AddDays(-1);
        p.AccumulatedSeconds = 511; 

        Assert.Equal(Convert.FromHexString("00000700000058020000"), Body(PeekAt(p, login, ResetInstant)));
    }

    [Fact]
    public void AfterTheReset_TheClockCountsFromTheInstant()
    {
        
        
        
        var login = ResetInstant.AddMinutes(-2.5);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        p.PeriodStartUtc = ResetInstant.AddDays(-1);

        Assert.Equal((7u, 600u), PeekAt(p, login, ResetInstant));
        Assert.Equal(Convert.FromHexString("000007000000fb000000"), Body(PeekAt(p, login, ResetInstant.AddSeconds(349.284))));
        Assert.Equal(Convert.FromHexString("0000070000000a000000"), Body(PeekAt(p, login, ResetInstant.AddSeconds(590.588))));
    }

    [Fact]
    public void GrantedClaim_ReproducesRetails2945()
    {
        
        var login = ResetInstant.AddHours(1);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        var claim = ClaimAt(p, 7, login, login.AddSeconds(650));

        Assert.Equal(Convert.FromHexString("00000800000008070000"), Body(claim));
        Assert.Equal((byte)7, claim.GrantedRow!.ID);
        Assert.Equal((8u, 1800u), PeekAt(p, login, login.AddSeconds(650)));
    }

    [Fact]
    public void EarlyClaim_Is31004WithTheCurrentSlotAndItsRemainingTime()
    {
        
        var login = ResetInstant.AddHours(1);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        var claim = ClaimAt(p, 7, login, login.AddSeconds(340));

        Assert.Equal(Convert.FromHexString("1c790700000004010000"), Body(claim));
        Assert.Null(claim.GrantedRow);

        
        var slotZero = ClaimAt(p, 0, login, login.AddSeconds(340));
        Assert.Equal((PlayTimeRewardChain.ResultNotYet, 7u, 260u), (slotZero.Result, slotZero.Slot, slotZero.Seconds));
    }

    [Fact]
    public void LastSlotClaim_Is31002_AndTheChainStartsAgainAtTheFirstRow()
    {
        
        
        var login = ResetInstant.AddHours(1);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        p.Index = 3; 
        var claimAt = login.AddSeconds(7200);
        var claim = ClaimAt(p, 10, login, claimAt);

        Assert.Equal(Convert.FromHexString("1a790000000000000000"), Body(claim));
        Assert.Equal((byte)10, claim.GrantedRow!.ID);
        Assert.Equal((7u, 600u), PeekAt(p, login, claimAt));

        var again = ClaimAt(p, 7, login, claimAt.AddSeconds(600));
        Assert.Equal(Convert.FromHexString("00000800000008070000"), Body(again));
    }

    [Fact]
    public void OfflineTime_IsNotCounted()
    {
        var firstLogin = ResetInstant.AddHours(1);
        var p = PlayTimeRewardChain.NewProgress(firstLogin, firstLogin);
        Assert.Equal((7u, 500u), PeekAt(p, firstLogin, firstLogin.AddSeconds(100)));

        
        var secondLogin = firstLogin.AddHours(3);
        Assert.Equal((7u, 497u), PeekAt(p, secondLogin, secondLogin.AddSeconds(3)));
    }

 /// <summary>Converts a local time on the UTC-7 test host to UTC. 05:00 local is the reset instant.</summary>
    private static DateTime Local(int h, int m, int s, int ms) =>
        ResetInstant + (new TimeSpan(0, h, m, s, ms) - TimeSpan.FromHours(5));

    [Fact]
    public void CharacterSwitch_ContinuesTheAccountsCountdown()
    {
        
        
        
        
        var dualLogin = Local(6, 27, 31, 463);
        var p = PlayTimeRewardChain.NewProgress(dualLogin, dualLogin);
        p.AccumulatedSeconds = 600;
        var claim = ClaimAt(p, 7, dualLogin, Local(6, 29, 42, 466));
        Assert.Equal(Convert.FromHexString("00000800000008070000"), Body(claim));

        _ = PeekAt(p, dualLogin, Local(6, 30, 9, 870)); 

        var yolinesLogin = Local(6, 30, 12, 320);
        Assert.Equal(Convert.FromHexString("000008000000ea060000"), Body(PeekAt(p, yolinesLogin, Local(6, 30, 15, 856))));

        
        var again = ClaimAt(p, 7, yolinesLogin, Local(6, 30, 16, 0));
        Assert.Equal((PlayTimeRewardChain.ResultNotYet, 8u), (again.Result, again.Slot));
        Assert.Null(again.GrantedRow);
    }

    [Fact]
    public void ReturningCharacter_SeesWhatTheAccountPlayedMeanwhile()
    {
        
        
        
        
        var dualLogin = Local(4, 57, 35, 773);
        var p = PlayTimeRewardChain.NewProgress(dualLogin, dualLogin);
        p.AccumulatedSeconds = 89; 

        Assert.Equal(Convert.FromHexString("00000700000058020000"), Body(PeekAt(p, dualLogin, ResetInstant)));
        _ = PeekAt(p, dualLogin, Local(5, 47, 49, 362));

        var yolinesLogin = Local(5, 47, 52, 2);
        Assert.Equal(Convert.FromHexString("00000700000000000000"), Body(PeekAt(p, yolinesLogin, Local(5, 47, 55, 423))));
    }

    [Fact]
    public void TwoStintsOfOneAccountInWorldAtOnce_CountWallTimeOnce()
    {
        var aLogin = ResetInstant.AddHours(1);
        var bLogin = aLogin.AddSeconds(10);
        var p = PlayTimeRewardChain.NewProgress(aLogin, aLogin);

        _ = PeekAt(p, aLogin, aLogin.AddSeconds(20));
        _ = PeekAt(p, bLogin, aLogin.AddSeconds(20));
        _ = PeekAt(p, aLogin, aLogin.AddSeconds(30));
        Assert.Equal((7u, 570u), PeekAt(p, bLogin, aLogin.AddSeconds(30)));
    }

    [Fact]
    public void StoredProgress_FromAnEarlierDay_StartsAtTheFirstRow()
    {
        var stored = new PlayTimeRewardDocument
        {
            Id = 42,
            SlotId = 9,
            AccumulatedSeconds = 1234,
            PeriodStartUtc = ResetInstant.AddDays(-1),
        };
        var login = ResetInstant.AddHours(3);
        var p = PlayTimeRewardStore.FromDocument(stored, Chain, login, login);

        Assert.Equal((7u, 597u), PeekAt(p, login, login.AddSeconds(3)));
    }

    [Fact]
    public void StoredProgress_OfTheCurrentDay_ContinuesWhereItStopped()
    {
        var login = ResetInstant.AddHours(3);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        p.Index = 2; 
        _ = PeekAt(p, login, login.AddSeconds(1000));
        var doc = PlayTimeRewardStore.Snapshot(42, p, Chain);
        Assert.Equal(9u, doc.SlotId);

        var relog = login.AddHours(1);
        var restored = PlayTimeRewardStore.FromDocument(doc, Chain, relog, relog);
        Assert.Equal((9u, 2595u), PeekAt(restored, relog, relog.AddSeconds(5)));
    }

    [Fact]
    public void PeriodStart_IsTheLatestNoonUtc()
    {
        Assert.Equal(ResetInstant, PlayTimeRewardChain.PeriodStart(ResetInstant.AddSeconds(2.355)));
        Assert.Equal(ResetInstant, PlayTimeRewardChain.PeriodStart(ResetInstant));
        Assert.Equal(ResetInstant.AddDays(-1), PlayTimeRewardChain.PeriodStart(ResetInstant.AddSeconds(-1)));
        Assert.Equal(ResetInstant.AddDays(1), PlayTimeRewardChain.NextResetAfter(ResetInstant));
    }

    [Fact]
    public void NoPlayTimeRows_AnswersNothing()
    {
        var login = ResetInstant.AddHours(1);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        var none = Array.Empty<Attendance>();

        Assert.Null(PlayTimeRewardChain.Peek(p, none, login, login));
        Assert.Null(PlayTimeRewardChain.Claim(p, none, 7, login, login, _ => true));
    }

    [Fact]
    public void ReadyClaim_WhoseRewardCannotLand_Is31004WithNothingLeft_AndKeepsTheSlot()
    {
        var login = ResetInstant.AddHours(1);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        Attendance? asked = null;
        var refused = PlayTimeRewardChain.Claim(p, Chain, 7, login, login.AddSeconds(650), row => { asked = row; return false; });

        Assert.NotNull(refused);
        Assert.Equal((byte)7, asked!.ID);
        Assert.True(refused.Value.Undeliverable);
        Assert.Null(refused.Value.GrantedRow);
        Assert.Equal(Convert.FromHexString("1c790700000000000000"), Body(refused.Value));

        
        var granted = ClaimAt(p, 7, login, login.AddSeconds(700));
        Assert.Equal(Convert.FromHexString("00000800000008070000"), Body(granted));
    }

    [Fact]
    public void NotReadyClaim_NeverAsksWhetherTheRewardCanLand()
    {
        var login = ResetInstant.AddHours(1);
        var p = PlayTimeRewardChain.NewProgress(login, login);
        bool asked = false;
        var early = PlayTimeRewardChain.Claim(p, Chain, 7, login, login.AddSeconds(340), _ => asked = true);

        Assert.False(asked);
        Assert.Equal(Convert.FromHexString("1c790700000004010000"), Body(early!.Value));
    }

 /// <summary>Item 66666666 (Loki's Dice): Stack_Max 255, Durability 0, from the ItemInfo table.</summary>
    private static readonly Dictionary<uint, ItemData> Items = new()
    {
        [66666666] = new ItemData { ID = 66666666, Stack_Max = 255, Durability = 0 },
    };

    [Fact]
    public void Reward_JoinsTheLowestHeldStackItFits()
    {
        var owned = new List<InventoryItem>
        {
            new() { Id = 5, CharacterId = 1, ItemId = 66666666, Count = 3, Slot = 9, StorageType = 0 },
            new() { Id = 6, CharacterId = 1, ItemId = 66666666, Count = 255, Slot = 2, StorageType = 0 },
            new() { Id = 7, CharacterId = 1, ItemId = 66666666, Count = 1, Slot = 4, StorageType = 0 },
        };
        var plan = PlayTimeRewardGetItemHandler.PlanRewardLandings(owned, 1, Chain[0], Items);

        var landing = Assert.Single(plan!);
        Assert.Same(owned[2], landing.MergeInto); 
        Assert.Equal(2, landing.MergedCount);
        Assert.Empty(landing.NewStacks);
    }

    [Fact]
    public void Reward_WithoutAHeldStack_TakesTheLowestUsableFreeCell()
    {
        var owned = new List<InventoryItem>
        {
            new() { Id = 5, CharacterId = 1, ItemId = 11100001, Count = 1, Slot = 0, StorageType = 0 },
            new() { Id = 6, CharacterId = 1, ItemId = 11100002, Count = 1, Slot = 1, StorageType = 0 },
        };
        var plan = PlayTimeRewardGetItemHandler.PlanRewardLandings(owned, 1, Chain[0], Items);

        var stack = Assert.Single(Assert.Single(plan!).NewStacks);
        Assert.Equal((short)2, stack.Slot);
        Assert.Equal(66666666u, stack.ItemId);
        Assert.Equal(1, stack.Count);
        Assert.Equal((short)0, stack.Durability);
        Assert.Equal((byte)0, stack.StorageType);
    }

    [Fact]
    public void Reward_IntoAFullBag_HasNoLanding()
    {
        
        var owned = Enumerable.Range(0, 20)
            .Select(i => new InventoryItem { Id = (ulong)(i + 1), CharacterId = 1, ItemId = 11100001, Count = 1, Slot = (short)i, StorageType = 0 })
            .ToList();

        Assert.Null(PlayTimeRewardGetItemHandler.PlanRewardLandings(owned, 1, Chain[0], Items));
    }
}
