using System.Reflection;
using RO2.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Guilds.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Characterization tests for the pure Guild rules that the Guild carve moves out of GuildService.cs. They pin what the
/// code does today, quirks included, so a move cannot change a rule unnoticed. Guilds here are never registered, so the
/// service's write-through is a no-op and no store is needed.
/// </summary>
public class GuildCarveS1CharacterizationTests
{
    private static GuildService Svc => GuildService.Instance;

    private static GuildRank Rank(byte index, string name, byte perms = 0) => new() { Index = index, Name = name, Permissions = perms };

    private static GuildMember Member(uint charId, byte auth = 1) => new() { CharId = charId, Gid = GuildService.GidFor(charId), AuthId = auth };

    private static T Private<T>(string name, params object?[] args)
        => (T)typeof(GuildService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    // ---- rank table -------------------------------------------------------------------------------------------

    [Fact]
    public void DefaultRanks_AreTheClientStringIds()
    {
        var ranks = Private<List<GuildRank>>("DefaultRanks");

        Assert.Equal(2, ranks.Count);
        Assert.Equal((byte)0, ranks[0].Index);
        Assert.Equal("Master", ranks[0].Name);
        Assert.Equal((byte)0x0F, ranks[0].Permissions);
        Assert.Equal((byte)1, ranks[1].Index);
        Assert.Equal("Member", ranks[1].Name);
        Assert.Equal((byte)0x00, ranks[1].Permissions);
        Assert.Equal((byte)0x01, GuildService.PermissionInvite);
        Assert.Equal((byte)0x02, GuildService.PermissionKick);
        Assert.Equal(2, GuildService.MinRanks);
        Assert.Equal(10, GuildService.MaxRanks);
    }

    [Fact]
    public void HasPermission_MasterHoldsEveryRight_WithoutMemberRowOrRankBits()
    {
        var g = new Guild { MasterCharId = 7, Ranks = { Rank(0, "m", 0x00) } };

        Assert.True(GuildService.HasPermission(g, 7, 0x0F));
        Assert.True(GuildService.HasPermission(g, 7, 0xFF));
        // quirk: an unset guild has MasterCharId 0, so charId 0 passes as master even though it is no member
        Assert.True(GuildService.HasPermission(new Guild(), 0, 0x01));
    }

    [Fact]
    public void HasPermission_MemberNeedsEveryRequestedBit_NonMemberNone()
    {
        var g = new Guild
        {
            MasterCharId = 1,
            Ranks = { Rank(0, "m", 0x0F), Rank(1, "a", 0x03), Rank(2, "b", 0x00) },
            Members = { Member(1, 0), Member(2, 1), Member(3, 2), Member(4, 9) },
        };

        Assert.True(GuildService.HasPermission(g, 2, 0x01));
        Assert.True(GuildService.HasPermission(g, 2, 0x03));
        Assert.False(GuildService.HasPermission(g, 2, 0x07));
        Assert.False(GuildService.HasPermission(g, 2, 0x04));
        Assert.True(GuildService.HasPermission(g, 3, 0x00));     // no bits requested: any ranked member passes
        Assert.False(GuildService.HasPermission(g, 3, 0x01));
        Assert.False(GuildService.HasPermission(g, 4, 0x00));    // member whose index is missing from the table
        Assert.False(GuildService.HasPermission(g, 99, 0x00));   // non-member
    }

    [Fact]
    public void RankTitleOf_EmptyForNonMemberMissingIndexAndMasterWithoutRow()
    {
        var g = new Guild
        {
            MasterCharId = 9,
            Ranks = { Rank(0, "Master"), Rank(1, "Member") },
            Members = { Member(1, 1), Member(2, 6) },
        };

        Assert.Equal("Member", GuildService.RankTitleOf(g, 1));
        Assert.Equal(string.Empty, GuildService.RankTitleOf(g, 2));
        Assert.Equal(string.Empty, GuildService.RankTitleOf(g, 99));
        Assert.Equal(string.Empty, GuildService.RankTitleOf(g, 9));
    }

    [Fact]
    public void LowestRankIndex_IsTheHighestIndexNotTheCount_ZeroWhenEmpty()
    {
        Assert.Equal((byte)0, GuildService.LowestRankIndex(new Guild()));
        Assert.Equal((byte)5, GuildService.LowestRankIndex(new Guild { Ranks = { Rank(0, "a"), Rank(5, "b"), Rank(1, "c") } }));
        Assert.Equal((byte)GuildService.MasterRankIndex, GuildService.LowestRankIndex(new Guild { Ranks = { Rank(0, "a") } }));
    }

    [Fact]
    public void NormalizeRanks_SeedsBothDefaultTitles_OnAnEmptyOrNullTable()
    {
        foreach (var seed in new[] { new Guild(), new Guild { Ranks = null! } })
        {
            Assert.True(Private<bool>("NormalizeRanks", seed));
            Assert.Equal(2, seed.Ranks.Count);
            Assert.Equal((0, "Master", (byte)0x0F), (seed.Ranks[0].Index, seed.Ranks[0].Name, seed.Ranks[0].Permissions));
            Assert.Equal((1, "Member", (byte)0x00), (seed.Ranks[1].Index, seed.Ranks[1].Name, seed.Ranks[1].Permissions));
            Assert.False(Private<bool>("NormalizeRanks", seed));
        }
    }

    [Fact]
    public void NormalizeRanks_InsertsMasterTitleFirst_OrPadsToTwoTitles()
    {
        var noMaster = new Guild { Ranks = { Rank(3, "x", 0x05) } };
        Assert.True(Private<bool>("NormalizeRanks", noMaster));
        Assert.Equal(new byte[] { 0, 3 }, noMaster.Ranks.Select(r => r.Index));   // master first, no member default (already 2)

        var onlyMaster = new Guild { Ranks = { Rank(0, "boss", 0x01) } };
        Assert.True(Private<bool>("NormalizeRanks", onlyMaster));
        Assert.Equal(new[] { "boss", "Member" }, onlyMaster.Ranks.Select(r => r.Name));
        Assert.Equal((byte)1, onlyMaster.Ranks[1].Index);

        var whole = new Guild { Ranks = { Rank(0, "a"), Rank(1, "b") } };
        Assert.False(Private<bool>("NormalizeRanks", whole));
    }

    [Fact]
    public void NormalizeRanks_RederivesMemberRanks()
    {
        var g = new Guild
        {
            MasterCharId = 1,
            Ranks = { Rank(0, "m"), Rank(1, "a"), Rank(5, "z") },
            Members = { Member(1, 4), Member(2, 0), Member(3, 9), Member(4, 1), Member(5, 5) },
        };

        Assert.True(Private<bool>("NormalizeRanks", g));
        Assert.Equal(new byte[] { 0, 5, 5, 1, 5 }, g.Members.Select(m => m.AuthId));   // master->0; rank0 & unknown->lowest
        Assert.False(Private<bool>("NormalizeRanks", g));
    }

    // ---- membership -------------------------------------------------------------------------------------------

    [Fact]
    public void NormalizeGids_SetsEveryMemberToTheCharacterUid()
    {
        var g = new Guild { Members = { new GuildMember { CharId = 11, Gid = 0 }, new GuildMember { CharId = 12, Gid = 5 } } };

        Assert.True(GuildService.NormalizeGids(g));
        Assert.All(g.Members, m => Assert.Equal(CharacterSerializer.GenerateUid(m.CharId), m.Gid));
        Assert.Equal(((ulong)0x0E043DA7 << 32) | 11, g.Members[0].Gid);
        Assert.False(GuildService.NormalizeGids(g));
        Assert.False(GuildService.NormalizeGids(new Guild()));
    }

    [Fact]
    public async Task SetAuth_RefusesMasterRankZeroUnknownGidAndUnknownRank()
    {
        var g = new Guild
        {
            MasterCharId = 1,
            Ranks = { Rank(0, "m"), Rank(1, "a"), Rank(2, "b") },
            Members = { Member(1, 0), Member(2, 1) },
        };
        var master = GuildService.GidFor(1);
        var plain = GuildService.GidFor(2);

        Assert.False(await Svc.SetAuthAsync(g, master, 2));
        Assert.False(await Svc.SetAuthAsync(g, plain, 0));
        Assert.False(await Svc.SetAuthAsync(g, GuildService.GidFor(77), 2));
        Assert.False(await Svc.SetAuthAsync(g, plain, 6));
        Assert.Equal((byte)1, g.Members[1].AuthId);
        Assert.Equal((byte)0, g.Members[0].AuthId);

        Assert.True(await Svc.SetAuthAsync(g, plain, 2));
        Assert.Equal((byte)2, g.Members[1].AuthId);
    }

    // ---- funds ------------------------------------------------------------------------------------------------

    [Fact]
    public void TryAddFunds_RejectsNegativeAndOverLimit_WithoutOverflow()
    {
        var g = new Guild { Gold = 100 };
        Assert.False(GuildService.TryAddFunds(g, -1, 1_000, out var after));
        Assert.Equal(100, after);
        Assert.True(GuildService.TryAddFunds(g, 900, 1_000, out after));      // exactly the limit
        Assert.Equal(1_000, after);
        Assert.False(GuildService.TryAddFunds(g, 1, 1_000, out after));
        Assert.True(GuildService.TryAddFunds(g, 0, 1_000, out after));       // zero at the limit still passes
        Assert.Equal(1_000, g.Gold);

        var big = new Guild { Gold = long.MaxValue - 1 };
        Assert.False(GuildService.TryAddFunds(big, 2, long.MaxValue, out after));
        Assert.Equal(long.MaxValue - 1, after);
        Assert.True(GuildService.TryAddFunds(big, 1, long.MaxValue, out after));
        Assert.Equal(long.MaxValue, after);
        Assert.True(GuildService.TryAddFunds(new Guild(), long.MaxValue, long.MaxValue, out after));
        Assert.Equal(long.MaxValue, after);
    }

    [Fact]
    public void TryAddFunds_RefusesAnyAmount_WhenGoldAlreadyAboveTheLimit()
    {
        var g = new Guild { Gold = 500 };

        Assert.False(GuildService.TryAddFunds(g, 0, 400, out var after));    // a lowered limit locks even a zero deposit
        Assert.Equal(500, after);
        Assert.False(GuildService.TryAddFunds(new Guild(), 0, -1, out after));
    }

    [Fact]
    public void TryTakeFunds_RejectsNegativeAndMoreThanHeld()
    {
        var g = new Guild { Gold = 50 };
        Assert.False(GuildService.TryTakeFunds(g, -1, out var after));
        Assert.Equal(50, after);
        Assert.False(GuildService.TryTakeFunds(g, 51, out after));
        Assert.True(GuildService.TryTakeFunds(g, 50, out after));
        Assert.Equal(0, after);
        Assert.True(GuildService.TryTakeFunds(g, 0, out after));
        Assert.Equal(0, g.Gold);
    }

    [Fact]
    public async Task ReturnFunds_AddsWithoutLimit_AndSaturatesAtLongMax()
    {
        var g = new Guild { Gold = 10 };
        Assert.Equal(15, await Svc.ReturnFundsAsync(g, 5));
        g.Gold = long.MaxValue - 5;
        Assert.Equal(long.MaxValue, await Svc.ReturnFundsAsync(g, 5));
        Assert.Equal(long.MaxValue, await Svc.ReturnFundsAsync(g, 1));
        Assert.Equal(long.MaxValue, await Svc.ReturnFundsAsync(g, long.MaxValue));
        Assert.Equal(long.MaxValue, g.Gold);
    }

    [Fact]
    public async Task ReturnFunds_NegativeAmount_SaturatesUp()
    {
        // quirk: long.MaxValue - negative wraps, so the saturation branch fires and the funds jump to long.MaxValue
        var g = new Guild { Gold = 10 };
        Assert.Equal(long.MaxValue, await Svc.ReturnFundsAsync(g, -1));
    }

    // ---- progression ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddExp_ReturnsBeforeAfter_AndSaturatesAtLongMax()
    {
        const ulong Cap = long.MaxValue;
        var g = new Guild { Exp = 10 };
        Assert.Equal((10UL, 15UL), await Svc.AddExpAsync(g, 5));
        Assert.Equal((15UL, 15UL), await Svc.AddExpAsync(g, 0));

        g.Exp = Cap - 3;
        Assert.Equal((Cap - 3, Cap), await Svc.AddExpAsync(g, 10));
        Assert.Equal((Cap, Cap), await Svc.AddExpAsync(g, 1));
        g.Exp = 1;
        Assert.Equal((1UL, Cap), await Svc.AddExpAsync(g, ulong.MaxValue));

        // quirk: a stored value above the cap is pulled down to the cap by the next credit
        g.Exp = ulong.MaxValue;
        Assert.Equal((ulong.MaxValue, Cap), await Svc.AddExpAsync(g, 0));
    }

    [Fact]
    public async Task CreditMemberExp_CreditsGuildAndMember_SaturatingBoth()
    {
        const ulong Cap = long.MaxValue;
        var g = new Guild { Exp = Cap - 1, Members = { Member(1), Member(2) } };
        g.Members[0].ContribPoints = long.MaxValue - 2;

        Assert.Equal((Cap - 1, Cap), await Svc.CreditMemberExpAsync(g, 1, 10));
        Assert.Equal(long.MaxValue, g.Members[0].ContribPoints);
        Assert.Equal(0, g.Members[1].ContribPoints);

        Assert.Equal((Cap, Cap), await Svc.CreditMemberExpAsync(g, 2, ulong.MaxValue));
        Assert.Equal(long.MaxValue, g.Members[1].ContribPoints);   // credit clamped to long.MaxValue

        var solo = new Guild { Exp = 0, Members = { Member(1) } };
        Assert.Equal((0UL, 40UL), await Svc.CreditMemberExpAsync(solo, 99, 40));   // left meanwhile: guild only
        Assert.Equal(0, solo.Members[0].ContribPoints);
        Assert.Equal((40UL, 40UL), await Svc.CreditMemberExpAsync(solo, 1, 0));
        Assert.Equal(0, solo.Members[0].ContribPoints);
    }

    [Fact]
    public async Task SetBribePercent_CapsAtFifty_NullForNonMember()
    {
        var g = new Guild { Members = { Member(1) } };

        Assert.Equal((byte)50, GuildService.MaxBribePercent);
        Assert.Equal((byte?)30, await Svc.SetBribePercentAsync(g, 1, 30));
        Assert.Equal((byte?)50, await Svc.SetBribePercentAsync(g, 1, 50));
        Assert.Equal((byte?)50, await Svc.SetBribePercentAsync(g, 1, 51));
        Assert.Equal((byte?)50, await Svc.SetBribePercentAsync(g, 1, 255));
        Assert.Equal((byte)50, GuildService.BribePercentOf(g, 1));
        Assert.Equal((byte?)0, await Svc.SetBribePercentAsync(g, 1, 0));
        Assert.Null(await Svc.SetBribePercentAsync(g, 2, 10));
        Assert.Equal((byte)0, GuildService.BribePercentOf(g, 2));
    }

    [Fact]
    public void LevelUpBribeAmount_IsExpOverOneHundredRoundedDown()
    {
        Assert.Equal(0UL, GuildBribePackets.LevelUpBribeAmount(0));
        Assert.Equal(0UL, GuildBribePackets.LevelUpBribeAmount(99));
        Assert.Equal(1UL, GuildBribePackets.LevelUpBribeAmount(100));
        Assert.Equal(123UL, GuildBribePackets.LevelUpBribeAmount(12_399));
        Assert.Equal(ulong.MaxValue / 100, GuildBribePackets.LevelUpBribeAmount(ulong.MaxValue));
    }

    private static GuildExpTable Lvl(byte level, uint next) => new() { Guild_Base_Level = level, Guild_Base_Exp_Next = next };

    [Fact]
    public void GuildLevelOf_UnreadableTable_IsLevelZeroWithTheWholeExp()
    {
        Assert.Equal(((byte)0, 123UL), GuildRosterPackets.GuildLevelOf(123, null));
        Assert.Equal(((byte)0, 123UL), GuildRosterPackets.GuildLevelOf(123, Array.Empty<GuildExpTable>()));
    }

    [Fact]
    public void GuildLevelOf_BoundariesAndCap()
    {
        var table = new[] { Lvl(1, 100), Lvl(2, 200), Lvl(3, 0) };

        Assert.Equal(((byte)1, 0UL), GuildRosterPackets.GuildLevelOf(0, table));
        Assert.Equal(((byte)1, 99UL), GuildRosterPackets.GuildLevelOf(99, table));
        Assert.Equal(((byte)2, 0UL), GuildRosterPackets.GuildLevelOf(100, table));
        Assert.Equal(((byte)2, 199UL), GuildRosterPackets.GuildLevelOf(299, table));
        Assert.Equal(((byte)3, 0UL), GuildRosterPackets.GuildLevelOf(300, table));
        Assert.Equal(((byte)3, ulong.MaxValue - 300), GuildRosterPackets.GuildLevelOf(ulong.MaxValue, table));
    }

    [Fact]
    public void GuildLevelOf_SortsByLevel_CapsAtLastRowEvenWithANextCost_StopsAtAZeroCost()
    {
        var shuffled = new[] { Lvl(2, 200), Lvl(1, 100) };
        Assert.Equal(((byte)2, 1_000UL), GuildRosterPackets.GuildLevelOf(1_100, shuffled));   // last row keeps its leftover exp

        var gap = new[] { Lvl(1, 100), Lvl(2, 0), Lvl(3, 50) };
        Assert.Equal(((byte)2, 900UL), GuildRosterPackets.GuildLevelOf(1_000, gap));        // a zero cost never advances
    }

    [Fact]
    public void UnspentPoints_IsLevelMinusOneMinusSpentPointLines_ClampedToAByte()
    {
        var table = new Dictionary<uint, GuildSkillLevel>
        {
            [101] = new() { Guild_Skill_ID = 101, Skill_LearnType = GuildSkillLearnPackets.PointLearnType },
            [102] = new() { Guild_Skill_ID = 102, Skill_LearnType = GuildSkillLearnPackets.PointLearnType },
            [201] = new() { Guild_Skill_ID = 201, Skill_LearnType = 1 },
        };

        Assert.Equal((byte)0, GuildSkillLearnPackets.UnspentPoints(0, [], table));
        Assert.Equal((byte)0, GuildSkillLearnPackets.UnspentPoints(1, [], table));
        Assert.Equal((byte)9, GuildSkillLearnPackets.UnspentPoints(10, [], table));
        Assert.Equal((byte)7, GuildSkillLearnPackets.UnspentPoints(10, [101, 102], table));    // a line costs its level, not its count
        Assert.Equal((byte)9, GuildSkillLearnPackets.UnspentPoints(10, [201], table));         // non-point lines are free
        Assert.Equal((byte)0, GuildSkillLearnPackets.UnspentPoints(3, [102], table));
        Assert.Equal((byte)254, GuildSkillLearnPackets.UnspentPoints(255, [], table));
    }

    // ---- invite (2565 contract) -------------------------------------------------------------------------------

    [Fact]
    public void InviteCodes_AreTheWireValues()
    {
        Assert.Equal(
            new ushort[] { 0, 1, 3, 7, 8, 11, 54, 73 },
            new[]
            {
                GuildInviteRules.Sent, GuildInviteRules.InviteeInGuild, GuildInviteRules.InviteeOffline,
                GuildInviteRules.InviterNoGuild, GuildInviteRules.InviterNoRight, GuildInviteRules.AlreadyInvited,
                GuildInviteRules.NoSuchName, GuildInviteRules.RejoinCooldownActive,
            });
    }

    [Fact]
    public void InviteDecide_CheckOrderIsTheContract()
    {
        var end = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // every fault present: each fix exposes the next check in the order
        var f = new GuildInviteRules.InviteFacts(false, false, false, false, true, end, true);

        Assert.Equal((GuildInviteRules.InviterNoGuild, (DateTime?)null), GuildInviteRules.Decide(f));
        f = f with { InviterInGuild = true };
        Assert.Equal((GuildInviteRules.InviterNoRight, (DateTime?)null), GuildInviteRules.Decide(f));
        f = f with { InviterMayInvite = true };
        Assert.Equal((GuildInviteRules.NoSuchName, (DateTime?)null), GuildInviteRules.Decide(f));
        f = f with { NameExists = true };
        Assert.Equal((GuildInviteRules.InviteeOffline, (DateTime?)null), GuildInviteRules.Decide(f));
        f = f with { InviteeOnline = true };
        Assert.Equal((GuildInviteRules.InviteeInGuild, (DateTime?)null), GuildInviteRules.Decide(f));
        f = f with { InviteeInGuild = false };
        Assert.Equal((GuildInviteRules.RejoinCooldownActive, (DateTime?)end), GuildInviteRules.Decide(f));
        f = f with { RejoinAtUtc = null };
        Assert.Equal((GuildInviteRules.AlreadyInvited, (DateTime?)null), GuildInviteRules.Decide(f));
        f = f with { InviteePending = false };
        Assert.Equal((GuildInviteRules.Sent, (DateTime?)null), GuildInviteRules.Decide(f));
    }

    // ---- vault ------------------------------------------------------------------------------------------------

    private static List<GuildStorageItem> Cells(params int[] slots) => slots.Select(s => new GuildStorageItem { StorageSlot = (byte)s }).ToList();

    [Fact]
    public void LowestFreeCell_EdgesOfCapacityAndAddressableRange()
    {
        Assert.Equal(0, GuildVaultCapacity.LowestFreeCell(Cells(), 1));
        Assert.Equal(1, GuildVaultCapacity.LowestFreeCell(Cells(0), 5));
        Assert.Equal(2, GuildVaultCapacity.LowestFreeCell(Cells(0, 1, 7), 5));
        Assert.Equal(-1, GuildVaultCapacity.LowestFreeCell(Cells(0, 1), 2));
        Assert.Equal(1, GuildVaultCapacity.LowestFreeCell(Cells(0, 200), 3));          // slots beyond capacity are ignored
        Assert.Equal(-1, GuildVaultCapacity.LowestFreeCell(Cells(), -4));
        Assert.Equal(255, GuildVaultCapacity.LowestFreeCell(Cells(Enumerable.Range(0, 255).ToArray()), 1_000));
        Assert.Equal(-1, GuildVaultCapacity.LowestFreeCell(Cells(Enumerable.Range(0, 256).ToArray()), 1_000));   // clamped to 256
    }

    private static ConfigDef Cfg(string v) => new() { type = GuildVaultCapacity.ConfigMaxVaultCells, Value = v };

    [Fact]
    public void MaxCells_FallsBackToAddressableCeiling_OnAbsentOrBadRow()
    {
        Assert.Equal(40, GuildVaultCapacity.MaxCells([Cfg("40")]));
        Assert.Equal(40, GuildVaultCapacity.MaxCells([Cfg(" 40 ")]));
        Assert.Equal(256, GuildVaultCapacity.MaxCells([Cfg("300")]));
        foreach (var bad in new[] { "0", "-5", "abc", "", "40.5" })
            Assert.Equal(256, GuildVaultCapacity.MaxCells([Cfg(bad)]));
        Assert.Equal(256, GuildVaultCapacity.MaxCells([new ConfigDef { type = 99, Value = "5" }]));
    }

    [Fact]
    public void SlotCapacity_TakesTheBiggestStorageCategoryAcrossSlots_CappedByMaxCells()
    {
        const byte Vault = GuildVaultCapacity.SkillCategoryVaultCells;
        var table = new Dictionary<uint, GuildSkillLevel>
        {
            [1] = new() { Skill_Category_1 = Vault, Skill_Value_1 = 20 },
            [2] = new() { Skill_Category_2 = Vault, Skill_Value_2 = 70 },
            [3] = new() { Skill_Category_3 = Vault, Skill_Value_3 = 50 },
            [4] = new() { Skill_Category_1 = 9, Skill_Value_1 = 200 },
        };

        Assert.Equal(70, GuildVaultCapacity.SlotCapacity([1, 2, 3], table, []));
        Assert.Equal(50, GuildVaultCapacity.SlotCapacity([1, 3], table, []));
        Assert.Equal(0, GuildVaultCapacity.SlotCapacity([4, 999], table, []));
        Assert.Equal(60, GuildVaultCapacity.SlotCapacity([2], table, [Cfg("60")]));
    }
}
