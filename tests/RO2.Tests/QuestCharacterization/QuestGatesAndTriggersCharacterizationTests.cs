using System.Collections.Concurrent;
using System.Text;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.QuestCharacterization;

/// <summary>
/// B8 S1: characterization of the accept gates (2431 refusal codes), the repeat gate, the Start_Type 4 level grant, the quest
/// fail triggers and the Quest_Info action-row selectors, as the host has them today.
/// </summary>
[Collection(GameDataStoreLoadedCollection.Name)]
public class QuestGatesAndTriggersCharacterizationTests
{
    private const uint LevelRefusal = 21702;
    private const uint JobRefusal = 21718;
    private const uint ProJobRefusal = 21719;

    [DataFact]
    public void RefusalCodes_AreTheShippedValues()
    {
        Assert.Equal(LevelRefusal, QuestProgressPackets.QuestErrorJobMasterLevel);
        Assert.Equal(JobRefusal, QuestProgressPackets.QuestErrorJobMismatch);
        Assert.Equal(ProJobRefusal, QuestProgressPackets.QuestErrorProJobMismatch);
        Assert.Equal(21717u, QuestProgressPackets.QuestErrorDailyDone);
        Assert.Equal(21703u, QuestProgressPackets.QuestErrorInsertRejected);
        Assert.Equal(21711u, QuestProgressPackets.QuestErrorPrizeRejected);
        Assert.Equal(21712u, QuestProgressPackets.QuestErrorRequirementUnmet);
        Assert.Equal(21708u, QuestProgressPackets.QuestErrorPrizeNotGiven);
        Assert.Equal(20020u, QuestProgressPackets.QuestErrorBagFull);
        Assert.Equal(20021u, QuestProgressPackets.QuestInsertItemMissing);
        Assert.Equal(20036u, QuestProgressPackets.QuestInsertStartItemMissing);
    }

    // (PC_Job mask, PC_ProJob, Job_Master min, max, Job_Master_Quest, class, profession, base level, master level) -> code (0 = accepted)
    [DataTheory]
    [InlineData(0u, 0, 0, 0, 0, 100, 0, 1, 0, 0u)]                       // no constraint at all
    [InlineData(0u, 0, 10, 0, 0, 1, 0, 9, 0, LevelRefusal)]              // below the minimum
    [InlineData(0u, 0, 10, 0, 0, 1, 0, 10, 0, 0u)]                       // the minimum is inclusive
    [InlineData(0u, 0, 0, 20, 0, 1, 0, 19, 0, 0u)]
    [InlineData(0u, 0, 0, 20, 0, 1, 0, 20, 0, LevelRefusal)]             // QUIRK: the maximum is EXCLUSIVE here (QuestLevelGrants' is inclusive)
    [InlineData(0u, 0, 0, 20, 0, 1, 0, 21, 0, LevelRefusal)]
    [InlineData(0u, 0, 10, 0, 0, 1, 0, 255, 0, 0u)]                      // max 0 = no upper bound
    [InlineData(0u, 0, 1, 0, 1, 1, 0, 50, 0, LevelRefusal)]              // Job_Master_Quest 1 compares the MASTER level (always 0 on this server)
    [InlineData(0u, 0, 1, 0, 1, 1, 0, 50, 1, 0u)]
    [InlineData(0u, 0, 0, 5, 1, 1, 0, 1, 5, LevelRefusal)]
    [InlineData(0u, 0, 0, 5, 1, 1, 0, 200, 4, 0u)]                       // the base level is ignored when the master level is compared
    [InlineData(4u, 0, 0, 0, 0, 3, 0, 1, 0, 0u)]                         // mask bit (class - 1)
    [InlineData(4u, 0, 0, 0, 0, 2, 0, 1, 0, JobRefusal)]
    [InlineData(4u, 0, 0, 0, 0, 0, 0, 1, 0, JobRefusal)]                 // class 0 (unknown) has no bit
    [InlineData(4u, 0, 0, 0, 0, 33, 0, 1, 0, JobRefusal)]                // nor has 33
    [InlineData(4u, 0, 0, 0, 0, 100, 0, 1, 0, JobRefusal)]               // nor Novice 100
    [InlineData(0x80000000u, 0, 0, 0, 0, 32, 0, 1, 0, 0u)]
    [InlineData(0x80000000u, 0, 0, 0, 0, 31, 0, 1, 0, JobRefusal)]
    [InlineData(1u, 0, 0, 0, 0, 1, 0, 1, 0, 0u)]
    [InlineData(0u, 3, 0, 0, 0, 1, 3, 1, 0, 0u)]                         // PC_ProJob must equal the profession
    [InlineData(0u, 3, 0, 0, 0, 1, 2, 1, 0, ProJobRefusal)]
    [InlineData(0u, 3, 0, 0, 0, 1, 0, 1, 0, ProJobRefusal)]
    [InlineData(0u, 0, 0, 0, 0, 1, 9, 1, 0, 0u)]                         // 0 = any profession
    [InlineData(4u, 3, 10, 0, 0, 2, 2, 1, 0, LevelRefusal)]              // order: Job_Master, then PC_Job, then PC_ProJob
    [InlineData(4u, 3, 0, 0, 0, 2, 2, 1, 0, JobRefusal)]
    public void Refusal_FollowsTheGateOrderAndBoundaries(uint pcJob, int proJob, int min, int max, int masterQuest,
        int combatClass, int profession, int baseLevel, int masterLevel, uint expected)
    {
        var row = new QuestAcceptGates.Row(1, pcJob, (byte)proJob, (byte)min, (byte)max, (byte)masterQuest);
        Assert.Equal(expected == 0 ? (uint?)null : expected,
            QuestAcceptGates.Refusal(row, (byte)combatClass, (byte)profession, (byte)baseLevel, (byte)masterLevel));
    }

