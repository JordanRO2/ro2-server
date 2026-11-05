using RO2.Domain.Entities;
using RO2.Worlds.Domain;
using RO2.Server.Zone.Services;
using RO2.Tests.Packets;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// Stage rules of the world event (WorldEventMachine) over the shipped tables, replayed against a stage sequence.
/// </summary>
/// <remarks>
/// Replays the transitions of event 122101 in one session: the offerings to the elder, the stage pushes that
/// follow each completed offering, and the fall back to stage 1 once stage 5 has lasted its Stage_Duration_Time of
/// 1,380 minutes.
/// </remarks>
public class WorldEventServiceTests
{
    private const uint Jawaii = 122101;

    [Fact]
    public void Offerings_MoveTheEvent_OnlyWhenEveryRowOfTheGroupIsMet()
    {
        var t = WorldEventTrigger3030Tests.Tables();
        var now = new DateTime(2026, 7, 11, 2, 7, 0, DateTimeKind.Utc);
        var doc = WorldEventMachine.Enter(Jawaii, 2, now, t);
        Assert.Equal(new uint[] { 0, 0 }, doc.Counts);

        
        Assert.Equal(new WorldEventMachine.OfferingResult(true, null), WorldEventMachine.AddOffering(doc, t, 32510000, 35000002, 50));
        Assert.Equal(new WorldEventMachine.OfferingResult(true, null), WorldEventMachine.AddOffering(doc, t, 32510000, 35000001, 50));
        
        Assert.Equal(new WorldEventMachine.OfferingResult(true, null), WorldEventMachine.AddOffering(doc, t, 32510000, 35000002, 50));
        Assert.Equal(new WorldEventMachine.OfferingResult(true, 3), WorldEventMachine.AddOffering(doc, t, 32510000, 35000001, 50));

        
        doc = WorldEventMachine.Enter(Jawaii, 3, now, t);
        Assert.Equal(new uint[] { 0, 0 }, doc.Counts);
        Assert.Equal(new WorldEventMachine.OfferingResult(true, null), WorldEventMachine.AddOffering(doc, t, 32420000, 35000003, 25));
        Assert.Equal(new WorldEventMachine.OfferingResult(true, 4), WorldEventMachine.AddOffering(doc, t, 32420000, 35000004, 25));

        
        doc = WorldEventMachine.Enter(Jawaii, 4, now, t);
        Assert.Equal(new WorldEventMachine.OfferingResult(true, 5), WorldEventMachine.AddOffering(doc, t, 32490000, 35000005, 4));
    }

    [Fact]
    public void Offerings_OutsideTheCurrentStagesGroup_AreNotCounted()
    {
        var t = WorldEventTrigger3030Tests.Tables();
        var now = new DateTime(2026, 7, 11, 2, 7, 0, DateTimeKind.Utc);

        
        
        var stage2 = WorldEventMachine.Enter(Jawaii, 2, now, t);
        Assert.False(WorldEventMachine.AddOffering(stage2, t, 32480000, 35000000, 991).Counted);
        Assert.False(WorldEventMachine.AddOffering(stage2, t, 32510000, 60000733, 1).Counted);
        Assert.Equal(new uint[] { 0, 0 }, stage2.Counts);

        var stage5 = WorldEventMachine.Enter(Jawaii, 5, now, t);
        Assert.Empty(stage5.Counts);
        Assert.False(WorldEventMachine.AddOffering(stage5, t, 32490000, 35000005, 4).Counted);
    }

    [Fact]
    public void Stage5_TimesOutAfter1380Minutes_BackToStage1()
    {
        var t = WorldEventTrigger3030Tests.Tables();
        var entered = new DateTime(2026, 7, 11, 2, 8, 26, 666, DateTimeKind.Utc);
        var doc = WorldEventMachine.Enter(Jawaii, 5, entered, t);

        Assert.Equal(entered.AddMinutes(1380), WorldEventMachine.Deadline(doc, t));
        Assert.Null(WorldEventMachine.TimedOut(doc, t, entered.AddMinutes(1380).AddMilliseconds(-1)));
        
        Assert.Equal((byte)1, WorldEventMachine.TimedOut(doc, t, new DateTime(2026, 7, 12, 1, 8, 39, DateTimeKind.Utc)));
    }

    [Fact]
    public void Stage1_WaitsOnItsCheckGroup_AndNeverTimesOut()
    {
        var t = WorldEventTrigger3030Tests.Tables();
        var doc = WorldEventMachine.Enter(Jawaii, 1, new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc), t);

        Assert.Single(doc.Counts); 
        Assert.Null(WorldEventMachine.Deadline(doc, t));
        Assert.Null(WorldEventMachine.TimedOut(doc, t, DateTime.MaxValue));
    }

    [Fact]
    public void Normalize_FitsStoredCountsToTheStage_AndDropsAStageTheTableNoLongerHas()
    {
        var t = WorldEventTrigger3030Tests.Tables();
        var stored = new WorldEventStateDocument { Id = Jawaii, Stage = 2, Counts = new List<uint> { 7 } };

        var fitted = WorldEventMachine.Normalize(stored, t);

        Assert.NotNull(fitted);
        Assert.Equal(new uint[] { 7, 0 }, fitted!.Counts);
        Assert.Equal(new List<uint> { 7 }, stored.Counts); 
        Assert.Null(WorldEventMachine.Normalize(new WorldEventStateDocument { Id = Jawaii, Stage = 9 }, t));
    }
}
