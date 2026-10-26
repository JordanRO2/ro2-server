using System;
using System.Collections.Generic;
using RO2.GameData.Tables;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Pure characterization of <see cref="WorldEventStageRules"/> (the stage rules moved out of WorldEventMachine in B5 S16).</summary>
public class WorldEventStageRulesTests
{
    private const uint Ev = 7;
    private static readonly DateTime T0 = new(2026, 7, 11, 2, 0, 0, DateTimeKind.Utc);

    // Stage 1: waits on check group 10 (two offering rows), next stage 2. Stage 2: 30 minutes, returns to 1.
    private static WorldEventTables Tables() => new(
        new List<WorldEventMain>
        {
            new() { World_Event_ID = Ev, Stage_Level = 1, Check_Group_ID_1 = 10, Next_Stage = 2, Return_Stage = 0 },
            new() { World_Event_ID = Ev, Stage_Level = 2, Stage_Duration_Time = 30, Return_Stage = 1 },
            new() { World_Event_ID = Ev, Stage_Level = 3, Stage_Duration_Time = 5, Return_Stage = 9 },
        },
        new List<WorldEventCheckGroup>
        {
            new() { Check_Group_ID = 10, INDEX = 2, Check_Type = 2, Type_Data_1 = 100, Type_Data_2 = 201, Type_Data_3 = 5 },
            new() { Check_Group_ID = 10, INDEX = 1, Check_Type = 2, Type_Data_1 = 100, Type_Data_2 = 200, Type_Data_3 = 3 },
            new() { Check_Group_ID = 10, INDEX = 3, Check_Type = 1, Type_Data_1 = 100, Type_Data_2 = 202, Type_Data_3 = 1 },
        },
        new List<WorldEventEventGroup>());

    [Fact]
    public void Enter_ZeroesOneCountPerOfferingRow_AndStampsBothTimes()
    {
        var s = WorldEventStageRules.Enter(Ev, 1, T0, Tables());
        Assert.Equal(new uint[] { 0, 0 }, s.Counts);
        Assert.Equal(T0, s.StageEnteredUtc);
        Assert.Equal(T0, s.UpdatedUtc);
        Assert.Empty(WorldEventStageRules.Enter(Ev, 99, T0, Tables()).Counts);
    }

    [Fact]
    public void AddOffering_CountsInIndexOrder_ReportsNextStageOnlyWhenAllRowsMet_AndDoesNotMutate()
    {
        var t = Tables();
        var s = WorldEventStageRules.Enter(Ev, 1, T0, t);

        var r1 = WorldEventStageRules.AddOffering(s, t, 100, 201, 5); // index-2 row, met
        Assert.True(r1.Counted);
        Assert.Null(r1.NextStage);
        Assert.Equal(new uint[] { 0, 5 }, r1.Counts);
        Assert.Equal(new uint[] { 0, 0 }, s.Counts);

        var s2 = s with { Counts = r1.Counts };
        var r2 = WorldEventStageRules.AddOffering(s2, t, 100, 200, 3); // index-1 row, now all met
        Assert.Equal(new WorldEventOfferingResult(true, 2, r2.Counts), r2);
        Assert.Equal(new uint[] { 3, 5 }, r2.Counts);
    }

    [Fact]
    public void AddOffering_Refusals_ReturnTheInputCountsUncounted()
    {
        var t = Tables();
        var s = WorldEventStageRules.Enter(Ev, 1, T0, t);
        Assert.False(WorldEventStageRules.AddOffering(s, t, 100, 200, 0).Counted);
        Assert.False(WorldEventStageRules.AddOffering(s, t, 100, 200, -1).Counted);
        Assert.False(WorldEventStageRules.AddOffering(s, t, 101, 200, 1).Counted);   // wrong npc
        Assert.False(WorldEventStageRules.AddOffering(s, t, 100, 202, 1).Counted);   // check type 1 is not an offering row
        Assert.False(WorldEventStageRules.AddOffering(s with { Stage = 99 }, t, 100, 200, 1).Counted);
        Assert.Same(s.Counts, WorldEventStageRules.AddOffering(s, t, 101, 200, 1).Counts);
    }

    [Fact]
    public void AddOffering_SaturatesAtUIntMax_AndPadsShortCounts()
    {
        var t = Tables();
        var s = new WorldEventState(Ev, 1, T0, new uint[] { uint.MaxValue - 1 }, T0);
        var r = WorldEventStageRules.AddOffering(s, t, 100, 200, 10);
        Assert.Equal(new uint[] { uint.MaxValue, 0 }, r.Counts);
    }

    [Fact]
    public void Normalize_FitsCounts_DropsMissingStage_AndKeepsTheRest()
    {
        var t = Tables();
        var stored = new WorldEventState(Ev, 1, T0, new uint[] { 7, 8, 9 }, T0.AddMinutes(1));
        var fitted = WorldEventStageRules.Normalize(stored, t)!;
        Assert.Equal(new uint[] { 7, 8 }, fitted.Counts);
        Assert.Equal(stored.UpdatedUtc, fitted.UpdatedUtc);
        Assert.Equal(new uint[] { 7, 8, 9 }, stored.Counts);
        Assert.Equal(new uint[] { 4, 0 }, WorldEventStageRules.Normalize(stored with { Counts = new uint[] { 4 } }, t)!.Counts);
        Assert.Null(WorldEventStageRules.Normalize(stored with { Stage = 99 }, t));
    }

    [Fact]
    public void DeadlineAndTimedOut_UseTheStageDurationAndReturnStage()
    {
        var t = Tables();
        Assert.Null(WorldEventStageRules.Deadline(WorldEventStageRules.Enter(Ev, 1, T0, t), t)); // no duration
        var s = WorldEventStageRules.Enter(Ev, 2, T0, t);
        Assert.Equal(T0.AddMinutes(30), WorldEventStageRules.Deadline(s, t));
        Assert.Null(WorldEventStageRules.TimedOut(s, t, T0.AddMinutes(30).AddTicks(-1)));
        Assert.Equal((byte)1, WorldEventStageRules.TimedOut(s, t, T0.AddMinutes(30)));
        // return stage 9 does not exist: no fall-back even past the deadline
        Assert.Null(WorldEventStageRules.TimedOut(WorldEventStageRules.Enter(Ev, 3, T0, t), t, DateTime.MaxValue));
    }
}