    [DataTheory]
    [InlineData(0u, 0, true)]
    [InlineData(0u, 100, true)]
    [InlineData(1u, 1, true)]
    [InlineData(1u, 2, false)]
    [InlineData(0x80000000u, 32, true)]
    [InlineData(0x80000000u, 33, false)]
    [InlineData(uint.MaxValue, 0, false)]            // a non-zero mask never lets class 0 through
    [InlineData(uint.MaxValue, 100, false)]
    [InlineData(uint.MaxValue, 17, true)]
    public void JobAllows_ZeroMaskLetsEveryClass_OtherwiseBitClassMinusOne(uint mask, int combatClass, bool expected)
        => Assert.Equal(expected, QuestAcceptGates.JobAllows(mask, (byte)combatClass));

    [DataFact]
    public void StartItemOf_OnlyStartType2_AndIndexKeepsTheFirstRowPerQuest()
    {
        Assert.Equal(5555u, QuestAcceptGates.StartItemOf(Q.Row(0) with { Start_Type = 2, Start_Type_ID = 5555 }));
        Assert.Equal(0u, QuestAcceptGates.StartItemOf(Q.Row(0) with { Start_Type = 1, Start_Type_ID = 5555 }));
        Assert.Equal(0u, QuestAcceptGates.StartItemOf(Q.Row(0) with { Start_Type = 4, Start_Type_ID = 5555 }));
        Assert.Equal(0u, QuestAcceptGates.StartItemOf(Q.Row(0) with { Start_Type = 2, Start_Type_ID = 0 }));

        var index = QuestAcceptGates.Index([
            new QuestAcceptGates.Row(7, 1, 0, 0, 0, 0), new QuestAcceptGates.Row(7, 2, 0, 0, 0, 0), new QuestAcceptGates.Row(8, 4, 0, 0, 0, 0)]);
        Assert.Equal(2, index.Count);
        Assert.Equal(1u, index[7].PC_Job);
        Assert.Equal(4u, index[8].PC_Job);
    }

    [DataFact]
    public void RepeatGate_IndexKeepsTheFirstRowPerQuest()
    {
        var index = QuestRepeatGate.Index([
            new QuestRepeatGate.Row(7, 1, 3, 0), new QuestRepeatGate.Row(7, 0, 9, 9), new QuestRepeatGate.Row(8, 0, 0, 5)]);
        Assert.Equal(2, index.Count);
        Assert.Equal(new QuestRepeatGate.Row(7, 1, 3, 0), index[7]);
        Assert.Equal(new QuestRepeatGate.Row(8, 0, 0, 5), index[8]);
    }

    // ---- Start_Type 4 level grant ------------------------------------------------------------------------------------------------

    private static readonly QuestData Grant = Q.Row(0) with { Start_Type = 4, Start_Type_ID = 10, MIN_Base_Level = 0, MAX_Base_Level = 20 };
    private static readonly QuestLevelGrants.ProJobRow ProJob = new(1, 3, 8);

