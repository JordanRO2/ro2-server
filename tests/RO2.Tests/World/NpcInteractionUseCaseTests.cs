using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>
/// Pure characterization of the NPC-interaction decisions carved into World (B5 S29): the function-object control slot and its
/// use case, the multi-teleporter accept/refuse, the save-point flow, the offering rule and the map-load decisions.
/// </summary>
public class NpcInteractionUseCaseTests
{
    private sealed class Player { }

    private sealed class Slots : IFuncObjectControlSlots<Player>
    {
        private readonly ConditionalWeakTable<Player, FuncObjectControlSlot> _table = new();
        public FuncObjectControlSlot Slot(Player player) => _table.GetValue(player, _ => new FuncObjectControlSlot());
        public FuncObjectControlSlot? Find(Player player) => _table.TryGetValue(player, out var s) ? s : null;
    }

    private sealed class FakeCatalog : IWorldCatalog
    {
        private readonly Dictionary<uint, MapData> _maps = new();
        private readonly Dictionary<uint, PositionData> _positions = new();
        private readonly Dictionary<uint, NpcData> _npcs = new();

        public FakeCatalog Map(uint id, uint returnMap = 0, float x = 0, float y = 0, float z = 0)
        {
            _maps[id] = new MapData { Map_Index = id, Map_ID = returnMap, x = x, y = y, z = z };
            return this;
        }

        public FakeCatalog Position(uint id, uint area, float x, float y, float z)
        {
            _positions[id] = new PositionData { Position_ID = id, Area_ID = area, Position_X = x, Position_Y = y, Position_Z = z };
            return this;
        }

        public FakeCatalog Teleporter(uint npcId, uint group)
        {
            _npcs[npcId] = new NpcData { ID = npcId, Function_Type_1 = WarpDestinationRules.MultiTeleportFunctionType, Function_ID_1 = group };
            return this;
        }

        public MapData? GetMap(uint mapIndex) => _maps.GetValueOrDefault(mapIndex);
        public PositionData? GetPosition(uint positionId) => _positions.GetValueOrDefault(positionId);
        public NpcData? GetNpc(uint npcId) => _npcs.GetValueOrDefault(npcId);
    }

    // ---- FuncObjectControlSlot / FuncObjectControlUseCase -------------------------------------------------------------

    private const uint Target = 0x001E7A14u, Other = 0x0008B714u;

    [Fact]
    public void Slot_EndsOnlyTheMatchingTarget_AndOnlyOnce()
    {
        var slot = new FuncObjectControlSlot();
        slot.Begin(new FuncObjectControl(Target, 0x0E), now: 100);

        Assert.False(slot.TryEnd(Other, out _));
        Assert.Equal(new FuncObjectControl(Target, 0x0E), slot.Current);
        Assert.True(slot.TryEnd(Target, out var ended));
        Assert.Equal(new FuncObjectControl(Target, 0x0E), ended);
        Assert.False(slot.TryEnd(Target, out _));
        Assert.Null(slot.Current);
    }

    [Fact]
    public void Slot_BeginReplacesThePreviousControl()
    {
        var slot = new FuncObjectControlSlot();
        slot.Begin(new FuncObjectControl(Target, 0x0E), 1);
        slot.Begin(new FuncObjectControl(Other, null), 2);

        Assert.False(slot.TryEnd(Target, out _));
        Assert.True(slot.TryEnd(Other, out var c));
        Assert.Null(c.State);
    }

    [Fact]
    public void Slot_TryEndAfter_TooEarlyKeepsRunning_ThenEndsOnceElapsed()
    {
        var slot = new FuncObjectControlSlot();
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        slot.Begin(new FuncObjectControl(Target, 0x0D), began);
        var min = TimeSpan.FromMilliseconds(500);

        var early = began + System.Diagnostics.Stopwatch.Frequency / 10; // 100 ms later
        Assert.Equal(FuncObjectControlEnd.TooEarly, slot.TryEndAfter(Target, min, early, out var c1, out var e1));
        Assert.Equal(default, c1);
        Assert.True(e1 < min);
        Assert.NotNull(slot.Current);

        var late = began + System.Diagnostics.Stopwatch.Frequency; // 1 s later
        Assert.Equal(FuncObjectControlEnd.Ended, slot.TryEndAfter(Target, min, late, out var c2, out var e2));
        Assert.Equal(new FuncObjectControl(Target, 0x0D), c2);
        Assert.True(e2 >= min);
        Assert.Null(slot.Current);
        Assert.Equal(FuncObjectControlEnd.NotRunning, slot.TryEndAfter(Target, min, late, out _, out _));
    }

