using RO2.Server.Zone.Services;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;
using static RO2.Worlds.Application.WorldEntryUseCase;

namespace RO2.Tests.World;

/// <summary>
/// The pure decisions of a world entry: where it enters, whether a supplied trait sheet wins, and which optional steps of the
/// plan are sent. The step order itself is pinned by <see cref="WorldEntryPlanTests"/>; here only the filtering is checked.
/// </summary>
public class WorldEntryUseCaseTests
{
    private static readonly EntrySpawn Stored = new(1101, 1f, 2f, 3f);
    private static readonly EntrySpawn Destination = new(1200, 10f, 20f, 30f);

    private static EntryRequest Request(
        bool isWarp = false, EntrySpawn? warpSpawn = null, bool warpTrait = false, bool gameData = true,
        bool inventory = true, bool novice = true, bool cardBook = true)
        => new(isWarp, Stored, warpSpawn, warpTrait, gameData, inventory, novice, cardBook);

    private static ushort[] Opcodes(EntryDecision d) => d.AfterPreMapLoad.Select(WorldEntryStepOpcodes.Of).ToArray();

    [Fact]
    public void Login_WithEverything_KeepsEveryPlanStepInOrder()
    {
        var d = Decide(Request());
        Assert.Equal(WorldEntryMode.Login, d.Mode);
        Assert.Equal(WorldEntryPlan.For(WorldEntryMode.Login).AfterPreMapLoad, d.AfterPreMapLoad);
    }

    [Fact]
    public void Warp_WithEverything_KeepsEveryPlanStepInOrder()
    {
        var d = Decide(Request(isWarp: true));
        Assert.Equal(WorldEntryMode.Warp, d.Mode);
        Assert.Equal(WorldEntryPlan.For(WorldEntryMode.Warp).AfterPreMapLoad, d.AfterPreMapLoad);
    }

    [Fact]
    public void Spawn_Login_IgnoresTheWarpDestination() => Assert.Equal(Stored, Decide(Request(warpSpawn: Destination)).Spawn);

    [Fact]
    public void Spawn_Warp_TakesTheDestination() => Assert.Equal(Destination, Decide(Request(isWarp: true, warpSpawn: Destination)).Spawn);

    [Fact]
    public void Spawn_WarpWithoutDestination_KeepsTheStoredSpawn() => Assert.Equal(Stored, Decide(Request(isWarp: true)).Spawn);

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void WarpTraitSheet_IsUsedOnlyByAWarpThatSuppliedOne(bool isWarp, bool supplied, bool expected)
        => Assert.Equal(expected, Decide(Request(isWarp: isWarp, warpTrait: supplied)).UseWarpTraitSheet);

    [Fact]
    public void CraftIndex_NeedsANoviceAndACatalog()
    {
        Assert.Contains(WorldEntryStep.CraftIndex, Decide(Request()).AfterPreMapLoad);
        Assert.DoesNotContain(WorldEntryStep.CraftIndex, Decide(Request(novice: false)).AfterPreMapLoad);
        Assert.DoesNotContain(WorldEntryStep.CraftIndex, Decide(Request(gameData: false)).AfterPreMapLoad);
    }

    [Fact]
    public void CardBookCharge_NeedsAChargeToReport()
    {
        Assert.Contains(WorldEntryStep.CardBookCharge, Decide(Request()).AfterPreMapLoad);
        Assert.DoesNotContain(WorldEntryStep.CardBookCharge, Decide(Request(cardBook: false)).AfterPreMapLoad);
    }

    [Fact]
    public void RegisterCardBook_NeedsInventoryAndCatalog()
    {
        Assert.Contains(WorldEntryStep.RegisterCardBook, Decide(Request(isWarp: true)).AfterPreMapLoad);
        Assert.DoesNotContain(WorldEntryStep.RegisterCardBook, Decide(Request(isWarp: true, inventory: false)).AfterPreMapLoad);
        Assert.DoesNotContain(WorldEntryStep.RegisterCardBook, Decide(Request(isWarp: true, gameData: false)).AfterPreMapLoad);
    }

    [Fact]
    public void LoginWithNothingOptional_LeavesTheUngatedSteps()
    {
        var d = Decide(Request(gameData: false, inventory: false, novice: false, cardBook: false));
        Assert.Equal(new ushort[]
        {
            2310, 2498, 2488, 2982,             // character detail, trait, skills (host-gated), ranking
            2311, 2472, 2523, 2522,             // map load, quick slots, vehicles
            2913, 2919, 2918,                   // monster book, pet book, active pet
            2497, 2435,                         // move speed, zeny
        }, Opcodes(d));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(10, 4, 5)]
    [InlineData(10, 20, 0)]
    [InlineData(255, 0, 254)]
    public void TalentPointsWithoutCatalog_IsLevelMinusOneMinusSpent_ClampedToAByte(int level, int spent, int expected)
        => Assert.Equal((byte)expected, TalentPointsWithoutCatalog(level, spent));
}
