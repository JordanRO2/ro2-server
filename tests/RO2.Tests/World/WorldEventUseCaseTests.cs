using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RO2.GameData.Tables;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Pure tests of <see cref="WorldEventUseCase"/>, the decisions the host applies for the world events (B5 S21).</summary>
public class WorldEventUseCaseTests
{
    private const uint EvA = 7;
    private const uint EvB = 9;
    private static readonly DateTime T0 = new(2026, 7, 11, 2, 0, 0, DateTimeKind.Utc);

    // Two events with the same shape: stage 1 waits on check group 10 (two offering rows), next stage 2.
    // Stage 2: 30 minutes, returns to 1.
    private static WorldEventTables Tables() => new(
        new List<WorldEventMain>
        {
            new() { World_Event_ID = EvA, Stage_Level = 1, Check_Group_ID_1 = 10, Next_Stage = 2, Return_Stage = 0 },
            new() { World_Event_ID = EvA, Stage_Level = 2, Stage_Duration_Time = 30, Return_Stage = 1 },
            new() { World_Event_ID = EvB, Stage_Level = 1, Check_Group_ID_1 = 10, Next_Stage = 2, Return_Stage = 0 },
            new() { World_Event_ID = EvB, Stage_Level = 2, Stage_Duration_Time = 30, Return_Stage = 1 },
        },
        new List<WorldEventCheckGroup>
        {
            new() { Check_Group_ID = 10, INDEX = 2, Check_Type = 2, Type_Data_1 = 100, Type_Data_2 = 201, Type_Data_3 = 5 },
            new() { Check_Group_ID = 10, INDEX = 1, Check_Type = 2, Type_Data_1 = 100, Type_Data_2 = 200, Type_Data_3 = 3 },
        },
        new List<WorldEventEventGroup>());

    [Fact]
    public void EntryStages_UsesInitialStagesWhenUnreadable_ElseLiveStagesInAscendingId()
    {
        var t = Tables();
        Assert.Equal(new (uint, byte)[] { (EvA, 1), (EvB, 1) }, WorldEventUseCase.EntryStages(null, t));

        var live = new[]
        {
            WorldEventStageRules.Enter(EvB, 2, T0, t),
            WorldEventStageRules.Enter(EvA, 1, T0, t),
        };
        Assert.Equal(new (uint, byte)[] { (EvA, 1), (EvB, 2) }, WorldEventUseCase.EntryStages(live, t));
    }

    [Fact]
    public void Load_EntersTheInitialStageWhenNothingUsableIsStored_AndKeepsAFittedStoredState()
    {
        var t = Tables();

        var (fresh, freshStore) = WorldEventUseCase.Load(null, EvA, 1, T0, t);
        Assert.True(freshStore);
        Assert.Equal(WorldEventStageRules.Enter(EvA, 1, T0, t).Stage, fresh.Stage);
        Assert.Equal(new uint[] { 0, 0 }, fresh.Counts);

        var gone = new WorldEventState(EvA, 9, T0, new uint[] { 1 }, T0);
        var (replaced, replacedStore) = WorldEventUseCase.Load(gone, EvA, 1, T0.AddHours(1), t);
        Assert.True(replacedStore);
        Assert.Equal((byte)1, replaced.Stage);
        Assert.Equal(T0.AddHours(1), replaced.StageEnteredUtc);

        var short1 = new WorldEventState(EvA, 1, T0, new uint[] { 4 }, T0);
        var (kept, keptStore) = WorldEventUseCase.Load(short1, EvA, 1, T0.AddHours(1), t);
        Assert.False(keptStore);
        Assert.Equal(new uint[] { 4, 0 }, kept.Counts);
        Assert.Equal(T0, kept.StageEnteredUtc);
    }

    [Fact]
    public void Offer_FirstCountedEventInAscendingIdWins_EvenWhenGivenOutOfOrder()
    {
        var t = Tables();
        var now = T0.AddMinutes(5);
        var live = new[]
        {
            WorldEventStageRules.Enter(EvB, 1, T0, t),
            WorldEventStageRules.Enter(EvA, 1, T0, t),
        };

        var d = WorldEventUseCase.Offer(live, t, 100, 201, 5, now);

        Assert.NotNull(d);
        Assert.Equal(EvA, d!.Previous.Id);
        Assert.Null(d.NextStage);
        Assert.Equal(new uint[] { 0, 5 }, d.CountsAfterOffering);
        Assert.Equal(new uint[] { 0, 5 }, d.Stored.Counts);
        Assert.Equal(now, d.Stored.UpdatedUtc);
        Assert.Equal(T0, d.Stored.StageEnteredUtc);
        Assert.Equal(new uint[] { 0, 0 }, live[1].Counts);
    }

