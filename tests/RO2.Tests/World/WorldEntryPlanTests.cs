using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>
/// The order of the world-entry steps for both entry modes. The opcode lists were read from the burst the host sent before the
/// plan existed (a login with every optional step present, and a warp), so a reorder here is a wire change.
/// </summary>
public class WorldEntryPlanTests
{
    private static ushort[] Opcodes(IEnumerable<WorldEntryStep> steps) => steps.Select(WorldEntryStepOpcodes.Of).ToArray();

    [Fact]
    public void Login_Order_IsTheLoginBurst()
    {
        Assert.Equal(new ushort[]
        {
            2558, 2743, 2595, 2498, 2669,       // key config, dungeon info, trait init, trait, server time
            2535, 2310, 2498, 2488, 2793, 2982, // craft index, character detail, trait, skills, card book charge, ranking
            2311, 2472, 2523, 2522,             // map load, quick slots, vehicle quick slot, vehicles
            2913, 2919, 2918, 2588,             // monster book, pet book, active pet, MVP cards
            2497, 2435,                         // move speed, zeny
        }, Opcodes(WorldEntryPlan.For(WorldEntryMode.Login).Steps));
    }

    [Fact]
    public void Warp_Order_IsTheLoginBurstWithoutTheLoginOnlySteps()
    {
        Assert.Equal(new ushort[]
        {
            2558, 2743, 2595, 2498, 2669,
            2311, 2472, 2523, 2522,
            2913, 2919, 2918, 2588,
        }, Opcodes(WorldEntryPlan.For(WorldEntryMode.Warp).Steps));
    }

    [Fact]
    public void Warp_IsTheLoginPlanMinusItsLoginOnlySteps()
    {
        var loginOnly = new[]
        {
            WorldEntryStep.CraftIndex, WorldEntryStep.CharacterDetail, WorldEntryStep.LoginPcTrait,
            WorldEntryStep.UseSkillList, WorldEntryStep.CardBookCharge, WorldEntryStep.RankingTopList,
            WorldEntryStep.MoveSpeed, WorldEntryStep.ZenyBalance,
        };
        Assert.Equal(
            WorldEntryPlan.For(WorldEntryMode.Login).Steps.Where(s => !loginOnly.Contains(s)),
            WorldEntryPlan.For(WorldEntryMode.Warp).Steps);
    }

    [Theory]
    [InlineData(WorldEntryMode.Login)]
    [InlineData(WorldEntryMode.Warp)]
    public void BothModes_StartWithThePreMapLoadPrefix_AndAfterPreMapLoadIsTheRest(WorldEntryMode mode)
    {
        var plan = WorldEntryPlan.For(mode);
        Assert.Equal(mode, plan.Mode);
        Assert.Equal(WorldEntryPlan.PreMapLoad, plan.Steps.Take(WorldEntryPlan.PreMapLoad.Count));
        Assert.Equal(plan.Steps.Skip(WorldEntryPlan.PreMapLoad.Count), plan.AfterPreMapLoad);
        Assert.Equal(plan.Steps.Distinct().Count(), plan.Steps.Count);
    }

    [Fact]
    public void PreMapLoad_IsTheFiveMessagesBeforeTheMapLoad()
    {
        Assert.Equal(new ushort[] { 2558, 2743, 2595, 2498, 2669 }, Opcodes(WorldEntryPlan.PreMapLoad));
    }

    [Fact]
    public void Departure_IsTheMarkerThenTheSelfDespawn()
    {
        Assert.Equal(new ushort[] { 2938, 2505 }, Opcodes(WorldEntryPlan.Departure));
    }

    [Fact]
    public void InPlaceMapLoad_IsTheMapLoadThenTheQuickSlotsAndTheTwoVehicleLists()
    {
        Assert.Equal(new ushort[] { 2311, 2472, 2523, 2522 }, Opcodes(WorldEntryPlan.InPlaceMapLoad));
    }

    [Fact]
    public void For_RejectsAnUnknownMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldEntryPlan.For((WorldEntryMode)99));
    }

    [Fact]
    public void EveryStep_HasAnOpcode()
    {
        foreach (var step in Enum.GetValues<WorldEntryStep>())
            Assert.NotEqual((ushort)0, WorldEntryStepOpcodes.Of(step));
    }
}