    [DataTheory]
    [InlineData(10, 3, true)]
    [InlineData(9, 3, false)]            // below the start level
    [InlineData(20, 3, true)]            // QUIRK: the base-level maximum is INCLUSIVE here
    [InlineData(21, 3, false)]
    [InlineData(10, 2, false)]
    [InlineData(10, 8, true)]            // the profession-level maximum is inclusive too
    [InlineData(10, 9, false)]
    public void LevelGrantMeets_BoundariesOfTheThreeRanges(int level, int jobLevel, bool expected)
        => Assert.Equal(expected, QuestLevelGrants.Meets(Grant, ProJob, (byte)level, (byte)jobLevel));

    [DataFact]
    public void LevelGrantMeets_ZeroMaximumsAreUnboundedAndOtherStartTypesNeverGrant()
    {
        Assert.True(QuestLevelGrants.Meets(Grant with { MAX_Base_Level = 0 }, ProJob, 255, 3));
        Assert.True(QuestLevelGrants.Meets(Grant, new QuestLevelGrants.ProJobRow(1, 3, 0), 10, 255));
        Assert.False(QuestLevelGrants.Meets(Grant with { Start_Type = 1 }, ProJob, 10, 3));
        Assert.False(QuestLevelGrants.Meets(Grant with { MIN_Base_Level = 15 }, ProJob, 12, 3));   // MIN_Base_Level above the start level wins
    }

    // ---- fail triggers -------------------------------------------------------------------------------------------------------------

    private static QuestFailTriggers.FailRow Fail(uint quest, byte type, uint v1 = 0, uint v2 = 0) => new(quest, type, v1, v2);

    [DataFact]
    public void ParseFailRows_KeepsEveryRowOfQuestsWithAKillOrDeathFail_InFileOrder()
    {
        var json = Encoding.UTF8.GetBytes("""
            [{"Quest_ID":1,"Fail_Type":0,"Fail_Type_Value1":0,"Fail_Type_Value2":0},
             {"Quest_ID":1,"Fail_Type":2,"Fail_Type_Value1":500,"Fail_Type_Value2":3},
             {"Quest_ID":2,"Fail_Type":38,"Fail_Type_Value1":60000,"Fail_Type_Value2":0},
             {"Quest_ID":3,"Fail_Type":26,"Fail_Type_Value1":0,"Fail_Type_Value2":0}]
            """);
        var rows = QuestFailTriggers.ParseFailRows(json);
        Assert.Equal(new uint[] { 1, 3 }, rows.Keys.Order().ToArray());
        Assert.Equal(new[] { Fail(1, 0), Fail(1, 2, 500, 3) }, rows[1]);
        Assert.Empty(QuestFailTriggers.ParseFailRows(Encoding.UTF8.GetBytes("null")));
        Assert.Empty(QuestFailTriggers.ParseFailRows(Encoding.UTF8.GetBytes("[]")));
    }

    [DataFact]
    public void PlanDeathFailures_HeldQuestsWithADeathRow_AscendingDistinct_SkippingFailed()
    {
        var rows = new Dictionary<uint, QuestFailTriggers.FailRow[]>
        {
            [10] = [Fail(10, 26)], [20] = [Fail(20, 2, 500, 1)], [30] = [Fail(30, 0), Fail(30, 26)], [40] = [Fail(40, 26)],
        };
        var plan = QuestFailTriggers.PlanDeathFailures(rows, new uint[] { 40, 30, 10, 20, 30, 99 }, q => q == 40);
        Assert.Equal(new[] { new QuestFailTriggers.Failure(10, 26, 0), new QuestFailTriggers.Failure(30, 26, 0) }, plan);
        Assert.Empty(QuestFailTriggers.PlanDeathFailures(rows, [], _ => false));
    }