    [Fact]
    public void Slot_TryEndAfter_AnotherTargetIsNotRunning_AndLeavesTheControl()
    {
        var slot = new FuncObjectControlSlot();
        slot.Begin(new FuncObjectControl(Target, 0x0E), 0);
        Assert.Equal(FuncObjectControlEnd.NotRunning, slot.TryEndAfter(Other, TimeSpan.Zero, 10, out _, out _));
        Assert.NotNull(slot.Current);
    }

    [Fact]
    public void UseCase_EndingAPlayerThatNeverBegan_IsNotRunning_AndCreatesNoSlot()
    {
        var slots = new Slots();
        var p = new Player();

        Assert.False(FuncObjectControlUseCase.TryEnd(slots, p, Target, out var c));
        Assert.Equal(default, c);
        Assert.Equal(FuncObjectControlEnd.NotRunning,
            FuncObjectControlUseCase.TryEndAfter(slots, p, Target, TimeSpan.Zero, 5, out _, out var e));
        Assert.Equal(TimeSpan.Zero, e);
        Assert.Null(slots.Find(p));
    }

    [Fact]
    public void UseCase_BeginThenEnd_GoesThroughThePlayersSlot()
    {
        var slots = new Slots();
        var a = new Player();
        var b = new Player();
        FuncObjectControlUseCase.Begin(slots, a, new FuncObjectControl(Target, 0x0E), 1);

        Assert.False(FuncObjectControlUseCase.TryEnd(slots, b, Target, out _));
        Assert.True(FuncObjectControlUseCase.TryEnd(slots, a, Target, out var c));
        Assert.Equal(new FuncObjectControl(Target, 0x0E), c);
    }

    [Fact]
    public void ResetsStateOnCancel_NeedsAnEndedControlThatBroadcastAState()
    {
        Assert.True(FuncObjectControlUseCase.ResetsStateOnCancel(true, new FuncObjectControl(Target, 0)));
        Assert.False(FuncObjectControlUseCase.ResetsStateOnCancel(true, new FuncObjectControl(Target, null)));
        Assert.False(FuncObjectControlUseCase.ResetsStateOnCancel(false, new FuncObjectControl(Target, 0x0E)));
    }

    [Theory]
    [InlineData(FuncObjectControlEnd.Ended, (byte)0x0E, true)]
    [InlineData(FuncObjectControlEnd.Ended, null, false)]
    [InlineData(FuncObjectControlEnd.TooEarly, (byte)0x0E, false)]
    [InlineData(FuncObjectControlEnd.NotRunning, (byte)0x0E, false)]
    public void AnswersReward_OnlyAnEndedControlWithAState(FuncObjectControlEnd end, byte? state, bool expected)
        => Assert.Equal(expected, FuncObjectControlUseCase.AnswersReward(end, new FuncObjectControl(Target, state)));

