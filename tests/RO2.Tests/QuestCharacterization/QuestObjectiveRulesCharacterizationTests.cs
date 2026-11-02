using System.Buffers.Binary;
using RO2.Quests.Domain;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.QuestCharacterization;

/// <summary>
/// B8 S1: characterization of the pure quest objective rules as they are in the host today (QuestProgressStore, LootService
/// kill closure, QuestUseItemProgress, QuestTalkProgress, QuestCollectDropTicks, QuestPrizeHandler). Every expected value was
/// read from the code and confirmed by running it. Quirks are pinned on purpose and marked "QUIRK".
/// </summary>
public class QuestObjectiveRulesCharacterizationTests
{
    private const uint Qid = 990000001;

    // ---- 2. constants, row counting, kill rows ---------------------------------------------------------------------

    [Fact]
    public void CompleteTypeConstants_AreTheShippedValues()
    {
        Assert.Equal((byte)0, QuestProgressStore.NoObjective);
        Assert.Equal((byte)1, QuestProgressStore.TalkObjective);
        Assert.Equal((byte)2, QuestProgressStore.KillObjective);
        Assert.Equal((byte)4, QuestProgressStore.CollectObjective);
        Assert.Equal((byte)5, QuestProgressStore.DeliverObjective);
        Assert.Equal((byte)6, QuestProgressStore.UseItemObjective);
        Assert.Equal((byte)1, QuestTalkProgress.TalkObjective);
        Assert.Equal(1u, QuestProgressStore.StateMet);
        Assert.Equal(2u, QuestProgressStore.StateRunning);
        Assert.Equal(20, QuestProgressStore.SubRecordLength);

        // The same numbers repeated as private constants in other classes: a carve must keep every copy equal.
        Assert.Equal((byte)3, Q.PrivateConst(typeof(QuestPrizeHandler), "PlaceObjective"));
        Assert.Equal((byte)34, Q.PrivateConst(typeof(QuestPrizeHandler), "TimedObjective"));
        Assert.Equal((byte)1, Q.PrivateConst(typeof(QuestPrizeHandler), "RewardTypeAllSlots"));
        Assert.Equal((byte)2, Q.PrivateConst(typeof(QuestPrizeHandler), "RewardTypePickOne"));
        Assert.Equal((byte)5, Q.PrivateConst(typeof(QuestPrizeHandler), "RewardTypePickOneAlt"));
        Assert.Equal((ushort)1, Q.PrivateConst(typeof(QuestPrizeHandler), "ObjectiveClosedCounter"));
        Assert.Equal(0u, Q.PrivateConst(typeof(QuestPrizeHandler), "TalkRowMetByDialog"));
        Assert.Equal(uint.MaxValue, QuestPrizeHandler.NoRewardChoice);
        Assert.Equal((byte)3, Q.PrivateConst(typeof(QuestCompleteHandler), "PositionObjective"));
        Assert.Equal((byte)5, Q.PrivateConst(typeof(QuestCompleteHandler), "DeliverObjective"));
        Assert.Equal((byte)34, Q.PrivateConst(typeof(QuestAcceptHandler), "TimedObjective"));
        Assert.Equal((byte)38, Q.PrivateConst(typeof(QuestAcceptHandler), "TimeLimitFailType"));
        Assert.Equal((byte)4, Q.PrivateConst(typeof(QuestAcceptHandler), "CollectObjective"));
        Assert.Equal((byte)5, Q.PrivateConst(typeof(QuestAcceptHandler), "DeliverObjective"));
        Assert.Equal((byte)6, Q.PrivateConst(typeof(ItemUseHandler), "UseItemObjective"));
        Assert.Equal((ushort)1, Q.PrivateConst(typeof(ItemUseHandler), "ObjectiveClosedMark"));
        Assert.Equal(255u, Q.PrivateConst(typeof(ItemUseHandler), "MaxUsesPerRow"));
        Assert.Equal((byte)2, QuestFailTriggers.FailTypeKill);
        Assert.Equal((byte)26, QuestFailTriggers.FailTypeDeath);
        Assert.Equal((byte)4, QuestLevelGrants.LevelStartType);
        Assert.Equal((byte)2, QuestAcceptGates.StartFromItem);
    }

    [Fact]
    public void ObjectiveRowCount_CountsEveryRowWithACompleteType_UnknownTypesIncluded()
    {
        Assert.Equal(0, QuestProgressStore.ObjectiveRowCount([]));
        Assert.Equal(0, QuestProgressStore.ObjectiveRowCount([Q.Row(0), Q.Row(0)]));
        Assert.Equal(3, QuestProgressStore.ObjectiveRowCount([Q.Row(0), Q.Row(2, 1), Q.Row(0), Q.Row(4, 1), Q.Row(99)]));
    }

    [Theory]
    [InlineData((byte)2, 1u, true)]
    [InlineData((byte)2, 0u, false)]   // 213010440: a kill row with no target
    [InlineData((byte)1, 5u, false)]
    [InlineData((byte)0, 5u, false)]
    [InlineData((byte)4, 5u, false)]
    public void IsKillRow_NeedsTypeTwoAndATarget(byte type, uint value1, bool expected)
        => Assert.Equal(expected, QuestProgressStore.IsKillRow(Q.Row(type, value1)));

    // ---- 1. needed-objective caps, both sides of the deliberate inconsistency -----------------------------------------

