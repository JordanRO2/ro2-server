using System.Buffers.Binary;
using RO2.Combats.Domain;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The field duel's fight (FieldPvPCombat, CombatService.ResolvePvpHit): who can hit whom, the HP-1 rule, and the
/// fought-out end on the wire.
/// </summary>
public class FieldPvPCombatTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private const uint Winner = 0x06EF4D11;
    private const uint Loser = 0x06EF5D11;

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    // ---- packets -------------------------------------------------------------------------------

    [Fact]
    public void Result2736Code3_AndTheLosersHp1Vitals_MatchTheDumpFrames()
    {
        // The winner's copy of the end frame: [self][winner][loser][3].
        Assert.Equal("011212005ae4114def06114def06115def060300",
            Hex(FieldPvPPackets.BuildResult2736(Winner, Winner, Loser, FieldPvPPackets.ResultFoughtOut)));
        // The loser's 2401 after the end pair: curHP 1, curSP 11975, maxHP 70353, maxSP 12012.
        Assert.Equal("011c1c00ebe2115def06115def0601000000c72e0000d1120100ec2e0000",
            Hex(ActorVitalsPackets.BuildActorAutoHeal2401(Loser, FieldPvPCombat.FoughtOutHp, 11975, 70353, 12012)));
    }

    // ---- HP ------------------------------------------------------------------------------------

    [Fact]
    public void TakeHit_LeavesTheLoserAtOne()
    {
        var victim = new PlayerState { Hp = 500, MaxHp = 500 };
        Assert.False(FieldPvPCombat.TakeHit(victim, 499));
        Assert.Equal(1, victim.Hp);
        victim.Hp = 100;
        Assert.True(FieldPvPCombat.TakeHit(victim, 100));   // exactly to 0 is lethal
        Assert.Equal(FieldPvPCombat.FoughtOutHp, victim.Hp);
        victim.Hp = 100;
        Assert.True(FieldPvPCombat.TakeHit(victim, 5000));
        Assert.Equal(FieldPvPCombat.FoughtOutHp, victim.Hp);
        Assert.False(victim.IsDead);
    }

    // ---- who ------------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheStartedOpponentIsAPvpTarget()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        PlayerState Pc(uint s)
        {
            var p = new PlayerState { SessionId = s, EntityId = PlayerStateManager.BuildActorHandle(s), MapId = 1100, Hp = 100 };
            players.AddPlayer(s, p);
            return p;
        }
        var a = Pc(0xA54001); var b = Pc(0xA54002); var bystander = Pc(0xA54003);
        var duel = FieldPvPService.Open(a, b, players);
        Assert.NotNull(duel);
        try
        {
            Assert.True(FieldPvPService.TryAccept(duel));
            // Accepted but still counting down: the clients have not armed the pair.
            Assert.Null(FieldPvPCombat.TryGetStartedOpponent(a, b.EntityId, players));

            Assert.True(FieldPvPService.TryMarkStarted(duel));
            Assert.Same(b, FieldPvPCombat.TryGetStartedOpponent(a, b.EntityId, players)?.Victim);
            Assert.Same(a, FieldPvPCombat.TryGetStartedOpponent(b, a.EntityId, players)?.Victim);
            Assert.Null(FieldPvPCombat.TryGetStartedOpponent(a, bystander.EntityId, players));
            Assert.Null(FieldPvPCombat.TryGetStartedOpponent(bystander, a.EntityId, players));
            Assert.Null(FieldPvPCombat.TryGetStartedOpponent(a, a.EntityId, players));
            Assert.Null(FieldPvPCombat.TryGetStartedOpponent(a, 0x0000D413, players));   // an NPC handle

            var found = FieldPvPCombat.OpponentsIn(new uint[] { 0x0000D413, b.EntityId, bystander.EntityId }, a, players);
            Assert.Equal(new[] { b.EntityId }, found.Keys);

            b.IsDead = true;
            Assert.Null(FieldPvPCombat.TryGetStartedOpponent(a, b.EntityId, players));
        }
        finally
        {
            FieldPvPService.TryClose(duel.Id, out _);
        }
        Assert.Null(FieldPvPCombat.TryGetStartedOpponent(b, a.EntityId, players));
    }

    // ---- damage ----------------------------------------------------------------------------------

    // CHARACTERIZATION - current AoV formula. The exact damage numbers below pin what CombatService computes today.
    [Fact]
    public void Characterization_ResolvePvpHit_MitigatesWithTheVictimsDefence_UnverifiedAoV()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var data = new GameDataStore();
        var combat = new CombatService(players, new NpcStateManager(data, NullLogger<NpcStateManager>.Instance),
            new BroadcastService(players, NullLogger<BroadcastService>.Instance), data, NullLogger<CombatService>.Instance);
        // No PCBaseStatus row and no weapon: the base damage is STR x 2 = 100 (CalculateBaseDamage). No Crit or
        // Parry, so the kind is 1 and the number deterministic.
        var attacker = new PlayerState { Str = 50, Level = 10 };
        var victim = new PlayerState { Level = 10, CombatDefense = 870 };
        // Defense% = 100*870/(870 + 870 + 10*35) = 41.63 -> round(100 * 0.5837) = 58.
        Assert.Equal((58, CombatService.DamageKindNormal), combat.ResolvePvpHit(attacker, victim, 0));
        victim.CombatDefense = 0;
        Assert.Equal((100, CombatService.DamageKindNormal), combat.ResolvePvpHit(attacker, victim, 0));
    }

    // CHARACTERIZATION - current AoV formula. The mitigation / crit / parry numbers pin the formulas as implemented.
    [Fact]
    public void Characterization_MitigateByDefense_AoVRateCappedAt75_UnverifiedAoV()
    {
        Assert.Equal(500, DefenseRules.Mitigate(1000, 870, 0, 0));         // 870/(870+870) = 50%
        Assert.Equal(250, DefenseRules.Mitigate(1000, 1_000_000, 0, 0));  // capped at 75%
        Assert.Equal(1, DefenseRules.Mitigate(1, 1_000_000, 0, 0));       // never below 1
        Assert.Equal(0, CombatRateRules.CritRatePct(0, 10, 10));
        Assert.Equal(0, CombatRateRules.ParryRatePct(0, 10, 10));
        // Crit/(0.02*Crit + 40 + 0.8*(DefLv - AtkLv)): 100/(2 + 40 + 0) and, for a victim 10 levels above, 100/(2 + 40 + 8).
        Assert.Equal(100.0 / 42.0, CombatRateRules.CritRatePct(100, 10, 10), 9);
        Assert.Equal(2.0, CombatRateRules.ParryRatePct(100, 20, 10), 9);
    }

    // ---- the fought-out end on the wire ------------------------------------------------------------

    [Fact]
    public async Task EndFoughtOut_SendsRetailsSequenceToBothDuelists()
    {
        // Effect_Table rows the removal reads: 119 knockdown and 101 stun are Influence_Type 2 (harmful), 60205
        // is Influence_Type 1 (the three ids and types as in data/Effect_Table.json).
        var dir = Path.Combine(Path.GetTempPath(), "ro2-fieldpvp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var data = new GameDataStore();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Effect_Table.json"),
                "[{\"ID\":119,\"Influence_Type\":2},{\"ID\":101,\"Influence_Type\":2},{\"ID\":60205,\"Influence_Type\":1}]");
            await data.LoadAsync(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var server = new ProudServer("test", NullLogger.Instance);
        var next = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionConnected += s => { Volatile.Read(ref next).TrySetResult(s); return Task.CompletedTask; };
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        // One client at a time, so each socket is known to be its session's.
        using var winnerClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await winnerClient.ConnectAsync(server.LocalEndPoint!);
        var winnerSession = await next.Task.WaitAsync(Generous);
        Volatile.Write(ref next, new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously));
        using var loserClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await loserClient.ConnectAsync(server.LocalEndPoint!);
        var loserSession = await next.Task.WaitAsync(Generous);

        FieldPvPService.Duel? duel = null;
        try
        {
            var players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var winner = new PlayerState { SessionId = winnerSession.SessionId, EntityId = Winner, MapId = 1100, Hp = 50000, MaxHp = 60000 };
            var loser = new PlayerState { SessionId = loserSession.SessionId, EntityId = Loser, MapId = 1100, Hp = 1, MaxHp = 70353, Mp = 11975, MaxMp = 12012 };
            players.AddPlayer(winner.SessionId, winner);
            players.AddPlayer(loser.SessionId, loser);
            loser.ActiveEffects.Add(new ActiveEffect { EffectId = 119, TargetHandle = Loser, MapId = 1100 });
            loser.ActiveEffects.Add(new ActiveEffect { EffectId = 60205, TargetHandle = Loser, MapId = 1100 });
            winner.ActiveEffects.Add(new ActiveEffect { EffectId = 101, TargetHandle = Winner, MapId = 1100 });

            // The loser is the challenger here (it carries side 1002 in the start pair).
            duel = FieldPvPService.Open(loser, winner, players);
            Assert.NotNull(duel);
            Assert.True(FieldPvPService.TryAccept(duel));
            Assert.True(FieldPvPService.TryMarkStarted(duel));

            var effects = new EffectService(players, broadcast, data, npcState: null!, loot: null!,
                scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create());
            var collection = new ServiceCollection();
            collection.AddSingleton(effects);
            collection.AddSingleton(data);
            await using var services = collection.BuildServiceProvider();

            Assert.True(await FieldPvPCombat.EndFoughtOutAsync(duel, winner, loser, services, broadcast, CancellationToken.None));

            // The winner's stream, in order: the loser's harmful effect leaves (2494 of 119 on the loser), then the
            // winner's own (2493 + 2494 of 101), then 2736 code 3 and the two end frames (winner first) and the
            // loser's 2401, the last three in their exact wire form.
            var w = new FrameReader(winnerClient);
            await AssertDelEffectAsync(w, 119, Loser);
            var (op, body) = await w.NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnDelEffectSuccess, op);
            Assert.Equal(101u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));
            await AssertDelEffectAsync(w, 101, Winner);
            Assert.Equal((ZoneS2COpcodes.NfyResultFiledPvP, "011212005ae4114def06114def06115def060300"), Hexed(await w.NextAsync()));
            Assert.Equal((ZoneS2COpcodes.OnChangeState, "011b1b00eae2114def06114def06000000000000000200000000000000"), Hexed(await w.NextAsync()));
            Assert.Equal((ZoneS2COpcodes.OnChangeState, "011b1b00eae2115def06115def06000000000000000200000000000000"), Hexed(await w.NextAsync()));
            Assert.Equal((ZoneS2COpcodes.OnActorAutoHeal, "011c1c00ebe2115def06115def0601000000c72e0000d1120100ec2e0000"), Hexed(await w.NextAsync()));

            // The loser's stream: its own 2493 first, and the 2736 names it as the loser, with itself first.
            var l = new FrameReader(loserClient);
            (op, body) = await l.NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnDelEffectSuccess, op);
            Assert.Equal(119u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));
            await AssertDelEffectAsync(l, 119, Loser);
            await AssertDelEffectAsync(l, 101, Winner);
            Assert.Equal((ZoneS2COpcodes.NfyResultFiledPvP,
                    Hex(FieldPvPPackets.BuildResult2736(Loser, Winner, Loser, FieldPvPPackets.ResultFoughtOut))),
                Hexed(await l.NextAsync()));

            // Only the harmful effects left; the duel is over, and the loser is alive at 1.
            Assert.Equal(new uint[] { 60205 }, loser.ActiveEffects.Select(e => e.EffectId));
            Assert.Empty(winner.ActiveEffects);
            Assert.False(FieldPvPService.TryGet(duel.Id, out _));
            Assert.Equal(1, loser.Hp);
            Assert.False(loser.IsDead);
        }
        finally
        {
            if (duel is not null) FieldPvPService.TryClose(duel.Id, out _);
            await server.StopAsync();
        }
    }

    // ---- a monster's hit fights the duel out --------------------------------------------------------

    [Fact]
    public async Task MonsterHitThatWouldKillADuelist_FightsTheDuelOut_WithTheOtherDuelistAsWinner()
    {
        // A monster hit ends the duel: NPC 0x00653413 hits 0x01B27511 for 7,987 and 0x01B23611 wins. The loser's vitals are
        // its HP-1 2401's (SP 12,434, maxima 58,881 / 12,434).
        const uint duelWinner = 0x01B23611, duelLoser = 0x01B27511, npcHandle = 0x00653413;
        var data = new GameDataStore();
        var server = new ProudServer("test", NullLogger.Instance);
        var next = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionConnected += s => { Volatile.Read(ref next).TrySetResult(s); return Task.CompletedTask; };
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var winnerClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await winnerClient.ConnectAsync(server.LocalEndPoint!);
        var winnerSession = await next.Task.WaitAsync(Generous);
        Volatile.Write(ref next, new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously));
        using var loserClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await loserClient.ConnectAsync(server.LocalEndPoint!);
        var loserSession = await next.Task.WaitAsync(Generous);

        FieldPvPService.Duel? duel = null;
        try
        {
            var players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            var winner = new PlayerState { SessionId = winnerSession.SessionId, EntityId = duelWinner, MapId = 1100, Hp = 40000, MaxHp = 50000 };
            var loser = new PlayerState { SessionId = loserSession.SessionId, EntityId = duelLoser, MapId = 1100, Hp = 5000, MaxHp = 58881, Mp = 12434, MaxMp = 12434 };
            players.AddPlayer(winner.SessionId, winner);
            players.AddPlayer(loser.SessionId, loser);
            duel = FieldPvPService.Open(winner, loser, players);
            Assert.NotNull(duel);
            Assert.True(FieldPvPService.TryAccept(duel));
            Assert.True(FieldPvPService.TryMarkStarted(duel));

            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton(new EffectService(players, broadcast, data, npcState: null!, loot: null!,
                scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
            // No inventory repository is registered, so the armour wear finds no gear and sends no 2503.
            collection.AddSingleton(sp => new DurabilityService(data, NullLogger<DurabilityService>.Instance,
                sp.GetRequiredService<IServiceScopeFactory>(), broadcast));
            await using var services = collection.BuildServiceProvider();
            var npc = new NpcRuntimeState { EntityOid = npcHandle, MapId = 1100, Hp = 9407 };

            Assert.True(await FieldPvPCombat.TryFightOutMonsterHitAsync(npc, loser, 7987, 1, players, services,
                broadcast, CancellationToken.None));

            // Alive at 1, SP kept, the duel over.
            Assert.Equal(FieldPvPCombat.FoughtOutHp, loser.Hp);
            Assert.Equal(12434, loser.Mp);
            Assert.False(loser.IsDead);
            Assert.False(FieldPvPService.TryGet(duel.Id, out _));

            // The winner's stream, with the monster's own SP in the 2696 as noted below.
            var w = new FrameReader(winnerClient);
            Assert.Equal((ZoneS2COpcodes.NfyNormalAttackStart, "011414003ee40000000001000000133465001175b201"), Hexed(await w.NextAsync()));
            Assert.Equal((ZoneS2COpcodes.NfyResultFiledPvP, "011212005ae41136b2011136b2011175b2010300"), Hexed(await w.NextAsync()));
            Assert.Equal((ZoneS2COpcodes.OnChangeState, "011b1b00eae21136b2011136b201000000000000000200000000000000"), Hexed(await w.NextAsync()));
            Assert.Equal((ZoneS2COpcodes.OnChangeState, "011b1b00eae21175b2011175b201000000000000000200000000000000"), Hexed(await w.NextAsync()));
            Assert.Equal((ZoneS2COpcodes.OnActorAutoHeal, "011c1c00ebe21175b2011175b201010000009230000001e6000092300000"), Hexed(await w.NextAsync()));
            await AssertMonsterHitAsync(w);
            Assert.Equal((ZoneS2COpcodes.OnChangeState, "011b1b00eae21175b2011175b201000000000000000200000000000000"), Hexed(await w.NextAsync()));

            // The loser's: the same frames, its own 2736 copy naming itself first.
            var l = new FrameReader(loserClient);
            Assert.Equal(ZoneS2COpcodes.NfyNormalAttackStart, (await l.NextAsync()).Op);
            Assert.Equal((ZoneS2COpcodes.NfyResultFiledPvP, "011212005ae41175b2011136b2011175b2010300"), Hexed(await l.NextAsync()));
            Assert.Equal(ZoneS2COpcodes.OnChangeState, (await l.NextAsync()).Op);
            Assert.Equal(ZoneS2COpcodes.OnChangeState, (await l.NextAsync()).Op);
            Assert.Equal(ZoneS2COpcodes.OnActorAutoHeal, (await l.NextAsync()).Op);
            await AssertMonsterHitAsync(l);
            Assert.Equal((ZoneS2COpcodes.OnChangeState, "011b1b00eae21175b2011175b201000000000000000200000000000000"), Hexed(await l.NextAsync()));
        }
        finally
        {
            if (duel is not null) FieldPvPService.TryClose(duel.Id, out _);
            await server.StopAsync();
        }

        static async Task AssertMonsterHitAsync(FrameReader reader)
        {
            var (op, body) = await reader.NextAsync();
            Assert.Equal(ZoneS2COpcodes.NfyAttackDamage, op);
            // The 2696 up to the attacker's HP (kind 1, damage 7,987, attacker 0x00653413 at HP 9,407), then
            // the loser at HP 1 with SP 12,434. body+36 is the attacker's SP, which NpcCombatService sends as 0 for
            // every monster hit.
            Assert.Equal("013232003fe4000000000101331f000000000000000000000000000013346500bf240000", Hex(body[..36]));
            Assert.Equal("00001175b201010000009230", Hex(body[38..50]));
        }
    }

    [Fact]
    public async Task MonsterHit_OutsideAStartedDuelOrShortOfZero_IsLeftToTheCaller()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var npc = new NpcRuntimeState { EntityOid = 0x00653413, MapId = 1100, Hp = 9407 };
        PlayerState Pc(uint s, int hp)
        {
            var p = new PlayerState { SessionId = s, EntityId = PlayerStateManager.BuildActorHandle(s), MapId = 1100, Hp = hp };
            players.AddPlayer(s, p);
            return p;
        }
        Task<bool> Hit(PlayerState victim, int damage)
            => FieldPvPCombat.TryFightOutMonsterHitAsync(npc, victim, damage, 1, players, services, broadcast,
                CancellationToken.None);

        var loner = Pc(0xA55001, 100);
        Assert.False(await Hit(loner, 500));     // in no duel: the caller's death runs
        Assert.Equal(100, loner.Hp);

        var a = Pc(0xA55002, 100);
        var b = Pc(0xA55003, 100);
        var duel = FieldPvPService.Open(a, b, players);
        Assert.NotNull(duel);
        try
        {
            Assert.True(FieldPvPService.TryAccept(duel));
            Assert.False(await Hit(a, 500));     // accepted, still counting down: not a fight yet
            Assert.Equal(100, a.Hp);

            Assert.True(FieldPvPService.TryMarkStarted(duel));
            Assert.False(await Hit(a, 99));      // short of 0: an ordinary hit
            Assert.Equal(100, a.Hp);

            b.IsDead = true;
            Assert.False(await Hit(a, 500));     // no live opponent to win it
            Assert.Equal(100, a.Hp);
            Assert.True(FieldPvPService.TryGet(duel.Id, out _));
        }
        finally
        {
            FieldPvPService.TryClose(duel.Id, out _);
        }
        Assert.False(await Hit(a, 500));         // closed by another path first
        Assert.Equal(100, a.Hp);
    }

    private static (ushort, string) Hexed((ushort Op, byte[] Body) frame) => (frame.Op, Hex(frame.Body));

    private static async Task AssertDelEffectAsync(FrameReader reader, uint effectId, uint handle)
    {
        var (op, body) = await reader.NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnDelEffect, op);
        // 2494: effect id at blob+8, handle at blob+12 (body+10 / body+14 behind the 1-byte compact length).
        Assert.Equal(effectId, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(10)));
        Assert.Equal(handle, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(14)));
    }

    /// <summary>Reads the internal RMI frames (<c>[13 57][w][len][01][u16 op][body]</c>) a test client receives.</summary>
    private sealed class FrameReader(Socket socket)
    {
        public async Task<(ushort Op, byte[] Body)> NextAsync()
        {
            while (true)
            {
                var head = await ReadExactAsync(3);
                Assert.Equal(0x13, head[0]);
                Assert.Equal(0x57, head[1]);
                var lengthBytes = await ReadExactAsync(head[2]);
                int length = head[2] switch
                {
                    1 => lengthBytes[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
                };
                var data = await ReadExactAsync(length);
                if (data.Length >= 3 && data[0] == 0x01)
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
            }
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }
    }
}
