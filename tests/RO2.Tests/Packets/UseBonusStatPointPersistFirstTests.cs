using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// HSV progression, CP-G8: 1493 ReqUseBonusStatPoint persists FIRST and mirrors after. Oracle: when the save throws,
/// the character's Bonus*/primaries and the runtime primaries are unchanged, the answer is a BadState 2680 and no
/// 2595/2498 goes out; when it succeeds there is one save and the 2595 + 2498 + Ok 2680 follow.
/// </summary>
public class UseBonusStatPointPersistFirstTests
{
    private const byte Job = 4;        // has a Standard_Stat row
    private const byte Level = 10;     // BonusStatPointsEarned(10) = 9

    private static readonly Lazy<Task<GameDataStore>> Store = new(async () =>
    {
        var store = new GameDataStore();
        await store.LoadAsync(RequireDataDirectory());
        return store;
    });

    // ---- the pure pricing / persist helpers ----------------------------------------------------------------------------

    private static byte[] Request(params (byte Stat, ushort Amount)[] entries)
    {
        var p = new byte[Math.Max(14 + 3 * entries.Length, 13 + QuestProgressPackets.BonusStatEchoLength)];
        p[13] = (byte)entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            p[14 + 3 * i] = entries[i].Stat;
            p[15 + 3 * i] = (byte)(entries[i].Amount & 0xFF);
            p[16 + 3 * i] = (byte)(entries[i].Amount >> 8);
        }
        return p;
    }

    private static int One(int _) => 1;

    [Fact]
    public void Price_RequestThatFits_IsOk_AndLeavesTheInputUntouched()
    {
        int[] current = [0, 0, 0, 0, 0, 0];
        var plan = UseBonusStatPointHandler.Price(Request((0, 3), (4, 2)), 2, current, unspent: 5, One);

        Assert.Equal(QuestProgressPackets.BonusStatResultOk, plan.Result);
        Assert.Equal(5, plan.TotalCost);
        Assert.Equal(new[] { 3, 0, 0, 0, 2, 0 }, plan.Bonus);
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, current);
    }

    [Fact]
    public void Price_PastThePool_IsTooFewPoints_ShortVectorIsBadState_BadStatOrZeroCostIsOverCap()
    {
        int[] none = [0, 0, 0, 0, 0, 0];
        Assert.Equal(QuestProgressPackets.BonusStatResultTooFewPoints,
            UseBonusStatPointHandler.Price(Request((0, 3), (1, 3)), 2, none, unspent: 5, One).Result);

        // count says 2 but only one entry is in the body
        var shortBody = Request((0, 1));
        shortBody[13] = 2;
        Assert.Equal(QuestProgressPackets.BonusStatResultBadState,
            UseBonusStatPointHandler.Price(shortBody.AsSpan(0, 17), 2, none, unspent: 9, One).Result);

        Assert.Equal(QuestProgressPackets.BonusStatResultOverCap,
            UseBonusStatPointHandler.Price(Request((6, 1)), 1, none, unspent: 9, One).Result);
        Assert.Equal(QuestProgressPackets.BonusStatResultOverCap,
            UseBonusStatPointHandler.Price(Request((0, 1)), 1, none, unspent: 9, static _ => 0).Result);
    }

    [Fact]
    public async Task Persist_SaveThrows_PutsTheCharacterBack_AndReturnsTheError()
    {
        var c = NewCharacter();
        var seen = new List<int>();
        var error = await UseBonusStatPointHandler.PersistAsync(c, (10, 5, 0, 7, 3, 0), [3, 0, 0, 0, 0, 0],
            saved => { seen.Add(saved.BonusStr); throw new IOException("db down"); });

        Assert.IsType<IOException>(error);
        Assert.Equal(new[] { 3 }, seen);                       // the save saw the allocation...
        AssertUntouched(c);                                    // ...and it was rolled back
    }

    [Fact]
    public async Task Persist_SaveSucceeds_WritesBonusAndEffectivePrimaries()
    {
        var c = NewCharacter();
        var error = await UseBonusStatPointHandler.PersistAsync(c, (10, 5, 0, 7, 3, 0), [3, 0, 0, 0, 2, 0],
            _ => Task.CompletedTask);

        Assert.Null(error);
        Assert.Equal((3, 0, 0, 0, 2, 0), (c.BonusStr, c.BonusAgi, c.BonusInt, c.BonusWis, c.BonusVit, c.BonusLuk));
        Assert.Equal((13, 5, 0, 7, 5, 0), (c.Str, c.Agi, c.Int, c.Wis, c.Vit, c.Luk));
    }

    // ---- the handler: refused -> nothing persisted / no 2498; accepted -> one save + 2595, 2498, 2680 -------------------

    private static Character NewCharacter() => new()
    {
        Id = 4242, Job = Job, Level = Level, Str = 10, Agi = 5, Int = 0, Wis = 7, Vit = 3, Luk = 0,
        Hp = 700, MaxHp = 700, Mp = 40, MaxMp = 40,
    };

    private static void AssertUntouched(Character c)
    {
        Assert.Equal((0, 0, 0, 0, 0, 0), (c.BonusStr, c.BonusAgi, c.BonusInt, c.BonusWis, c.BonusVit, c.BonusLuk));
        Assert.Equal((10, 5, 0, 7, 3, 0), (c.Str, c.Agi, c.Int, c.Wis, c.Vit, c.Luk));
    }

    /// <summary>Counts SaveCharacterAsync and throws from it on demand; every other repository call is unexpected.</summary>
    private class SaveProbe : DispatchProxy
    {
        public int Saves;
        public bool Fail;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(ICharacterRepository.SaveCharacterAsync), targetMethod!.Name);
            Saves++;
            return Fail ? Task.FromException(new IOException("injected save failure")) : Task.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required ServiceProvider Services { get; init; }
        public required SaveProbe Repo { get; init; }
        public required PlayerState Player { get; init; }
        public required Character Character { get; init; }
        public required UseBonusStatPointHandler Handler { get; init; }

        public static async Task<Rig> StartAsync(bool failSave)
        {
            var store = await Store.Value;
            var net = await Harness.StartAsync(1);
            var players = new PlayerStateManager(new NoDirtySink());
            var character = NewCharacter();
            var player = new PlayerState
            {
                SessionId = net.Sessions[0].SessionId, EntityId = 0x11, CharacterId = character.Id, Name = "Tester",
                Character = character, Level = Level, Str = 10, Agi = 5, Int = 0, Wis = 7, Vit = 3, Luk = 0,
                Hp = 700, MaxHp = 700, Mp = 40, MaxMp = 40,
            };
            players.AddPlayer(player.SessionId, player);
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);
            var proxy = DispatchProxy.Create<ICharacterRepository, SaveProbe>();
            var probe = (SaveProbe)(object)proxy;
            probe.Fail = failSave;
            var services = new ServiceCollection()
                .AddSingleton(store)
                .AddSingleton(proxy)
                .AddSingleton<IInventoryRepository>(new NoGearInventory())
                .AddSingleton(new EffectService(players, broadcast, store, npcState: null!, loot: null!,
                    scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()))
                .BuildServiceProvider();
            return new Rig
            {
                Net = net, Services = services, Repo = probe, Player = player, Character = character,
                Handler = new UseBonusStatPointHandler(services.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<UseBonusStatPointHandler>.Instance, players, broadcast),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Net.DisposeAsync();
        }
    }

    [Fact]
    public async Task Handler_SaveThrows_NothingMirrored_No2498_BadState2680()
    {
        await using var rig = await Rig.StartAsync(failSave: true);
        var request = Request((0, 3));

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], request);

        Assert.Equal(1, rig.Repo.Saves);
        AssertUntouched(rig.Character);
        Assert.Equal((10, 5, 0, 7, 3, 0), (rig.Player.Str, rig.Player.Agi, rig.Player.Int, rig.Player.Wis, rig.Player.Vit, rig.Player.Luk));

        // the FIRST frame is the refusal itself: no 2595 / 2498 ahead of it
        var ack = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnAnsUseBonusStatPoint, ack.Op);
        Assert.Equal(QuestProgressPackets.BuildBonusStatPoint2680(rig.Player.EntityId,
            QuestProgressPackets.BonusStatResultBadState, request.AsSpan(13, QuestProgressPackets.BonusStatEchoLength)), ack.Body);

        // and nothing queued behind it
        await rig.Net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await rig.Net.Readers[0].NextAsync()));
    }

    [Fact]
    public async Task Handler_Accepted_OneSave_Mirrors_Sends2595Then2498Then2680Ok()
    {
        await using var rig = await Rig.StartAsync(failSave: false);
        var request = Request((0, 3));

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], request);

        Assert.Equal(1, rig.Repo.Saves);
        Assert.Equal(3, rig.Character.BonusStr);
        var baseStr = StatService.ClassBasePrimaries(await Store.Value, Job)!.Value.str;
        Assert.Equal(baseStr + 3, rig.Character.Str);
        Assert.Equal(baseStr + 3, rig.Player.Str);

        Assert.Equal(ZoneS2COpcodes.OnPcInitTrait, (await rig.Net.Readers[0].NextAsync()).Op);
        Assert.Equal(ZoneS2COpcodes.OnPcTrait, (await rig.Net.Readers[0].NextAsync()).Op);
        var ack = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnAnsUseBonusStatPoint, ack.Op);
        Assert.Equal(QuestProgressPackets.BuildBonusStatPoint2680(rig.Player.EntityId,
            QuestProgressPackets.BonusStatResultOk, request.AsSpan(13, QuestProgressPackets.BonusStatEchoLength)), ack.Body);
    }

    [Fact]
    public async Task Handler_RefusedByPricing_PersistsNothing_AndSendsOnlyTheRefusal()
    {
        await using var rig = await Rig.StartAsync(failSave: false);

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Request((0, 10)));   // 10 > 9 unspent

        Assert.Equal(0, rig.Repo.Saves);
        AssertUntouched(rig.Character);
        var ack = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnAnsUseBonusStatPoint, ack.Op);
        Assert.Equal(QuestProgressPackets.BonusStatResultTooFewPoints, BitConverter.ToUInt16(ack.Body, 11));
    }
}