    [DataFact]
    public void CountKill_FailsAtValue2Kills_ZeroReadAsOne_FirstFailingRowWins_NpcZeroIgnored()
    {
        var rows = new Dictionary<uint, QuestFailTriggers.FailRow[]>
        {
            [10] = [Fail(10, 2, 500, 3)],
            [20] = [Fail(20, 2, 500, 0)],
            [30] = [Fail(30, 2, 500, 1), Fail(30, 2, 500, 1)],
            [40] = [Fail(40, 26), Fail(40, 2, 600, 2)],
        };
        var counts = new ConcurrentDictionary<(uint Quest, int Row), int>();
        var held = new uint[] { 40, 30, 20, 10 };

        Assert.Equal(new[] { new QuestFailTriggers.Failure(20, 2, 500), new QuestFailTriggers.Failure(30, 2, 500) },
            QuestFailTriggers.CountKill(rows, held, _ => false, counts, 500));           // ascending; quest 20 (need 0 = 1) and 30 fail at once
        Assert.Equal(1, counts[(10, 0)]);
        Assert.False(counts.ContainsKey((30, 1)));                                        // the break: the second matching row is never counted

        Assert.Empty(QuestFailTriggers.CountKill(rows, [10], _ => false, counts, 500));   // 2 of 3
        Assert.Equal(new[] { new QuestFailTriggers.Failure(10, 2, 500) }, QuestFailTriggers.CountKill(rows, [10], _ => false, counts, 500));
        // QUIRK: counting goes on after the failure, so every further kill reports the failure again
        Assert.Equal(new[] { new QuestFailTriggers.Failure(10, 2, 500) }, QuestFailTriggers.CountKill(rows, [10], _ => false, counts, 500));

        Assert.Empty(QuestFailTriggers.CountKill(rows, [40], _ => false, counts, 500));   // the death row of 40 is not a kill row
        Assert.Empty(QuestFailTriggers.CountKill(rows, [40], _ => false, counts, 0));     // npc 0 is ignored
        Assert.Empty(QuestFailTriggers.CountKill(rows, [20], q => q == 20, counts, 500)); // a failed quest is not counted
        Assert.Empty(QuestFailTriggers.CountKill(rows, [99], _ => false, counts, 500));   // not in the table
    }

    // ---- Quest_Info action rows ---------------------------------------------------------------------------------------------------------

    private static QuestAcceptHandler.QuestActionRow Act(byte active, byte action, uint v1, uint v2 = 0) => new(1, active, action, v1, v2);

    [DataFact]
    public void ActionRowSelectors_GiveRows_CountZeroReadAsOne_ItemZeroSkipped_RowOrderKept()
    {
        QuestAcceptHandler.QuestActionRow[] rows = [Act(1, 3, 100), Act(1, 3, 0, 5), Act(1, 4, 101, 2), Act(3, 3, 102, 4), Act(1, 3, 103, 7), Act(2, 3, 104, 9)];
        Assert.Equal(new[] { (100u, 1), (103u, 7) }, QuestAcceptHandler.SelectAcceptGrants(rows));
        Assert.Equal(new[] { (102u, 4) }, QuestAcceptHandler.SelectObjectiveCloseGrants(rows));
        Assert.Empty(QuestAcceptHandler.SelectAcceptGrants([]));
    }

    [DataFact]
    public async Task ActionRowSelectors_TakeRows_NeedAnItemInfoRow_CompletionTakesSkipTheObjectiveItem()
    {
        var store = await LoadStoreAsync("ItemInfo.json");
        const uint known = 40000981, objectiveItem = 40000974, unknown = 4;
        Assert.True(store.Items.ContainsKey(known));
        Assert.True(store.Items.ContainsKey(objectiveItem));
        Assert.False(store.Items.ContainsKey(unknown));
        QuestAcceptHandler.QuestActionRow[] rows = [Act(1, 4, known), Act(1, 4, unknown, 3), Act(1, 3, known), Act(2, 4, known, 0), Act(2, 4, objectiveItem, 5)];

        Assert.Equal(new[] { (known, 1) }, QuestAcceptHandler.SelectTakeRows(rows, 1, store));        // count 0 reads as 1; unknown item dropped
        Assert.Equal(new[] { (known, 1), (objectiveItem, 5) }, QuestAcceptHandler.SelectTakeRows(rows, 2, store));
        // an Active_Type 2 take of the quest's own collect / deliver item is the turn-in's hand-over, not a take
        Assert.Equal(new[] { (known, 1) }, QuestAcceptHandler.SelectCompletionTakes(rows, [Q.Row(4, objectiveItem, 5)], store));
        Assert.Equal(new[] { (known, 1) }, QuestAcceptHandler.SelectCompletionTakes(rows, [Q.Row(5, objectiveItem, 5)], store));
        Assert.Equal(new[] { (known, 1), (objectiveItem, 5) }, QuestAcceptHandler.SelectCompletionTakes(rows, [Q.Row(2, objectiveItem, 5)], store));
        Assert.Equal(new[] { (known, 1), (objectiveItem, 5) }, QuestAcceptHandler.SelectCompletionTakes(rows, null, store));
    }
}
