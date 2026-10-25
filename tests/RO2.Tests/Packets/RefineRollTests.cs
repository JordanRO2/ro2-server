using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Economy;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// 1668 refine roll, read FAITHFULLY from the shipped REFINE_RATE / REFINE_ITEM columns:
/// success = Succ_Rate + catalysts; on failure Break_Rate (+ catalysts) is the survival chance (no destroy here: a
/// failed survival resets to the band floor), then Down_Rate with Down_Type 1 = drop to the band floor (+19 -> +10,
/// not -1). The custom two-band model it replaced is preserved in custom/refine/.
/// </summary>
public sealed class RefineRollTests
{
    // ------------------------------------------------------------------ helpers

    /// <summary>Returns the queued values for <c>Next(int)</c>, in order, so a roll is fully scripted.</summary>
    private sealed class ScriptedRandom(params int[] values) : Random
    {
        private readonly Queue<int> _q = new(values);
        public override int Next(int maxValue) => _q.Dequeue();
        public int Remaining => _q.Count;
    }

    private static string DataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "REFINE_RATE.csv"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/REFINE_RATE.csv");
    }

    private static List<T> LoadTable<T>(string file) => TypedCsv.Load<T>(Path.Combine(DataDirectory(), file));

    private static readonly List<REFINERATE> RealRates = LoadTable<REFINERATE>("REFINE_RATE.csv");
    private static readonly List<REFINEITEM> RealItems = LoadTable<REFINEITEM>("REFINE_ITEM.csv");

    /// <summary>The row refining +cur uses (INDEX = target level = cur + 1).</summary>
    private static REFINERATE RealRow(int cur) => RealRates.Single(r => r.INDEX == cur + 1);

    private static readonly REFINEITEM Plain = new() { ITEM_ID = 1 };

    private static REFINERATE Row(int index, int succ, int brk, int down, byte downType = 1) => new()
    {
        INDEX = (byte)index, Need_Gold = 100, Succ_Rate = (ushort)succ, Break_Rate = (ushort)brk,
        Down_Rate = (ushort)down, Down_Type = downType,
    };

    // ------------------------------------------------------------------ the shipped table

    [Fact]
    public void RealTable_HasTwentyRows_AndOnlyIndexOneAndElevenAreGuaranteed()
    {
        Assert.Equal(20, RealRates.Count);
        Assert.Equal(Enumerable.Range(1, 20), RealRates.Select(r => (int)r.INDEX).OrderBy(i => i));
        Assert.All(RealRates, r => Assert.Equal(1, r.Down_Type));
        var guaranteed = RealRates.Where(r => r.Succ_Rate == 10000).Select(r => (int)r.INDEX).ToArray();
        Assert.Equal(new[] { 1, 11 }, guaranteed);
        Assert.All(RealRates.Where(r => r.Succ_Rate == 10000), r => Assert.Equal(0, r.Down_Rate));
    }

    [Theory]
    [InlineData(0)]    // +0 -> +1 uses INDEX 1
    [InlineData(10)]   // +10 -> +11 uses INDEX 11
    public void RealTable_PlusOneAndPlusElevenAlwaysSucceed(int cur)
    {
        var row = RealRow(cur);
        Assert.Equal(10000, row.Succ_Rate);
        var rng = new Random(12345);
        for (int i = 0; i < 5000; i++)
        {
            var r = RefineItemHandler.RollRefine(row, Plain, null, cur, 20, rng);
            Assert.Equal(0u, r.Code);
            Assert.Equal(cur + 1, r.NewLevel);
        }
    }

    // ------------------------------------------------------------------ success

    [Fact]
    public void Success_UsesSuccRateAndCatalystsAdditively_ClampedAt10000()
    {
        var row = Row(5, succ: 4800, brk: 0, down: 0);
        var blessing = new REFINEITEM { ITEM_ID = 2, Succ_Rate = 5000 };

        // 4800 + 5000 = 9800 -> a roll of 9799 succeeds, 9800 fails.
        Assert.Equal(0u, RefineItemHandler.RollRefine(row, blessing, null, 4, 20, new ScriptedRandom(9799, 0)).Code);
        Assert.NotEqual(0u, RefineItemHandler.RollRefine(row, blessing, null, 4, 20, new ScriptedRandom(9800, 0, 0)).Code);

        // Both slots add, and the sum is clamped to 10000 (no overflow past a certain success).
        var r = RefineItemHandler.RollRefine(row, blessing, blessing, 4, 20, new ScriptedRandom(9999, 9999));
        Assert.Equal(0u, r.Code);
        Assert.Equal(10000, r.SuccChance);
        Assert.Equal(5, r.NewLevel);
    }

    [Fact]
    public void Success_LuckyJump_RollsPlusThreeFirstThenPlusTwoElseOne()
    {
        var row = Row(3, succ: 10000, brk: 0, down: 0);
        var lucky = new REFINEITEM { ITEM_ID = 3, _3_GRADE_RATE = 1000, _2_GRADE_RATE = 5000 };

        int Jump(int jumpRoll) => RefineItemHandler.RollRefine(row, lucky, null, 2, 20, new ScriptedRandom(0, jumpRoll)).NewLevel - 2;

        Assert.Equal(3, Jump(0));      // [0, 1000) -> +3
        Assert.Equal(3, Jump(999));
        Assert.Equal(2, Jump(1000));   // [1000, 6000) -> +2
        Assert.Equal(2, Jump(5999));
        Assert.Equal(1, Jump(6000));   // else +1
    }

    [Fact]
    public void Success_LuckyJumpNeverPassesTheMaxLevel()
    {
        var row = Row(20, succ: 10000, brk: 0, down: 0);
        var sure3 = new REFINEITEM { ITEM_ID = 3, _3_GRADE_RATE = 10000 };
        Assert.Equal(20, RefineItemHandler.RollRefine(row, sure3, null, 19, 20, new ScriptedRandom(0, 0)).NewLevel);
    }

    // ------------------------------------------------------------------ failure

    [Fact]
    public void Failure_Plus19DropsToPlus10_NotPlus18()
    {
        // INDEX 20 (+19 -> +20): Down_Type 1 is "drop to the band floor". Survival fails (roll >= Break_Rate).
        var row = RealRow(19);
        Assert.Equal(1, row.Down_Type);
        var r = RefineItemHandler.RollRefine(row, Plain, null, 19, 20, new ScriptedRandom(9999, 9999));
        Assert.Equal(3u, r.Code);
        Assert.Equal(10, r.NewLevel);

        // A survived failure that still rolls inside Down_Rate drops to the floor as well, not by one.
        var d = RefineItemHandler.RollRefine(row, Plain, null, 19, 20, new ScriptedRandom(9999, 0, 0));
        Assert.Equal(3u, d.Code);
        Assert.Equal(10, d.NewLevel);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(10, 0)]
    [InlineData(11, 10)]
    [InlineData(15, 10)]
    [InlineData(19, 10)]
    public void Failure_DropGoesToTheBandFloor_NeverBelowIt(int cur, int floor)
    {
        var row = Row(cur + 1, succ: 0, brk: 0, down: 10000);
        var r = RefineItemHandler.RollRefine(row, Plain, null, cur, 20, new ScriptedRandom(9999, 9999));
        Assert.Equal(3u, r.Code);
        Assert.Equal(floor, r.NewLevel);
    }

    [Fact]
    public void Failure_SurvivalIsBreakRatePlusCatalysts_AndInfiniumAlwaysSurvives()
    {
        var infinium = new REFINEITEM { ITEM_ID = 4, ITEM_TYPE = 1, Break_Rate = 10000 };
        var row = Row(15, succ: 0, brk: 4800, down: 0);

        // Without it: 4800 survival -> a roll of 4800 fails to survive and resets to the floor (+10).
        var broken = RefineItemHandler.RollRefine(row, Plain, null, 14, 20, new ScriptedRandom(9999, 4800));
        Assert.Equal((3u, 10), (broken.Code, broken.NewLevel));
        // A roll of 4799 survives; Down_Rate 0 -> the level is kept.
        var kept = RefineItemHandler.RollRefine(row, Plain, null, 14, 20, new ScriptedRandom(9999, 4799, 9999));
        Assert.Equal((1u, 14), (kept.Code, kept.NewLevel));
        // With Infinium (+10000, clamped) every roll survives.
        var safe = RefineItemHandler.RollRefine(row, Plain, infinium, 14, 20, new ScriptedRandom(9999, 9999, 9999));
        Assert.Equal((1u, 14), (safe.Code, safe.NewLevel));
    }

    [Fact]
    public void Failure_InfiniumStillDropsByDownRate()
    {
        // Survival blocks the reset, not the Down_Rate drop (the +11 -> +10 case of the data notes).
        var infinium = new REFINEITEM { ITEM_ID = 4, ITEM_TYPE = 1, Break_Rate = 10000 };
        var row = Row(12, succ: 0, brk: 8800, down: 2600);
        var hit = RefineItemHandler.RollRefine(row, Plain, infinium, 11, 20, new ScriptedRandom(9999, 0, 2599));
        Assert.Equal((3u, 10), (hit.Code, hit.NewLevel));
        var miss = RefineItemHandler.RollRefine(row, Plain, infinium, 11, 20, new ScriptedRandom(9999, 0, 2600));
        Assert.Equal((1u, 11), (miss.Code, miss.NewLevel));
    }

    [Fact]
    public void Failure_DownTypeOtherThanOneNeverDrops()
    {
        var row = Row(8, succ: 0, brk: 10000, down: 10000, downType: 0);
        var r = RefineItemHandler.RollRefine(row, Plain, null, 7, 20, new ScriptedRandom(9999, 0));
        Assert.Equal((1u, 7), (r.Code, r.NewLevel));
    }

    // ------------------------------------------------------------------ the handler end to end

    private const uint CharId = 910101u;
    private const uint Armour = 41000102;
    private const uint Powder = 41000103;
    private const long StartZeny = 100000;

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required EconomyFixture Fx { get; init; }
        public required PlayerState Player { get; init; }
        public required RefineItemHandler Handler { get; init; }
        public ValueTask DisposeAsync() => Net.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(IReadOnlyList<REFINERATE> rates)
    {
        var net = await Harness.StartAsync(1);
        var fx = new EconomyFixture();
        var store = new GameDataStore();
        void Set(string prop, object value) => typeof(GameDataStore).GetProperty(prop)!.SetValue(store, value);
        Set(nameof(GameDataStore.Items), new[]
        {
            new ItemData { ID = Armour, Name = "Armour", Equip_Slot = 4 },
            new ItemData { ID = Powder, Name = "Powder", Stack_Max = 99 },
        }.ToDictionary(t => t.ID).ToFrozenDictionary());
        Set(nameof(GameDataStore.REFINERATE), rates);
        Set(nameof(GameDataStore.REFINEITEM), new[] { new REFINEITEM { ITEM_ID = Powder } });
        var player = fx.AddPlayer(CharId, StartZeny);
        player.SessionId = net.Sessions[0].SessionId;
        player.EntityId = 0x11u;
        var players = new PlayerStateManager(new NoDirtySink());
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var scopes = new ServiceCollection()
            .AddSingleton(fx.InventoryRepo)
            .AddSingleton(fx.CharacterRepo)
            .AddSingleton(store)
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new Rig
        {
            Net = net, Fx = fx, Player = player,
            Handler = new RefineItemHandler(scopes, NullLogger<RefineItemHandler>.Instance, players, broadcast),
        };
    }

    private static byte[] Refine1668(ulong equipUid, ulong matUid)
    {
        var p = new byte[2 + 50];
        p[0] = 1; p[1] = 50;
        var blob = p.AsSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(blob, 50);
        BinaryPrimitives.WriteUInt64LittleEndian(blob[20..], equipUid);
        BinaryPrimitives.WriteUInt64LittleEndian(blob[31..], matUid);
        return p;
    }

    /// <summary>The 2997 (result code at blob+8, outcome at blob+10, new level at blob+44), skipping other frames.</summary>
    private static async Task<(ushort Result, uint Outcome, byte NewLevel)> ReadResultAsync(Rig rig)
    {
        while (true)
        {
            var (op, body) = await rig.Net.Readers[0].NextAsync();
            if (op != ZoneS2COpcodes.OnRefineItem) continue;
            Assert.True(body.AsSpan().TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob));
            return (BinaryPrimitives.ReadUInt16LittleEndian(blob[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(blob[10..]),
                    blob[44]);
        }
    }

    [Fact]
    public async Task Handler_PlusTenToPlusEleven_UsesTheRealGuaranteedRow()
    {
        await using var rig = await StartAsync(RealRates);
        var armour = rig.Fx.GiveItem(CharId, Armour);
        armour.Refine = 10;
        await rig.Fx.InventoryRepo.UpdateItemAsync(armour);   // the fixture hands out a copy
        var powder = rig.Fx.GiveItem(CharId, Powder);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Refine1668(armour.Id, powder.Id));

        var (result, outcome, level) = await ReadResultAsync(rig);
        Assert.Equal((ushort)0, result);
        Assert.Equal(0u, outcome);
        Assert.Equal((byte)11, level);
        Assert.Equal((byte)11, rig.Fx.Inventory.Rows.Single(r => r.Id == armour.Id).Refine);
        Assert.Equal(StartZeny - RealRow(10).Need_Gold, rig.Player.Gold);   // Need_Gold of INDEX 11, charged
    }

    [Fact]
    public async Task Handler_FailedPlus19_DropsToPlus10_AndChargesNeedGold()
    {
        // INDEX 20 forced to a certain failure that does not survive: the item must land on +10, not +18.
        var rates = new[] { Row(20, succ: 0, brk: 0, down: 10000) };
        await using var rig = await StartAsync(rates);
        var armour = rig.Fx.GiveItem(CharId, Armour);
        armour.Refine = 19;
        await rig.Fx.InventoryRepo.UpdateItemAsync(armour);   // the fixture hands out a copy
        var powder = rig.Fx.GiveItem(CharId, Powder);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Refine1668(armour.Id, powder.Id));

        var (result, outcome, level) = await ReadResultAsync(rig);
        Assert.Equal((ushort)0, result);
        Assert.Equal(3u, outcome);
        Assert.Equal((byte)10, level);
        Assert.Equal((byte)10, rig.Fx.Inventory.Rows.Single(r => r.Id == armour.Id).Refine);
        Assert.Equal(StartZeny - 100, rig.Player.Gold);
    }

    [Fact]
    public async Task Handler_MaxRefineComesFromTheTable_NotAHardcoded20()
    {
        // The table stops at INDEX 2, so +2 cannot be refined further (it would have been allowed under a const 20).
        var rates = new[] { Row(1, 10000, 10000, 0), Row(2, 10000, 10000, 0) };
        await using var rig = await StartAsync(rates);
        var armour = rig.Fx.GiveItem(CharId, Armour);
        armour.Refine = 2;
        await rig.Fx.InventoryRepo.UpdateItemAsync(armour);   // the fixture hands out a copy
        var powder = rig.Fx.GiveItem(CharId, Powder);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Refine1668(armour.Id, powder.Id));

        var (result, _, _) = await ReadResultAsync(rig);
        Assert.Equal((ushort)1, result);
        Assert.Equal(StartZeny, rig.Player.Gold);
        Assert.Equal((byte)2, rig.Fx.Inventory.Rows.Single(r => r.Id == armour.Id).Refine);
    }
}