    [Fact]
    public void Offer_CompletingTheStageStoresAFreshEntryIntoTheNextStage()
    {
        var t = Tables();
        var now = T0.AddMinutes(5);
        var live = new[] { new WorldEventState(EvA, 1, T0, new uint[] { 0, 5 }, T0) };

        var d = WorldEventUseCase.Offer(live, t, 100, 200, 3, now);

        Assert.NotNull(d);
        Assert.Equal((byte?)2, d!.NextStage);
        Assert.Equal(new uint[] { 3, 5 }, d.CountsAfterOffering);
        Assert.Equal(WorldEventStageRules.Enter(EvA, 2, now, t).Stage, d.Stored.Stage);
        Assert.Equal(now, d.Stored.StageEnteredUtc);
        Assert.Equal(now, d.Stored.UpdatedUtc);
        Assert.Empty(d.Stored.Counts);
    }

    [Fact]
    public void Offer_ReturnsNullWhenNoEventCounts()
    {
        var t = Tables();
        var live = new[] { WorldEventStageRules.Enter(EvA, 1, T0, t) };
        Assert.Null(WorldEventUseCase.Offer(live, t, 100, 999, 1, T0));
        Assert.Null(WorldEventUseCase.Offer(live, t, 100, 200, 0, T0));
        Assert.Null(WorldEventUseCase.Offer(Array.Empty<WorldEventState>(), t, 100, 200, 1, T0));
    }

    [Fact]
    public void RunTimeouts_MovesTimedOutStagesToTheirReturnStage_AndWaitsForTheNearestDeadline()
    {
        var t = Tables();
        var now = T0.AddMinutes(31);
        var live = new[]
        {
            WorldEventStageRules.Enter(EvB, 2, T0.AddMinutes(20), t),
            WorldEventStageRules.Enter(EvA, 2, T0, t),
        };

        var d = WorldEventUseCase.RunTimeouts(live, t, now, TimeSpan.FromHours(1));

        var only = Assert.Single(d.Transitions);
        Assert.Equal(EvA, only.Previous.Id);
        Assert.Equal((byte)2, only.Previous.Stage);
        Assert.Equal((byte)1, only.Entered.Stage);
        Assert.Equal(now, only.Entered.StageEnteredUtc);
        Assert.Equal(new uint[] { 0, 0 }, only.Entered.Counts);
        Assert.Equal(TimeSpan.FromMinutes(19), d.Wait);
    }

    [Fact]
    public void RunTimeouts_CapsTheWaitAtTheLongestWait_AndHasNoTransitionsBeforeTheDeadline()
    {
        var t = Tables();
        var live = new[] { WorldEventStageRules.Enter(EvA, 2, T0, t) };

        var d = WorldEventUseCase.RunTimeouts(live, t, T0.AddMinutes(1), TimeSpan.FromMinutes(1));

        Assert.Empty(d.Transitions);
        Assert.Equal(TimeSpan.FromMinutes(1), d.Wait);
    }

    [Fact]
    public async Task PushStageChange_CountsSends_SkipsNoSession_ReportsFailures_AndKeepsGoing()
    {
        var notifier = new FakeNotifier(
            new WorldRecipient(1, 10), new WorldRecipient(2, 10), new WorldRecipient(3, 11), new WorldRecipient(4, 11));
        notifier.NoSession.Add(1);
        notifier.Fails.Add(3);
        var failed = new List<uint>();

        var sent = await WorldEventUseCase.PushStageChangeAsync(notifier, EvA, 2, (r, _) => failed.Add(r.SessionId), CancellationToken.None);

        Assert.Equal(2, sent);
        Assert.Equal(new uint[] { 3 }, failed);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, notifier.Attempted);
    }

    [Fact]
    public async Task PushStageChange_RethrowsACancellationTheCallerAskedFor()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var notifier = new FakeNotifier(new WorldRecipient(1, 10));
        notifier.Cancels.Add(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WorldEventUseCase.PushStageChangeAsync(notifier, EvA, 2, (_, _) => { }, cts.Token));
    }

    private sealed class FakeNotifier : IWorldNotifier
    {
        private readonly WorldRecipient[] _players;
        public HashSet<uint> NoSession { get; } = new();
        public HashSet<uint> Fails { get; } = new();
        public HashSet<uint> Cancels { get; } = new();
        public List<uint> Attempted { get; } = new();

        public FakeNotifier(params WorldRecipient[] players) => _players = players;

        public IReadOnlyList<WorldRecipient> OnlinePlayers() => _players;

        public Task<bool> SendStageChangeAsync(WorldRecipient recipient, uint eventId, byte stage, CancellationToken ct)
        {
            Attempted.Add(recipient.SessionId);
            if (Cancels.Contains(recipient.SessionId)) throw new OperationCanceledException(ct);
            if (Fails.Contains(recipient.SessionId)) throw new InvalidOperationException("send failed");
            return Task.FromResult(!NoSession.Contains(recipient.SessionId));
        }
    }
}