    [Theory]
    [InlineData(0u, (ushort)1)]
    [InlineData(1u, (ushort)1)]
    [InlineData(2u, (ushort)2)]
    [InlineData(254u, (ushort)254)]
    [InlineData(255u, (ushort)255)]
    [InlineData(256u, (ushort)256)]            // u16 cap: NO byte cap here (store / leave-bag / kill / collect-closure side)
    [InlineData(1000u, (ushort)1000)]
    [InlineData(65535u, (ushort)65535)]
    [InlineData(65536u, (ushort)65535)]
    [InlineData(uint.MaxValue, (ushort)65535)]
    public void NeededKills_IsAU16CappedValue2_ZeroReadAsOne(uint value2, ushort expected)
        => Assert.Equal(expected, QuestProgressStore.NeededKills(Q.Row(2, 7000, value2)));

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 1)]
    [InlineData(255u, 255)]
    [InlineData(256u, 256)]                    // the turn-in side is uncapped: 256 items are taken for need 256
    [InlineData(70000u, 70000)]
    public void ObjectiveItemCount_IsUncappedValue2_ZeroReadAsOne(uint value2, int expected)
        => Assert.Equal(expected, QuestPrizeHandler.ObjectiveItemCount(Q.Row(4, 5000, value2)));

    [Theory]
    [InlineData(0u, (ushort)1)]
    [InlineData(1u, (ushort)1)]
    [InlineData(254u, (ushort)254)]
    [InlineData(255u, (ushort)255)]
    [InlineData(256u, (ushort)255)]            // use-item rows ARE byte capped (the session counter is a byte)
    [InlineData(100000u, (ushort)255)]
    public void UsesNeeded_IsAByteCappedValue3_ZeroReadAsOne(uint value3, ushort expected)
        => Assert.Equal(expected, QuestProgressStore.UsesNeeded(Q.Row(6, 0, 5000, value3)));

    // The 255 / 256 inconsistency on the collect side, one assertion set: the store reads min(bag, u16 need), the live tick
    // (LootService.AdvanceCollectQuestsAsync, pinned in QuestHandlerFlowCharacterizationTests) caps the need at a byte.
    [Fact]
    public void CollectCap_StoreSideIsU16_BoundaryAt255_256()
    {
        var rows = new[] { Q.Row(4, 5000, 256) };
        foreach (var (held, expectedValue, expectedMet) in new[] { (254, 254, false), (255, 255, false), (256, 256, true), (400, 256, true) })
        {
            Assert.Equal($"[{expectedValue}]/{expectedMet}", Show(QuestProgressStore.LoginProgress(rows, null, [Q.Stack(5000, held)])));
        }
    }

    // QUIRK: the closed-flag test at turn-in compares the byte-capped collect counter with the u16 need, so a collect need
    // of 256 or more never reads as "closed this session" (the 2427 would be sent again at the turn-in).
    [Theory]
    [InlineData((byte)4, (ushort)254, 254u, true)]
    [InlineData((byte)4, (ushort)255, 255u, true)]
    [InlineData((byte)4, (ushort)255, 256u, false)]
    [InlineData((byte)4, (ushort)256, 256u, true)]
    [InlineData((byte)5, (ushort)3, 3u, true)]
    [InlineData((byte)5, (ushort)2, 3u, false)]
    [InlineData((byte)4, (ushort)0, 0u, false)]     // need 0 reads as 1
    [InlineData((byte)4, (ushort)1, 0u, true)]
    public void ObjectiveClosedThisSession_CollectAndDeliver_CompareCounterWithU16Need(byte type, ushort progress, uint value2, bool expected)
        => Assert.Equal(expected, ClosedThisSession(Q.Row(type, 5000, value2), progress, killClosed: false));

    [Theory]
    [InlineData((byte)1, (ushort)0, false)]
    [InlineData((byte)1, (ushort)1, true)]    // talk, place, use-item and timed: the bare closed flag 1
    [InlineData((byte)3, (ushort)0, false)]
    [InlineData((byte)3, (ushort)1, true)]
    [InlineData((byte)6, (ushort)0, false)]
    [InlineData((byte)6, (ushort)1, true)]
    [InlineData((byte)34, (ushort)0, false)]
    [InlineData((byte)34, (ushort)1, true)]
    [InlineData((byte)34, (ushort)200, true)]
    [InlineData((byte)0, (ushort)9, false)]   // any other type is never "closed this session"
    [InlineData((byte)7, (ushort)9, false)]
    public void ObjectiveClosedThisSession_TalkPlaceUseItemTimed_AreTheBareFlag(byte type, ushort progress, bool expected)
        => Assert.Equal(expected, ClosedThisSession(Q.Row(type, 5000, 1), progress, killClosed: false));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ObjectiveClosedThisSession_Kill_IgnoresTheCounter_UsesTheClosedSet(bool killClosed, bool expected)
    {
        Assert.Equal(expected, ClosedThisSession(Q.Row(2, 7000, 1), progress: 0, killClosed));
        Assert.Equal(expected, ClosedThisSession(Q.Row(2, 7000, 1), progress: 500, killClosed));
    }

    private static bool ClosedThisSession(QuestData quest, ushort progress, bool killClosed)
        => (bool)Q.CallPrivateStatic(typeof(QuestPrizeHandler), "ObjectiveClosedThisSession", quest, progress, killClosed)!;

    // ---- 3. kill closure + restore ---------------------------------------------------------------------------------

    [Fact]
    public void AllKillObjectivesMet_FirstRowFromTheCounter_LaterRowsFromTheExtraCounts()
    {
        QuestData[] rows = [Q.Row(2, 7000, 2), Q.Row(2, 7001, 3)];
        var player = new PlayerState();

        Assert.False(LootService.AllKillObjectivesMet(player, Qid, rows));
        player.QuestKillProgress[Qid] = 2;
        Assert.False(LootService.AllKillObjectivesMet(player, Qid, rows));    // later row has no count

        LootService.RestoreKillProgress(player, Qid, rows, new ushort[] { 2, 2 });
        Assert.False(LootService.AllKillObjectivesMet(player, Qid, rows));
        LootService.RestoreKillProgress(player, Qid, rows, new ushort[] { 2, 3 });
        Assert.True(LootService.AllKillObjectivesMet(player, Qid, rows));
        Assert.Equal((ushort)3, LootService.ExtraKillCount(player, Qid, 7001));
        Assert.Equal((ushort)0, LootService.ExtraKillCount(player, Qid, 7000));    // row 0 never lives in the extra table
    }

    [Fact]
    public void AllKillObjectivesMet_NoObjectiveRowsOrAnyNonKillRowOrTargetlessRow_IsFalse()
    {
        var player = new PlayerState();
        player.QuestKillProgress[Qid] = 9;
        Assert.False(LootService.AllKillObjectivesMet(player, Qid, []));
        Assert.False(LootService.AllKillObjectivesMet(player, Qid, [Q.Row(0), Q.Row(0)]));
        Assert.False(LootService.AllKillObjectivesMet(player, Qid, [Q.Row(2, 7000, 1), Q.Row(1, 7001)]));
        Assert.False(LootService.AllKillObjectivesMet(player, Qid, [Q.Row(2, 0, 1)]));
    }

    // QUIRK: with a leading type-0 row the first KILL row has ROW index 1, so it reads the extra table, not QuestKillProgress
    // (the accept path and the first-row counter assume the objective sits at row 0).
    [Fact]
    public void AllKillObjectivesMet_RowIndexNotObjectiveIndexDecidesTheCounterSource()
    {
        QuestData[] rows = [Q.Row(0), Q.Row(2, 7000, 9)];
        var player = new PlayerState();
        player.QuestKillProgress[Qid] = 9;
        Assert.False(LootService.AllKillObjectivesMet(player, Qid, rows));
        LootService.RestoreKillProgress(player, Qid, rows, new ushort[] { 9 });
        Assert.Equal((ushort)9, LootService.ExtraKillCount(player, Qid, 7000));
        Assert.True(LootService.AllKillObjectivesMet(player, Qid, rows));
    }

    [Fact]
    public void RestoreKillProgress_ClampsToNeed_SkipsZeroAndNonKillRows_ClosesWhenAllMet()
    {
        // type-0 rows consume no count slot: the counts are indexed by objective, not by row
        QuestData[] rows = [Q.Row(2, 7000, 3), Q.Row(0), Q.Row(2, 7001, 2)];
        var player = new PlayerState();
        LootService.RestoreKillProgress(player, Qid, rows, new ushort[] { 9, 9 });
        Assert.Equal((ushort)3, player.QuestKillProgress[Qid]);               // 9 clamped to NeededKills 3
        Assert.Equal((ushort)2, LootService.ExtraKillCount(player, Qid, 7001));
        Assert.True(LootService.KillObjectiveClosed(player, Qid));

        var partial = new PlayerState();
        LootService.RestoreKillProgress(partial, Qid, rows, new ushort[] { 0, 1 });
        Assert.False(partial.QuestKillProgress.ContainsKey(Qid));             // a zero count leaves the counter alone
        Assert.Equal((ushort)1, LootService.ExtraKillCount(partial, Qid, 7001));
        Assert.False(LootService.KillObjectiveClosed(partial, Qid));

        LootService.ResetExtraKillProgress(player, Qid);
        Assert.False(LootService.KillObjectiveClosed(player, Qid));
        Assert.Equal((ushort)0, LootService.ExtraKillCount(player, Qid, 7001));
    }

    // ---- 4. use-item ------------------------------------------------------------------------------------------------

    [Fact]
    public void UseItemAddUse_CountsToNeeded_ThenReturnsNull()
    {
        var player = new PlayerState();
        Assert.Equal(1, QuestUseItemProgress.AddUse(player, Qid, rowCount: 2, rowIndex: 1, needed: 3));
        Assert.Equal(2, QuestUseItemProgress.AddUse(player, Qid, 2, 1, 3));
        Assert.Equal(3, QuestUseItemProgress.AddUse(player, Qid, 2, 1, 3));
        Assert.Null(QuestUseItemProgress.AddUse(player, Qid, 2, 1, 3));      // already met
        Assert.Null(QuestUseItemProgress.AddUse(player, Qid, 2, 1, 3));
        Assert.Equal(3, QuestUseItemProgress.Count(player, Qid, 1));
        Assert.Equal(0, QuestUseItemProgress.Count(player, Qid, 0));          // rows are counted separately
        Assert.Equal(1, QuestUseItemProgress.AddUse(player, Qid, 2, 0, 1));
        Assert.Null(QuestUseItemProgress.AddUse(player, Qid, 2, 0, 1));
    }

    [Fact]
    public void UseItemProgress_ResetRestoreAndRowCountChange()
    {
        var player = new PlayerState();
        QuestUseItemProgress.Restore(player, Qid, rowCount: 3, rowIndex: 2, count: 5);
        Assert.Equal(5, QuestUseItemProgress.Count(player, Qid, 2));
        QuestUseItemProgress.Restore(player, Qid, 3, 3, 9);                   // outside the rows: ignored
        QuestUseItemProgress.Restore(player, Qid, 3, -1, 9);
        Assert.Equal(0, QuestUseItemProgress.Count(player, Qid, 3));
        QuestUseItemProgress.Restore(player, Qid, 3, 0, -4);                  // negative stored count reads as 0
        Assert.Equal(0, QuestUseItemProgress.Count(player, Qid, 0));

        // A different row count throws the quest's counts away (the table is rebuilt).
        Assert.Equal(1, QuestUseItemProgress.AddUse(player, Qid, rowCount: 4, rowIndex: 2, needed: 9));
        Assert.Equal(1, QuestUseItemProgress.Count(player, Qid, 2));

        QuestUseItemProgress.Reset(player, Qid);
        Assert.Equal(0, QuestUseItemProgress.Count(player, Qid, 2));
        Assert.Equal(0, QuestUseItemProgress.Count(new PlayerState(), Qid, 0));
    }

    [Fact]
    public void CurrentMetValues_TalkOneUseCountOtherZero_AndTheClosedCounterShortcut()
    {
        QuestData[] rows = [Q.Row(1, 100), Q.Row(6, 0, 5000, 3), Q.Row(0), Q.Row(2, 7000, 1)];
        var player = new PlayerState();
        Assert.Equal(new ushort[] { 0, 0, 0 }, QuestProgressStore.CurrentMetValues(player, Qid, rows));

        QuestUseItemProgress.Restore(player, Qid, rows.Length, 1, 2);
        Assert.Equal(new ushort[] { 0, 2, 0 }, QuestProgressStore.CurrentMetValues(player, Qid, rows));

        // The bare closed flag (counter >= 1) with a talk / use-item FIRST row closes every talk row and lifts every
        // use-item row to its need.
        player.QuestKillProgress[Qid] = 1;
        Assert.Equal(new ushort[] { 1, 3, 0 }, QuestProgressStore.CurrentMetValues(player, Qid, rows));

        // The shortcut needs a talk / use-item row 0: with a kill row 0 the counter is a kill count, not a close.
        QuestData[] killFirst = [Q.Row(2, 7000, 5), Q.Row(1, 100)];
        var killer = new PlayerState();
        killer.QuestKillProgress[Qid] = 4;
        Assert.Equal(new ushort[] { 0, 0 }, QuestProgressStore.CurrentMetValues(killer, Qid, killFirst));
        Assert.Empty(QuestProgressStore.CurrentMetValues(killer, Qid, []));
    }

    [Fact]
    public void CurrentKillCounts_RowZeroFromTheCounter_LaterRowsFromTheExtraTable_NonKillRowsZero()
    {
        QuestData[] rows = [Q.Row(2, 7000, 5), Q.Row(4, 5000, 2), Q.Row(2, 7001, 5)];
        var player = new PlayerState();
        player.QuestKillProgress[Qid] = 4;
        LootService.RestoreKillProgress(player, Qid, rows, new ushort[] { 4, 0, 3 });
        Assert.Equal(new ushort[] { 4, 0, 3 }, QuestProgressStore.CurrentKillCounts(player, Qid, rows));

        // QUIRK: a collect FIRST row is not a kill row, so its overloaded counter (items held) is never reported as a kill count.
        QuestData[] collectFirst = [Q.Row(4, 5000, 9)];
        var collector = new PlayerState();
        collector.QuestKillProgress[Qid] = 7;
        Assert.Equal(new ushort[] { 0 }, QuestProgressStore.CurrentKillCounts(collector, Qid, collectFirst));
    }

    // ---- 5. talk ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TalkTrackedRows_TwoOrMoreTalkRows_OrOneLoneRowWhoseNpcIsNotTheRewardNpc()
    {
        // two or more talk rows with Value3 0 and a target: all of them
        Assert.Equal(new[] { 0, 1 }, QuestTalkProgress.TrackedRows([Q.Row(1, 10), Q.Row(1, 20)]));
        Assert.Equal(new[] { 0, 2 }, QuestTalkProgress.TrackedRows([Q.Row(1, 10), Q.Row(0), Q.Row(1, 20)]));
        // a Value3 != 0 row (closed by the client with a 1377) and a target-less row are not tracked
        Assert.Equal(new[] { 0, 2 }, QuestTalkProgress.TrackedRows([Q.Row(1, 10), Q.Row(1, 20, v3: 1), Q.Row(1, 30)]));
        Assert.Empty(QuestTalkProgress.TrackedRows([Q.Row(1, 10), Q.Row(1, 0)]));    // only one trackable left: not row-0-alone-with-others
        // a lone talk row 0, nothing else, whose NPC differs from the reward NPC
        Assert.Equal(new[] { 0 }, QuestTalkProgress.TrackedRows([Q.Row(1, 10, rewardNpc: 11)]));
        Assert.Equal(new[] { 0 }, QuestTalkProgress.TrackedRows([Q.Row(1, 10, rewardNpc: 11), Q.Row(0)]));
        Assert.Empty(QuestTalkProgress.TrackedRows([Q.Row(1, 10, rewardNpc: 10)]));  // talk NPC == reward NPC: left to the turn-in
        Assert.Empty(QuestTalkProgress.TrackedRows([Q.Row(1, 10, rewardNpc: 0), Q.Row(2, 7000, 1)]));   // another objective row
        Assert.Empty(QuestTalkProgress.TrackedRows([Q.Row(0), Q.Row(1, 10, rewardNpc: 11)]));            // lone talk row not at index 0
        Assert.Empty(QuestTalkProgress.TrackedRows([Q.Row(1, 10, v3: 1, rewardNpc: 11)]));
        Assert.Empty(QuestTalkProgress.TrackedRows([Q.Row(2, 7000, 1)]));
        Assert.Empty(QuestTalkProgress.TrackedRows([]));
    }

    [Fact]
    public void TalkMeet_RecordsTargetRows_ReportsAllMetOnlyWhenEveryTrackedRowIsMet()
    {
        QuestData[] rows = [Q.Row(1, 10), Q.Row(1, 20), Q.Row(1, 10, v3: 1)];
        var player = new PlayerState();

        Assert.Null(QuestTalkProgress.Meet(player, Qid, rows, 99));              // not a tracked NPC: nothing recorded
        Assert.Empty(QuestTalkProgress.MetRows(player, Qid));

        Assert.False(QuestTalkProgress.Meet(player, Qid, rows, 10)!.Value.AllMet);
        Assert.Equal(new[] { 0 }, QuestTalkProgress.MetRows(player, Qid).Order());
        Assert.False(QuestTalkProgress.Meet(player, Qid, rows, 10)!.Value.AllMet);   // meeting twice changes nothing
        Assert.True(QuestTalkProgress.Meet(player, Qid, rows, 20)!.Value.AllMet);    // row 2 (Value3 1) is not tracked
        Assert.Equal(new[] { 0, 1 }, QuestTalkProgress.MetRows(player, Qid).Order());

        // the returned set is a copy
        ((System.Collections.Generic.HashSet<int>)QuestTalkProgress.MetRows(player, Qid)).Add(77);
        Assert.Equal(new[] { 0, 1 }, QuestTalkProgress.MetRows(player, Qid).Order());

        // no tracked rows at all (talk NPC == reward NPC): Meet never records
        Assert.Null(QuestTalkProgress.Meet(player, Qid + 1, [Q.Row(1, 10, rewardNpc: 10)], 10));
    }

    [Fact]
    public void TalkMeet_TwoRowsOnTheSameNpc_AreMetByOneMeeting()
    {
        QuestData[] rows = [Q.Row(1, 10), Q.Row(1, 10)];
        var player = new PlayerState();
        Assert.True(QuestTalkProgress.Meet(player, Qid, rows, 10)!.Value.AllMet);
        Assert.Equal(new[] { 0, 1 }, QuestTalkProgress.MetRows(player, Qid).Order());
    }

    [Fact]
    public void TalkBeginResetRestoreAndPurge()
    {
        QuestData[] rows = [Q.Row(1, 10), Q.Row(1, 20)];
        var player = new PlayerState();
        Assert.False(QuestTalkProgress.AcceptedThisLogin(player, Qid));

        QuestTalkProgress.Restore(player, Qid, rows, [0, 5]);                    // 5 is not a tracked row
        Assert.Equal(new[] { 0 }, QuestTalkProgress.MetRows(player, Qid).Order());
        Assert.False(QuestTalkProgress.AcceptedThisLogin(player, Qid));          // a restore is not an accept

        QuestTalkProgress.Begin(player, Qid);
        Assert.True(QuestTalkProgress.AcceptedThisLogin(player, Qid));
        Assert.Empty(QuestTalkProgress.MetRows(player, Qid));                    // Begin forgets the met rows

        QuestTalkProgress.Restore(player, Qid, rows, [1]);
        QuestTalkProgress.Reset(player, Qid);
        Assert.Empty(QuestTalkProgress.MetRows(player, Qid));
        Assert.True(QuestTalkProgress.AcceptedThisLogin(player, Qid));           // Reset keeps the "begun" mark

        // Purge drops the rows of quests the player no longer holds
        QuestTalkProgress.Restore(player, Qid, rows, [0]);
        QuestTalkProgress.Purge(player);
        Assert.Empty(QuestTalkProgress.MetRows(player, Qid));
        QuestTalkProgress.Restore(player, Qid, rows, [0]);
        player.ActiveQuests.Add(Qid);
        QuestTalkProgress.Purge(player);
        Assert.Equal(new[] { 0 }, QuestTalkProgress.MetRows(player, Qid).Order());
    }

    [Fact]
    public async Task TalkMetElsewhereUnmet_IsFalseWhenTheTalkNpcIsNotASpawnableNpc()
    {
        // The last conjunct (IsMeetableNpc) needs an NPCInfo row placed by ZoneSpawn.json; an empty store has none, so the
        // decision is false whatever the other inputs say.
        var store = await Q.StoreAsync();
        var quest = Q.Row(1, 10, rewardNpc: 11);
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(quest, giverId: 12, objectiveClosed: false, acceptedThisLogin: true, store));
        Assert.False(QuestTalkProgress.IsMeetableNpc(store, 10));
        Assert.False(QuestTalkProgress.IsMeetableNpc(store, 0));
        // The cheap conjuncts short-circuit before it: a closed objective, the giver being the talk NPC, the talk NPC being the reward NPC
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(quest, 12, true, true, store));
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(quest, 10, false, true, store));
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(Q.Row(1, 10, rewardNpc: 10), 12, false, true, store));
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(Q.Row(2, 10), 12, false, true, store));
    }

    [Fact]
    public void SingleTalkRow_AndSideClosedBy()
    {
        Assert.True(QuestPrizeHandler.IsSingleTalkRow([Q.Row(1, 10), Q.Row(0)]));
        Assert.False(QuestPrizeHandler.IsSingleTalkRow([]));
        Assert.False(QuestPrizeHandler.IsSingleTalkRow([Q.Row(1, 10, v3: 1)]));      // Value3 != 0
        Assert.False(QuestPrizeHandler.IsSingleTalkRow([Q.Row(1, 0)]));               // no NPC
        Assert.False(QuestPrizeHandler.IsSingleTalkRow([Q.Row(1, 10), Q.Row(1, 10)])); // a second objective row
        Assert.False(QuestPrizeHandler.IsSingleTalkRow([Q.Row(2, 10, 1)]));
    }

    [Fact]
    public async Task SideClosedBy_ListsHeldSingleTalkQuestsWhoseNpcIsTheGiver_Ascending()
    {
        var store = await Q.StoreAsync(
            Q.Row(1, 10, questId: 300), Q.Row(1, 10, questId: 100), Q.Row(1, 10, questId: 200),
            Q.Row(1, 99, questId: 400), Q.Row(2, 10, 1, questId: 500), Q.Row(1, 10, questId: 600));
        var player = new PlayerState();
        foreach (var id in new uint[] { 300, 100, 200, 400, 500, 600, 999 }) player.ActiveQuests.Add(id);
        player.QuestKillProgress[200] = 1;                                           // already closed: skipped
        QuestFailState.MarkFailed(player, 600);                                      // failed: skipped

        Assert.Equal(new uint[] { 100, 300 }, QuestPrizeHandler.SideClosedBy(player, 300 + 1000, giverId: 10, store));
        Assert.Equal(new uint[] { 100 }, QuestPrizeHandler.SideClosedBy(player, 300, 10, store));   // the turn-in quest itself is excluded
        Assert.Empty(QuestPrizeHandler.SideClosedBy(player, 300, giverId: 0, store));
        Assert.Empty(QuestPrizeHandler.SideClosedBy(player, 300, giverId: 77, store));
    }

    // ---- 6. collect drop-tick planner -----------------------------------------------------------------------------

    [Fact]
    public void CollectRowsIndex_CollectAndDeliverRowsOfTheItem_NeedReadAsOneWhenZero_FileOrderPerQuest()
    {
        var rows = new Dictionary<uint, QuestData[]>
        {
            [20] = [Q.Row(2, 7000, 1, questId: 20), Q.Row(4, 100, 300, questId: 20)],
            [10] = [Q.Row(4, 100, 3, questId: 10), Q.Row(5, 100, 0, questId: 10), Q.Row(6, 100, 1, questId: 10), Q.Row(4, 0, 5, questId: 10), Q.Row(4, 101, 5, questId: 10)],
        };
        Assert.Equal(
            new[] { new QuestCollectDropTicks.CollectRow(20, 1, 300), new QuestCollectDropTicks.CollectRow(10, 0, 3), new QuestCollectDropTicks.CollectRow(10, 1, 1) },
            QuestCollectDropTicks.RowsCollecting(rows, 100));
        Assert.Equal(new[] { new QuestCollectDropTicks.CollectRow(10, 4, 5) }, QuestCollectDropTicks.RowsCollecting(rows, 101));
        Assert.Empty(QuestCollectDropTicks.RowsCollecting(rows, 0));        // a row with Value1 0 is never indexed
        Assert.Empty(QuestCollectDropTicks.RowsCollecting(rows, 555));
    }

    [Fact]
    public void PlanTicks_AscendingQuestThenRow_OnlyHeldQuests_MinOfHeldAndNeed_ClampedToU16()
    {
        var rows = new[]
        {
            new QuestCollectDropTicks.CollectRow(20, 1, 5), new QuestCollectDropTicks.CollectRow(10, 3, 2),
            new QuestCollectDropTicks.CollectRow(10, 0, 300), new QuestCollectDropTicks.CollectRow(30, 0, 70000),
        };
        Assert.Equal(
            new[] { new QuestCollectDropTicks.Tick(10, 0, 4), new QuestCollectDropTicks.Tick(10, 3, 2), new QuestCollectDropTicks.Tick(20, 1, 4) },
            QuestCollectDropTicks.PlanTicks(rows, new uint[] { 20, 10 }, heldInBag: 4));
        Assert.Equal(new[] { new QuestCollectDropTicks.Tick(20, 1, 0) }, QuestCollectDropTicks.PlanTicks(rows, new uint[] { 20 }, 0));
        Assert.Equal(new[] { new QuestCollectDropTicks.Tick(20, 1, 0) }, QuestCollectDropTicks.PlanTicks(rows, new uint[] { 20 }, -5));   // negative bag reads as 0
        Assert.Equal(new[] { new QuestCollectDropTicks.Tick(30, 0, 65535) }, QuestCollectDropTicks.PlanTicks(rows, new uint[] { 30 }, 70000));
        Assert.Equal(new[] { new QuestCollectDropTicks.Tick(10, 0, 256) }, QuestCollectDropTicks.PlanTicks([rows[2]], new uint[] { 10 }, 256)); // NOT byte capped, unlike the live collect tick
        Assert.Empty(QuestCollectDropTicks.PlanTicks(rows, new uint[] { 99 }, 4));
        Assert.Empty(QuestCollectDropTicks.PlanTicks([], new uint[] { 10 }, 4));
    }

    [Fact]
    public void LowerFirstRowCounter_OnlyRowZero_OnlyDownward_NeverCreates()
    {
        var player = new PlayerState();
        player.QuestKillProgress[Qid] = 5;
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(Qid, 1, 0));      // row 1: untouched
        Assert.Equal((ushort)5, player.QuestKillProgress[Qid]);
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(Qid, 0, 5));      // equal: untouched
        Assert.Equal((ushort)5, player.QuestKillProgress[Qid]);
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(Qid, 0, 9));      // higher: never raised
        Assert.Equal((ushort)5, player.QuestKillProgress[Qid]);
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(Qid, 0, 2));
        Assert.Equal((ushort)2, player.QuestKillProgress[Qid]);
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(Qid + 1, 0, 0)); // no counter: none created
        Assert.False(player.QuestKillProgress.ContainsKey(Qid + 1));
    }

    // ---- 7. ProgressJson codec -------------------------------------------------------------------------------------

    [Fact]
    public void ProgressJson_ExactLiterals()
    {
        Assert.Equal("{\"kill\":[4,0,5]}", QuestProgressStore.Encode(new ushort[] { 4, 0, 5 }));
        Assert.Equal("{\"kill\":[]}", QuestProgressStore.Encode(ReadOnlySpan<ushort>.Empty));
        Assert.Equal("{\"kill\":[3,0],\"met\":[0,1]}", QuestProgressStore.EncodeState(new ushort[] { 3, 0 }, new ushort[] { 0, 1 }));
        Assert.Equal("{\"kill\":[],\"met\":[]}", QuestProgressStore.EncodeState([], []));
        Assert.Equal("{\"kill\":[65535],\"met\":[255]}", QuestProgressStore.EncodeState(new ushort[] { 65535 }, new ushort[] { 255 }));
        // Key order is kill then met, with no spaces.
        Assert.Equal("{\"kill\":[1],\"met\":[2]}", QuestProgressStore.EncodeState(new ushort[] { 1 }, new ushort[] { 2 }));
    }

    [Fact]
    public void ProgressJson_RoundTrip()
    {
        var json = QuestProgressStore.EncodeState(new ushort[] { 7, 0, 1000 }, new ushort[] { 0, 1, 3 });
        Assert.Equal(new ushort[] { 7, 0, 1000 }, QuestProgressStore.Decode(json, 3));
        Assert.Equal(new ushort[] { 0, 1, 3 }, QuestProgressStore.DecodeMet(json, 3));
        Assert.True(QuestProgressStore.HasMetKey(json));
    }

    // The legacy form has no "met" key: HasMetKey is false (= run state unknown) and the met values read as all zero.
    [Fact]
    public void ProgressJson_LegacyDocumentWithoutMet_IsStateUnknown()
    {
        const string legacy = "{\"kill\":[4,5]}";
        Assert.False(QuestProgressStore.HasMetKey(legacy));
        Assert.Equal(new ushort[] { 0, 0 }, QuestProgressStore.DecodeMet(legacy, 2));
        Assert.Equal(new ushort[] { 4, 5 }, QuestProgressStore.Decode(legacy, 2));

        // An EMPTY met array still counts as the key being present.
        Assert.True(QuestProgressStore.HasMetKey("{\"kill\":[1],\"met\":[]}"));
        Assert.Equal(new ushort[] { 0, 0 }, QuestProgressStore.DecodeMet("{\"met\":[]}", 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"kill\":[1,2")]                // truncated
    [InlineData("[1,2,3]")]                      // root is an array
    [InlineData("\"kill\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"kill\":5}")]                 // key present but not an array
    [InlineData("{\"kill\":null}")]
    [InlineData("{\"kill\":{\"a\":1}}")]
    [InlineData("{\"other\":[1,2]}")]
    public void ProgressJson_MalformedOrNonArray_DecodesToAllZeros_AndNoMetKey(string? json)
    {
        Assert.Equal(new ushort[] { 0, 0, 0 }, QuestProgressStore.Decode(json, 3));
        Assert.Equal(new ushort[] { 0, 0, 0 }, QuestProgressStore.DecodeMet(json, 3));
        Assert.False(QuestProgressStore.HasMetKey(json));
    }

    [Fact]
    public void ProgressJson_DecodeEdges()
    {
        // more entries than rows are ignored; fewer leave zeros
        Assert.Equal(new ushort[] { 1, 2 }, QuestProgressStore.Decode("{\"kill\":[1,2,3,4]}", 2));
        Assert.Equal(new ushort[] { 1, 0, 0, 0 }, QuestProgressStore.Decode("{\"kill\":[1]}", 4));
        // clamped to the u16 range
        Assert.Equal(new ushort[] { 0, 65535, 65535, 5 }, QuestProgressStore.Decode("{\"kill\":[-9,65536,99999999999,5]}", 4));
        // non-integers read as 0 but still consume their slot
        Assert.Equal(new ushort[] { 0, 0, 0, 7 }, QuestProgressStore.Decode("{\"kill\":[\"3\",1.5,null,7]}", 4));
        Assert.Equal(new ushort[] { 0, 6 }, QuestProgressStore.Decode("{\"kill\":[{},6]}", 2));
        // a number too large for an Int64 reads as 0 (not clamped)
        Assert.Equal(new ushort[] { 0, 2 }, QuestProgressStore.Decode("{\"kill\":[99999999999999999999999,2]}", 2));
        // zero or negative row counts give an empty array
        Assert.Empty(QuestProgressStore.Decode("{\"kill\":[1]}", 0));
        Assert.Empty(QuestProgressStore.Decode("{\"kill\":[1]}", -3));
        // met key is independent of the kill key
        Assert.Equal(new ushort[] { 0, 9 }, QuestProgressStore.DecodeMet("{\"met\":[0,9],\"kill\":[5,5]}", 2));
        Assert.True(QuestProgressStore.HasMetKey("{\"met\":[0,9],\"kill\":[5,5]}"));
        Assert.False(QuestProgressStore.HasMetKey("{\"met\":5}"));
        Assert.False(QuestProgressStore.HasMetKey("{\"met\":null}"));
    }

    // ---- 8. login-progress state word ------------------------------------------------------------------------------

    [Fact]
    public void LoginProgress_NoObjectiveRows_IsEmptyAndNeverMet()
    {
        var (values, allMet) = QuestProgressStore.LoginProgress([Q.Row(0)], "{\"kill\":[9]}", null);
        Assert.Empty(values);
        Assert.False(allMet);
        Assert.False(QuestProgressStore.LoginProgress([], null, null).AllMet);
    }

    [Fact]
    public void LoginProgress_KillRows_RawStoredCountsAreNotClamped()
    {
        QuestData[] rows = [Q.Row(2, 7000, 3), Q.Row(2, 7001, 2)];
        Assert.Equal("[3,1]/False", Show(QuestProgressStore.LoginProgress(rows, "{\"kill\":[3,1]}", null)));
        Assert.Equal("[3,2]/True", Show(QuestProgressStore.LoginProgress(rows, "{\"kill\":[3,2]}", null)));
        // QUIRK: an over-need stored count is reported raw here, while RestoreKillProgress clamps it
        Assert.Equal("[9,2]/True", Show(QuestProgressStore.LoginProgress(rows, "{\"kill\":[9,2]}", null)));
        Assert.Equal("[0,0]/False", Show(QuestProgressStore.LoginProgress(rows, null, null)));
        // a targetless kill row is never met
        Assert.False(QuestProgressStore.LoginProgress([Q.Row(2, 0, 1)], "{\"kill\":[5]}", null).AllMet);
    }

    [Fact]
    public void LoginProgress_CollectAndDeliver_MinOfBagAndU16Need_NullBagCountsNothing()
    {
        foreach (var type in new byte[] { 4, 5 })
        {
            QuestData[] rows = [Q.Row(type, 5000, 3)];
            Assert.Equal("[2]/False", Show(QuestProgressStore.LoginProgress(rows, null, [Q.Stack(5000, 2)])));
            Assert.Equal("[3]/True", Show(QuestProgressStore.LoginProgress(rows, null, [Q.Stack(5000, 3)])));
            Assert.Equal("[3]/True", Show(QuestProgressStore.LoginProgress(rows, null, [Q.Stack(5000, 50)])));
            Assert.Equal("[3]/True", Show(QuestProgressStore.LoginProgress(rows, null, [Q.Stack(5000, 1), Q.Stack(5000, 2)])));   // stacks add up
            Assert.Equal("[0]/False", Show(QuestProgressStore.LoginProgress(rows, null, null)));
            Assert.Equal("[0]/False", Show(QuestProgressStore.LoginProgress(rows, null, [Q.Stack(5000, 3, storageType: 1)])));       // only StorageType 0 counts
            Assert.Equal("[1]/False", Show(QuestProgressStore.LoginProgress(rows, null, [Q.Stack(5000, 0)])));                         // a count of 0 counts as 1
        }
        // a collect row with no item is never met
        Assert.Equal("[0]/False", Show(QuestProgressStore.LoginProgress([Q.Row(4, 0, 1)], null, [Q.Stack(0, 5)])));
        // need 0 reads as 1
        Assert.Equal("[1]/True", Show(QuestProgressStore.LoginProgress([Q.Row(4, 5000, 0)], null, [Q.Stack(5000, 9)])));
    }

    [Fact]
    public void LoginProgress_TalkAndUseItemRows_ComeFromTheMetKey_LegacyDocumentIsUnmet()
    {
        QuestData[] talk = [Q.Row(1, 10)];
        Assert.Equal("[1]/True", Show(QuestProgressStore.LoginProgress(talk, "{\"kill\":[0],\"met\":[1]}", null)));
        Assert.Equal("[1]/True", Show(QuestProgressStore.LoginProgress(talk, "{\"met\":[7]}", null)));     // any non-zero reads as the closed 1
        Assert.Equal("[0]/False", Show(QuestProgressStore.LoginProgress(talk, "{\"met\":[0]}", null)));
        Assert.Equal("[0]/False", Show(QuestProgressStore.LoginProgress(talk, "{\"kill\":[1]}", null)));  // legacy: state unknown reads unmet

        QuestData[] use = [Q.Row(6, 0, 5000, 3)];
        Assert.Equal("[2]/False", Show(QuestProgressStore.LoginProgress(use, "{\"met\":[2]}", null)));
        Assert.Equal("[3]/True", Show(QuestProgressStore.LoginProgress(use, "{\"met\":[3]}", null)));
        Assert.Equal("[3]/True", Show(QuestProgressStore.LoginProgress(use, "{\"met\":[9]}", null)));      // min(stored, need)
        Assert.Equal("[1]/True", Show(QuestProgressStore.LoginProgress([Q.Row(6, 0, 5000, 0)], "{\"met\":[1]}", null)));
        Assert.Equal("[255]/True", Show(QuestProgressStore.LoginProgress([Q.Row(6, 0, 5000, 300)], "{\"met\":[300]}", null)));
    }

    // QUIRK: a place (3) or timed (34) quest, and any unknown Complete_Type, is NEVER met at login: the 2310 shows state 2 for it.
    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)34)]
    [InlineData((byte)7)]
    [InlineData((byte)99)]
    public void LoginProgress_OtherCompleteTypes_AreNeverMet(byte type)
    {
        var (values, allMet) = QuestProgressStore.LoginProgress([Q.Row(type, 5, 1)], "{\"kill\":[9],\"met\":[9]}", null);
        Assert.Equal(new ushort[] { 0 }, values);
        Assert.False(allMet);
    }

    [Fact]
    public void LoginProgress_MixedRows_TypeZeroRowsShiftNothing_AllRowsMustBeMet()
    {
        QuestData[] rows = [Q.Row(0), Q.Row(2, 7000, 1), Q.Row(1, 10), Q.Row(4, 5000, 2)];
        const string json = "{\"kill\":[1,0,0],\"met\":[0,1,0]}";
        Assert.Equal("[1,1,1]/False", Show(QuestProgressStore.LoginProgress(rows, json, [Q.Stack(5000, 1)])));
        Assert.Equal("[1,1,2]/True", Show(QuestProgressStore.LoginProgress(rows, json, [Q.Stack(5000, 2)])));
    }

    private static string Show((ushort[] Values, bool AllMet) r) => $"[{string.Join(",", r.Values)}]/{r.AllMet}";

    private static byte[] Element(CharacterQuest quest, GameDataStore? data, IReadOnlyList<InventoryItem>? bag)
    {
        using var w = new PacketWriter();
        QuestProgressStore.WriteLoginElement(w, quest, data, bag);
        return w.ToArray();
    }

    [Fact]
    public async Task WriteLoginElement_StateWordIs1WhenEveryObjectiveIsMet_Else2_NeverZero()
    {
        var store = await Q.StoreAsync(
            Q.Row(2, 7000, 2, questId: 5001), Q.Row(4, 5000, 3, questId: 5001),
            Q.Row(0, questId: 5002),
            Q.Row(1, 10, questId: 5003));
        var met = new CharacterQuest { CharacterId = 1, QuestId = 5001, ProgressJson = "{\"kill\":[2,0],\"met\":[0,0]}" };

        var running = Element(met, store, [Q.Stack(5000, 2)]);
        Assert.Equal(14 + 2 * 20, running.Length);
        Assert.Equal(2, running[0]);                                           // a: objective rows
        Assert.Equal(0, running[1]);                                           // b: always 0
        Assert.Equal(5001u, BinaryPrimitives.ReadUInt32LittleEndian(running.AsSpan(2)));
        Assert.Equal(QuestProgressStore.StateRunning, BinaryPrimitives.ReadUInt32LittleEndian(running.AsSpan(6)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(running.AsSpan(10)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(running.AsSpan(14)));   // row 0 progress
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(running.AsSpan(16)));   // row 1 progress: min(held 2, need 3)

        var done = Element(met, store, [Q.Stack(5000, 3)]);
        Assert.Equal(QuestProgressStore.StateMet, BinaryPrimitives.ReadUInt32LittleEndian(done.AsSpan(6)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(done.AsSpan(16)));
        Assert.All(done.AsSpan(18).ToArray(), b => Assert.Equal(0, b));

        // no bag: collect counts nothing
        Assert.Equal(QuestProgressStore.StateRunning, BinaryPrimitives.ReadUInt32LittleEndian(Element(met, store, null).AsSpan(6)));

        // a quest with no objective rows is written with one objective, no progress, state 2
        var none = Element(new CharacterQuest { CharacterId = 1, QuestId = 5002 }, store, null);
        Assert.Equal(14 + 20, none.Length);
        Assert.Equal(1, none[0]);
        Assert.Equal(QuestProgressStore.StateRunning, BinaryPrimitives.ReadUInt32LittleEndian(none.AsSpan(6)));

        // a quest missing from Quest_Info (or no data at all) is written with one objective, no progress, state 2
        foreach (var data in new GameDataStore?[] { store, null })
        {
            var unknown = Element(new CharacterQuest { CharacterId = 1, QuestId = 123, ProgressJson = "{\"kill\":[9]}" }, data, null);
            Assert.Equal(14 + 20, unknown.Length);
            Assert.Equal(QuestProgressStore.StateRunning, BinaryPrimitives.ReadUInt32LittleEndian(unknown.AsSpan(6)));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(unknown.AsSpan(14)));
        }

        // talk quest closed by "met"
        var talk = Element(new CharacterQuest { CharacterId = 1, QuestId = 5003, ProgressJson = "{\"kill\":[0],\"met\":[1]}" }, store, null);
        Assert.Equal(QuestProgressStore.StateMet, BinaryPrimitives.ReadUInt32LittleEndian(talk.AsSpan(6)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(talk.AsSpan(14)));
    }

    [Fact]
    public async Task RestoreObjectiveState_CollectFirstRowCounterIsU16_TalkAndUseItemCloseOnAllMet()
    {
        var store = await Q.StoreAsync(
            Q.Row(4, 5000, 300, questId: 6001),
            Q.Row(1, 10, questId: 6002, rewardNpc: 11),
            Q.Row(1, 10, questId: 6003, rewardNpc: 10),
            Q.Row(6, 0, 5000, 3, questId: 6004));

        // collect: the counter is min(bag, u16 need) with NO byte cap (the live tick and the accept cap at 255)
        var collector = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(collector, new CharacterQuest { CharacterId = 1, QuestId = 6001 }, store, [Q.Stack(5000, 280)]);
        Assert.Equal((ushort)280, collector.QuestKillProgress[6001]);
        var over = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(over, new CharacterQuest { CharacterId = 1, QuestId = 6001 }, store, [Q.Stack(5000, 999)]);
        Assert.Equal((ushort)300, over.QuestKillProgress[6001]);
        var empty = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(empty, new CharacterQuest { CharacterId = 1, QuestId = 6001 }, store, []);
        Assert.False(empty.QuestKillProgress.ContainsKey(6001));          // nothing held: no counter is created

        // talk with a document that carries "met": state known, tracked row restored, counter is the closed flag 1
        var talker = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(talker, new CharacterQuest { CharacterId = 1, QuestId = 6002, ProgressJson = "{\"kill\":[0],\"met\":[1]}" }, store, null);
        Assert.True(QuestProgressStore.PersistedStateKnown(talker, 6002));
        Assert.Equal(new[] { 0 }, QuestTalkProgress.MetRows(talker, 6002).Order());
        Assert.Equal((ushort)1, talker.QuestKillProgress[6002]);

        // legacy document (no "met"): state unknown, nothing restored
        var legacy = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(legacy, new CharacterQuest { CharacterId = 1, QuestId = 6002, ProgressJson = "{\"kill\":[0]}" }, store, null);
        Assert.False(QuestProgressStore.PersistedStateKnown(legacy, 6002));
        Assert.False(legacy.QuestKillProgress.ContainsKey(6002));
        Assert.Empty(QuestTalkProgress.MetRows(legacy, 6002));

        // talk NPC == reward NPC is not a tracked row: no met row, but the all-met counter still closes it
        var untracked = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(untracked, new CharacterQuest { CharacterId = 1, QuestId = 6003, ProgressJson = "{\"met\":[1]}" }, store, null);
        Assert.Empty(QuestTalkProgress.MetRows(untracked, 6003));
        Assert.Equal((ushort)1, untracked.QuestKillProgress[6003]);

        // use-item: partial keeps the use count only; all met sets the closed flag too
        var user = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(user, new CharacterQuest { CharacterId = 1, QuestId = 6004, ProgressJson = "{\"met\":[2]}" }, store, null);
        Assert.Equal(2, QuestUseItemProgress.Count(user, 6004, 0));
        Assert.False(user.QuestKillProgress.ContainsKey(6004));
        var finished = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(finished, new CharacterQuest { CharacterId = 1, QuestId = 6004, ProgressJson = "{\"met\":[9]}" }, store, null);
        Assert.Equal(3, QuestUseItemProgress.Count(finished, 6004, 0));   // min(stored, need)
        Assert.Equal((ushort)1, finished.QuestKillProgress[6004]);

        // unknown quest: nothing at all
        var stranger = new PlayerState();
        QuestProgressStore.RestoreObjectiveState(stranger, new CharacterQuest { CharacterId = 1, QuestId = 1234, ProgressJson = "{\"met\":[1]}" }, store, null);
        Assert.False(QuestProgressStore.PersistedStateKnown(stranger, 1234));
        Assert.Empty(stranger.QuestKillProgress);
    }

    [Fact]
    public async Task RestoreKillProgress_StoreWrapper_SkipsAllZeroDocumentsAndUnknownQuests()
    {
        var store = await Q.StoreAsync(Q.Row(2, 7000, 3, questId: 6101), Q.Row(2, 7001, 5, questId: 6101));
        var player = new PlayerState();
        QuestProgressStore.RestoreKillProgress(player, new CharacterQuest { CharacterId = 1, QuestId = 6101, ProgressJson = "{\"kill\":[0,0]}" }, store);
        Assert.Empty(player.QuestKillProgress);
        QuestProgressStore.RestoreKillProgress(player, new CharacterQuest { CharacterId = 1, QuestId = 6101, ProgressJson = "{\"kill\":[2,4]}" }, store);
        Assert.Equal((ushort)2, player.QuestKillProgress[6101]);
        Assert.Equal((ushort)4, LootService.ExtraKillCount(player, 6101, 7001));
        QuestProgressStore.RestoreKillProgress(player, new CharacterQuest { CharacterId = 1, QuestId = 777, ProgressJson = "{\"kill\":[2]}" }, store);
        Assert.False(player.QuestKillProgress.ContainsKey(777));
    }

    // ---- 9. reward selection + the objective check at turn-in ---------------------------------------------------------

    private static QuestData Reward(byte rewardType) => new()
    {
        Quest_ID = Qid, Reward_Type = rewardType,
        Reward_Item_ID1 = 11, Reward_Item_Count1 = 1, Reward_Item_ID2 = 0, Reward_Item_Count2 = 0, Reward_Item_ID3 = 13, Reward_Item_Count3 = 3,
        Reward_Item_ID7 = 17, Reward_Item_Count7 = 7,
    };

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void RewardItemsOf_Type1_PaysEverySlotWhateverTheChoice_EmptySlotsIncluded(uint choice)
    {
        var slots = QuestPrizeHandler.RewardItemsOf(Reward(1), choice);
        Assert.Equal(7, slots.Length);
        Assert.Equal(new (uint, uint)[] { (11, 1), (0, 0), (13, 3), (0, 0), (0, 0), (0, 0), (17, 7) }, slots);
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)5)]
    public void RewardItemsOf_PickTypes_PayOnlyTheChosenSlot_ValidPickIsAZeroBasedIndex(byte rewardType)
    {
        Assert.Equal(new (uint, uint)[] { (11, 1) }, QuestPrizeHandler.RewardItemsOf(Reward(rewardType), 0));
        Assert.Equal(new (uint, uint)[] { (13, 3) }, QuestPrizeHandler.RewardItemsOf(Reward(rewardType), 2));
        Assert.Equal(new (uint, uint)[] { (17, 7) }, QuestPrizeHandler.RewardItemsOf(Reward(rewardType), 6));
        // QUIRK: an empty slot IS returned (as (0,0)); the payer skips Id 0 afterwards
        Assert.Equal(new (uint, uint)[] { (0, 0) }, QuestPrizeHandler.RewardItemsOf(Reward(rewardType), 1));
        // an invalid pick pays nothing
        Assert.Empty(QuestPrizeHandler.RewardItemsOf(Reward(rewardType), 7));
        Assert.Empty(QuestPrizeHandler.RewardItemsOf(Reward(rewardType), 100));
        Assert.Empty(QuestPrizeHandler.RewardItemsOf(Reward(rewardType), QuestPrizeHandler.NoRewardChoice));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)6)]
    [InlineData((byte)255)]
    public void RewardItemsOf_OtherRewardTypes_PayNothing(byte rewardType)
    {
        Assert.Empty(QuestPrizeHandler.RewardItemsOf(Reward(rewardType), 0));
        Assert.Empty(QuestPrizeHandler.RewardItemsOf(Reward(rewardType), QuestPrizeHandler.NoRewardChoice));
    }

    [Fact]
    public void RewardExp_IsRewardExpWithTheExpRateApplied()
    {
        // The exp that feeds 2429 is EffectService.ApplyExpRate(Reward_EXP, rate): rate 0 is the identity, 6000 = +60%.
        // (Reward_Money and the completion count are inline in GrantRewardsAsync: pinned through the handler flow.)
        Assert.Equal(180000u, EffectService.ApplyExpRate(180000, 0));
        Assert.Equal(441600u, EffectService.ApplyExpRate(276000, 6000));
        Assert.Equal(0u, EffectService.ApplyExpRate(0, 6000));
        Assert.Equal(0u, EffectService.ApplyExpRate(180000, -10000));
    }

    [Fact]
    public async Task CheckObjective_Kill_FirstRowCounterThenEveryLaterKillRow()
    {
        QuestData[] rows = [Q.Row(2, 7000, 2), Q.Row(2, 7001, 2)];
        var store = await Q.StoreAsync(rows);
        var quest = store.Quests[Qid];
        var player = new PlayerState();
        var inventory = new GearInventory([]);

        var first = await QuestPrizeHandler.CheckObjectiveAsync(player, quest, store, inventory);
        Assert.False(first.Met);
        Assert.Null(first.Bag);
        player.QuestKillProgress[Qid] = 2;
        Assert.False((await QuestPrizeHandler.CheckObjectiveAsync(player, quest, store, inventory)).Met);   // later row 0/2
        LootService.RestoreKillProgress(player, Qid, rows, new ushort[] { 2, 1 });
        Assert.False((await QuestPrizeHandler.CheckObjectiveAsync(player, quest, store, inventory)).Met);
        LootService.RestoreKillProgress(player, Qid, rows, new ushort[] { 2, 2 });
        Assert.True((await QuestPrizeHandler.CheckObjectiveAsync(player, quest, store, inventory)).Met);
    }

    [Fact]
    public async Task CheckObjective_Collect_BagCountAgainstTheUncappedNeed_AndReturnsThatBag()
    {
        var store = await Q.StoreAsync(Q.Row(4, 5000, 256));
        var quest = store.Quests[Qid];
        var player = new PlayerState { CharacterId = 1 };

        var short255 = await QuestPrizeHandler.CheckObjectiveAsync(player, quest, store, new GearInventory([Q.Stack(5000, 255)]));
        Assert.False(short255.Met);
        Assert.NotNull(short255.Bag);
        var enough = await QuestPrizeHandler.CheckObjectiveAsync(player, quest, store, new GearInventory([Q.Stack(5000, 256)]));
        Assert.True(enough.Met);
        Assert.Single(enough.Bag!);

        var deliver = (await Q.StoreAsync(Q.Row(5, 5000, 0))).Quests[Qid];     // need 0 reads as 1
        Assert.True((await QuestPrizeHandler.CheckObjectiveAsync(player, deliver, store, new GearInventory([Q.Stack(5000, 1)]))).Met);
        Assert.False((await QuestPrizeHandler.CheckObjectiveAsync(player, deliver, store, new GearInventory([]))).Met);
    }

    // QUIRK: every objective type the server does not track at the turn-in is "met" (talk, place, use-item, timed, unknown),
    // and so are a targetless kill row and an itemless collect row.
    [Theory]
    [InlineData((byte)0, 0u)]
    [InlineData((byte)1, 10u)]
    [InlineData((byte)3, 10u)]
    [InlineData((byte)6, 10u)]
    [InlineData((byte)34, 10u)]
    [InlineData((byte)99, 10u)]
    [InlineData((byte)2, 0u)]
    [InlineData((byte)4, 0u)]
    [InlineData((byte)5, 0u)]
    public async Task CheckObjective_UntrackedTypes_AreMetWithoutABag(byte type, uint value1)
    {
        var store = await Q.StoreAsync(Q.Row(type, value1, 3));
        var result = await QuestPrizeHandler.CheckObjectiveAsync(new PlayerState(), store.Quests[Qid], store, new GearInventory([]));
        Assert.True(result.Met);
        Assert.Null(result.Bag);
    }

    [Fact]
    public void CountInBag_OnlyTheMainBag_ZeroCountReadsAsOne()
    {
        Assert.Equal(0, QuestPrizeHandler.CountInBag([], 5000));
        Assert.Equal(7, QuestPrizeHandler.CountInBag([Q.Stack(5000, 3), Q.Stack(5000, 4), Q.Stack(5001, 9)], 5000));
        Assert.Equal(0, QuestPrizeHandler.CountInBag([Q.Stack(5000, 3, storageType: 1)], 5000));
        Assert.Equal(1, QuestPrizeHandler.CountInBag([Q.Stack(5000, 0)], 5000));
        Assert.Equal(1, QuestPrizeHandler.CountInBag([Q.Stack(5000, -4)], 5000));
    }
}