    [Theory]
    [InlineData((ushort)0, 0.0)]
    [InlineData((ushort)1000, 500.0)]
    [InlineData((ushort)3001, 1500.5)]
    public void RewardMinElapsed_IsHalfTheCasting(ushort castingMs, double expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), FuncObjectControlUseCase.RewardMinElapsed(castingMs));

    [Fact]
    public void PortalDestination_NeedsThePositionAndItsMap()
    {
        var catalog = new FakeCatalog().Map(1100).Position(7, 1100, 1, 2, 3).Position(8, 9999, 1, 2, 3);

        Assert.Equal(7u, FuncObjectControlUseCase.PortalDestination(catalog, 7)!.Position_ID);
        Assert.Null(FuncObjectControlUseCase.PortalDestination(catalog, 8)); // map off Map_List
        Assert.Null(FuncObjectControlUseCase.PortalDestination(catalog, 9)); // no such Position_Info row
    }

    // ---- MultiTeleportUseCase -----------------------------------------------------------------------------------------

    private static FakeCatalog TeleportCatalog() => new FakeCatalog()
        .Map(3101).Position(274, 3101, 10, 20, 30).Teleporter(17260000, group: 1);

    private static readonly MultiTeleportUseCase.NpcRef Teleporter = new(17260000, 1100);

    [Fact]
    public void MultiTeleport_ResultCodesArePinned()
    {
        Assert.Equal((ushort)0, MultiTeleportUseCase.ResultAccepted);
        Assert.Equal((ushort)24323, MultiTeleportUseCase.ResultRefused);
        Assert.Equal(9, MultiTeleportUseCase.RequestLength);
    }

    [Fact]
    public void MultiTeleport_AcceptsAKnownSlot_WithItsPositionInfoRow()
    {
        var d = MultiTeleportUseCase.Decide(TeleportCatalog(), 9, false, 1100, Teleporter, 1);

        Assert.True(d.Accepted);
        Assert.Equal((ushort)0, d.Result);
        Assert.Equal(274u, d.Destination!.Position_ID);
    }

    [Fact]
    public void MultiTeleport_RefusalsAreCheckedInOrder_AllWith24323()
    {
        var catalog = TeleportCatalog();

        // short wins over everything, then dead, then the NPC gate, then the destination
        var shortBody = MultiTeleportUseCase.Decide(catalog, 8, true, 1100, null, 1);
        var dead = MultiTeleportUseCase.Decide(catalog, 9, true, 1100, null, 1);
        var noNpc = MultiTeleportUseCase.Decide(catalog, 9, false, 1100, null, 1);
        var otherMap = MultiTeleportUseCase.Decide(catalog, 9, false, 1200, Teleporter, 1);
        var badSlot = MultiTeleportUseCase.Decide(catalog, 9, false, 1100, Teleporter, 5);

        Assert.Equal(MultiTeleportUseCase.Refusal.ShortRequest, shortBody.Why);
        Assert.Equal(MultiTeleportUseCase.Refusal.PlayerDead, dead.Why);
        Assert.Equal(MultiTeleportUseCase.Refusal.NotNpcOnMap, noNpc.Why);
        Assert.Equal(MultiTeleportUseCase.Refusal.NotNpcOnMap, otherMap.Why);
        Assert.Equal(MultiTeleportUseCase.Refusal.NoDestination, badSlot.Why);
        foreach (var d in new[] { shortBody, dead, noNpc, otherMap, badSlot })
        {
            Assert.False(d.Accepted);
            Assert.Equal((ushort)24323, d.Result);
            Assert.Null(d.Destination);
        }
    }

    [Fact]
    public void MultiTeleport_ADestinationMapOffMapList_IsRefused()
    {
        var catalog = new FakeCatalog().Position(274, 3101, 10, 20, 30).Teleporter(17260000, 1); // map 3101 not loaded
        Assert.Equal(MultiTeleportUseCase.Refusal.NoDestination,
            MultiTeleportUseCase.Decide(catalog, 9, false, 1100, Teleporter, 1).Why);
    }

    // ---- TeleportSavePointUseCase -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void SavePoint_IgnoredWhenDeadFightingOrTrading(bool dead, bool combat, bool trading, bool ignored)
        => Assert.Equal(ignored, TeleportSavePointUseCase.IsIgnored(dead, combat, trading));

    [Fact]
    public void SavePoint_DecisionCarriesTheResolvedPoint_AndWhetherItsMapExists()
    {
        var catalog = new FakeCatalog().Map(3101, returnMap: 1100, x: 610, y: 969, z: 43).Map(1100);

        var fromInstance = TeleportSavePointUseCase.Decide(catalog, 3101, 0);
        Assert.Equal((1100u, 610f, 969f, 43f), (fromInstance.MapId, fromInstance.X, fromInstance.Y, fromInstance.Z));
        Assert.True(fromInstance.MapKnown);

        var noBind = TeleportSavePointUseCase.Decide(new FakeCatalog(), 1100, 0);
        Assert.Equal(SpawnRules.DefaultMapId, noBind.MapId);
        Assert.False(noBind.MapKnown); // the spawn map is not in this catalog
    }

    [Fact]
    public void SavePoint_InPlaceIsTheSameMap()
    {
        Assert.True(TeleportSavePointUseCase.IsInPlace(1200, 1200));
        Assert.False(TeleportSavePointUseCase.IsInPlace(1100, 1200));
    }

    // ---- OfferingUseCase ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5u, (byte)0, 10, 5u, (byte)10, true)]  // exactly the stack
    [InlineData(5u, (byte)0, 9, 5u, (byte)10, false)]  // short stack
    [InlineData(6u, (byte)0, 10, 5u, (byte)1, false)]  // another character's item
    [InlineData(5u, (byte)1, 10, 5u, (byte)1, false)]  // not in the bag
    public void CanOffer_NeedsOwnBagStackLargeEnough(uint owner, byte storage, int stack, uint me, byte count, bool expected)
        => Assert.Equal(expected, OfferingUseCase.CanOffer(owner, storage, stack, me, count));

    [Theory]
    [InlineData(10, (byte)3, 7, false)]
    [InlineData(10, (byte)10, 0, true)]
    [InlineData(3, (byte)3, 0, true)]
    [InlineData(1, (byte)255, 0, true)] // never negative
    public void After_RemovesTheStackWhenNothingIsLeft(int stack, byte count, int remaining, bool remove)
        => Assert.Equal((remaining, remove), OfferingUseCase.After(stack, count));

    [Fact]
    public void MessageStringId_IsTheFirstMatchingOfferingRow_OrZero()
    {
        var checks = new List<WorldEventCheckGroup>
        {
            new() { Check_Type = 1, Type_Data_1 = 100, Type_Data_2 = 200, Type_Data_4 = 11 }, // not an offering row
            new() { Check_Type = 2, Type_Data_1 = 100, Type_Data_2 = 200, Type_Data_4 = 22 },
            new() { Check_Type = 2, Type_Data_1 = 100, Type_Data_2 = 200, Type_Data_4 = 33 }, // later duplicate loses
            new() { Check_Type = 2, Type_Data_1 = 101, Type_Data_2 = 200, Type_Data_4 = 44 },
        };

        Assert.Equal((ushort)22, OfferingUseCase.MessageStringId(checks, 100, 200));
        Assert.Equal((ushort)44, OfferingUseCase.MessageStringId(checks, 101, 200));
        Assert.Equal((ushort)0, OfferingUseCase.MessageStringId(checks, 100, 201));
        Assert.Equal((ushort)0, OfferingUseCase.MessageStringId(new List<WorldEventCheckGroup>(), 100, 200));
    }

    // ---- MapLoadUseCase -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0UL, null)]
    [InlineData(1UL, 1u)]
    [InlineData(4294967295UL, 4294967295u)]
    [InlineData(4294967296UL, 4294967295u)]
    [InlineData(ulong.MaxValue, 4294967295u)]
    public void MasteryNotification_NothingForZero_ClampedToU32(ulong total, uint? expected)
        => Assert.Equal(expected, MapLoadUseCase.MasteryNotification(total));

    [Theory]
    [InlineData(true, true, MapLoadUseCase.GliderViewDataSource.Character)]
    [InlineData(true, false, MapLoadUseCase.GliderViewDataSource.Character)]
    [InlineData(false, true, MapLoadUseCase.GliderViewDataSource.Prebuilt)]
    [InlineData(false, false, MapLoadUseCase.GliderViewDataSource.None)]
    public void GliderViewData_PrefersTheCharacterRow(bool character, bool prebuilt, MapLoadUseCase.GliderViewDataSource expected)
        => Assert.Equal(expected, MapLoadUseCase.GliderViewData(character, prebuilt));

    [Theory]
    [InlineData(null, (byte)30, null)]
    [InlineData((byte)10, (byte)0, null)]
    [InlineData((byte)30, (byte)30, null)]
    [InlineData((byte)10, (byte)30, (byte)30)]
    public void LiveLevelOverride_OnlyWhenTheRowLagsALivePlayerLevel(byte? row, byte live, byte? expected)
        => Assert.Equal(expected, MapLoadUseCase.LiveLevelOverride(row, live));

    [Fact]
    public void MoveSpeedFallback_OnlyWhenNothingSentIt()
    {
        Assert.True(MapLoadUseCase.NeedsMoveSpeedFallback(false));
        Assert.False(MapLoadUseCase.NeedsMoveSpeedFallback(true));
    }
}
