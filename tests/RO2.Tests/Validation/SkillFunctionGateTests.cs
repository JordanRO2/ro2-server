using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Validation;

/// <summary>
/// Tests for the non-damage 1509 functions (revival, guild Emergency Call, warp items). Each requires an accepted
/// 1508/1561 before it, a live caster and, for a targeted cast, a resolved target on the map within reach. A 1509
/// without a pending cast is refused with the silent 2715 (code 20152) and has no effect; a 1508 followed by 1509
/// still succeeds.
/// </summary>
public class SkillFunctionGateTests
{
    private const uint Map = 1100;
    private const uint Revival = 511001;          
    private const uint EmergencyCall = 4000101;   
    private const uint Prontera = ItemWarpSkills.PronteraWarp;     
    private const uint Butterfly = ItemWarpSkills.ButterflyWing;   

    private static GateActor Caster(bool dead = false, params uint[] learned)
        => new(Map, 0, 0, dead, learned.ToDictionary(g => g, _ => (byte)1));

    private static GateTarget? Target(float x = 3f, bool dead = true, uint map = Map) => new GateTarget(map, x, 0, dead);

    
    [Fact]
    public void RevivalTargetsADeadPc_TheAliveRuleIsOff()
    {
        
        Assert.Equal(ActorRefusal.None,
            SkillDamageHandler.EvaluateFunctionGate(Caster(false, 5110), Revival, Target(dead: true), true, 10f, false));
        Assert.Equal(ActorRefusal.TargetDead,
            SkillDamageHandler.EvaluateFunctionGate(Caster(false, 5110), Revival, Target(dead: true), true, 10f, true));
    }

    [Fact]
    public void RevivalIsRefusedForAnUnresolvedCrossMapOrDistantTargetOrDeadCasterOrUnlearnedSkill()
    {
        Assert.Equal(ActorRefusal.TargetNotFound,
            SkillDamageHandler.EvaluateFunctionGate(Caster(false, 5110), Revival, null, true, 10f, false));
        Assert.Equal(ActorRefusal.CrossMap,
            SkillDamageHandler.EvaluateFunctionGate(Caster(false, 5110), Revival, Target(map: 1200), true, 10f, false));
        Assert.Equal(ActorRefusal.OutOfRange,
            SkillDamageHandler.EvaluateFunctionGate(Caster(false, 5110), Revival, Target(x: 500f), true, 10f, false));
        Assert.Equal(ActorRefusal.ActorDead,
            SkillDamageHandler.EvaluateFunctionGate(Caster(true, 5110), Revival, Target(), true, 10f, false));
        Assert.Equal(ActorRefusal.SkillNotLearned,
            SkillDamageHandler.EvaluateFunctionGate(Caster(), Revival, Target(), true, 10f, false));
    }

    [Fact]
    public void ItemAndGuildSkillsSkipTheLearnedCheck_ButNotTheLiveCasterCheck()
    {
        
        foreach (var id in new[] { EmergencyCall, Prontera, Butterfly })
        {
            Assert.Equal(ActorRefusal.None, SkillDamageHandler.EvaluateFunctionGate(Caster(), id, null, false, null, true));
            Assert.Equal(ActorRefusal.ActorDead, SkillDamageHandler.EvaluateFunctionGate(Caster(true), id, null, false, null, true));
        }
    }

    
    
    
    
    private static Task<bool> Govern(Rig rig, uint id, GateTarget? target = null, bool targeted = false,
        bool mustBeAlive = true, float? reach = 10f)
        => rig.Handler.GovernFunctionCastAsync(rig.Net.Sessions[0], rig.Caster, id, target, targeted, mustBeAlive, reach,
            consume: true, primaryTarget: 0, x: 1f, y: 2f, z: 3f);

    private static async Task AssertRefusedWith2715(Rig rig, uint id)
    {
        var (op, body) = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, op);
        Assert.Equal(Hex(SkillPackets.BuildUseSkillStartAck2715(id, 0, 0f, 1f, 2f, 3f,
            resultCode: SkillPackets.SkillResultOnCooldown)), Hex(body));
        await Task.Delay(100);
        Assert.Equal(0, rig.Net.Clients[0].Available);   
        Assert.Equal(0, rig.Net.Clients[1].Available);   
    }

    [Fact]
    public async Task ForgedEmergencyCallAndWarpItems_AreRefused_LegitOnesAreGovernedOnce()
    {
        await using var rig = await Rig.StartAsync();
        foreach (var id in new[] { EmergencyCall, Prontera, Butterfly })
        {
            
            Assert.False(await Govern(rig, id));
            await AssertRefusedWith2715(rig, id);

            
            SkillCastLandings.RecordPending(rig.Caster, id, 0, DateTime.UtcNow);
            Assert.True(await Govern(rig, id));
            Assert.Equal(0, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));
            Assert.False(await Govern(rig, id));
            await AssertRefusedWith2715(rig, id);
        }
    }

    [Fact]
    public async Task CastOfAnotherSkillDoesNotGovern_AndADeadCasterIsRefused()
    {
        await using var rig = await Rig.StartAsync();
        SkillCastLandings.RecordPending(rig.Caster, Butterfly, 0, DateTime.UtcNow);
        Assert.False(await Govern(rig, EmergencyCall));      
        await AssertRefusedWith2715(rig, EmergencyCall);

        rig.Caster.IsDead = true;
        Assert.False(await Govern(rig, Butterfly));          
        await AssertRefusedWith2715(rig, Butterfly);
        Assert.Equal(1, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));   
    }

    [Fact]
    public async Task ForgedWarpItem1509_ThroughTheHandler_IsRefusedAndMovesNothing()
    {
        await using var rig = await Rig.StartAsync();
        foreach (var id in new[] { Butterfly, Prontera })
        {
            
            ItemWarpSkills.GrantLanding(rig.Caster, id, Map, DateTime.UtcNow);
            await rig.Handler.RunAsync(rig.Net.Sessions[0], Request(id, 0), rig.Caster, rig.Services);

            var (op, _) = await rig.Net.Readers[0].NextAsync();
            Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, op);
            await Task.Delay(100);
            Assert.Equal(0, rig.Net.Clients[0].Available);
            Assert.Equal(Map, rig.Caster.MapId);
        }
    }

    [Fact]
    public async Task LegitButterflyWing1509_ThroughTheHandler_StaysSilent()
    {
        await using var rig = await Rig.StartAsync();
        SkillCastLandings.RecordPending(rig.Caster, Butterfly, 0, DateTime.UtcNow);

        await rig.Handler.RunAsync(rig.Net.Sessions[0], Request(Butterfly, 0), rig.Caster, rig.Services);

        
        await Task.Delay(100);
        Assert.Equal(0, rig.Net.Clients[0].Available);
    }

    
    [Fact]
    public async Task ForgedRevival_IsRefused_NoOffer_No2718_No3016()
    {
        await using var rig = await Rig.StartAsync();
        rig.Caster.LearnedSkills[5110] = 1;

        
        Assert.False(await Govern(rig, Revival, ActorGate.TargetOf(rig.Dead), targeted: true, mustBeAlive: false));
        await AssertRefusedWith2715(rig, Revival);
        Assert.Equal(0, rig.Dead.RevivalOfferCount);
    }

    [Fact]
    public async Task Revival_WithACastButUnlearnedUnresolvedOrDistantTarget_IsRefusedAndKeepsTheCast()
    {
        await using var rig = await Rig.StartAsync();
        SkillCastLandings.RecordPending(rig.Caster, Revival, rig.Dead.EntityId, DateTime.UtcNow);

        
        Assert.False(await Govern(rig, Revival, ActorGate.TargetOf(rig.Dead), true, false));
        await AssertRefusedWith2715(rig, Revival);

        
        rig.Caster.LearnedSkills[5110] = 1;
        Assert.False(await Govern(rig, Revival, null, true, false));
        await AssertRefusedWith2715(rig, Revival);

        
        rig.Dead.X = 5000f;
        Assert.False(await Govern(rig, Revival, ActorGate.TargetOf(rig.Dead), true, false));
        await AssertRefusedWith2715(rig, Revival);

        Assert.Equal(1, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));   
        Assert.Equal(0, rig.Dead.RevivalOfferCount);
    }

    [Fact]
    public async Task LegitRevival1508Then1509_StillOffersTheRevivalToTheDeadPc()
    {
        await using var rig = await Rig.StartAsync();
        rig.Caster.LearnedSkills[5110] = 1;
        SkillCastLandings.RecordPending(rig.Caster, Revival, rig.Dead.EntityId, DateTime.UtcNow);
        Assert.True(rig.Dead.IsDead);   

        Assert.True(await Govern(rig, Revival, ActorGate.TargetOf(rig.Dead), targeted: true, mustBeAlive: false));
        await rig.Handler.LandRevivalSkillAsync(rig.Caster, Revival, rig.Dead.EntityId, rig.Dead, castType: 2,
            rig.Services, CancellationToken.None);

        
        Assert.Equal(ZoneS2COpcodes.NfySkillDamage, (await rig.Net.Readers[0].NextAsync()).Op);
        Assert.Equal(ZoneS2COpcodes.NfySkillDamage, (await rig.Net.Readers[1].NextAsync()).Op);
        var (op, body) = await rig.Net.Readers[1].NextAsync();
        Assert.Equal(ZoneS2COpcodes.NfyRevivalSkill, op);
        Assert.Equal(Hex(RevivalSkillPackets.BuildNfyRevivalSkill3016(rig.Caster.X, rig.Caster.Y, rig.Caster.Z,
            rig.Caster.EntityId, Revival)), Hex(body));
        Assert.Equal(1, rig.Dead.RevivalOfferCount);
        Assert.Equal(0, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));   
        await Task.Delay(100);
        Assert.Equal(0, rig.Net.Clients[0].Available);
    }

    
 /// <summary>A 1509 body: <c>[u32 blobLen][blob]</c> with the skill, a primary target (when non-zero) and no hit list.</summary>
    private static byte[] Request(uint skillId, uint primaryTarget)
    {
        var blob = new byte[47];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, skillId);
        blob[4] = (byte)(primaryTarget != 0 ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(5), primaryTarget);
        blob[33] = 2;
        var payload = new byte[4 + blob.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)blob.Length);
        blob.CopyTo(payload, 4);
        return payload;
    }

    private sealed class Probe(PlayerStateManager pm, BroadcastService bc)
        : SkillDamageHandler(null!, NullLogger<SkillDamageHandler>.Instance, pm, bc)
    {
        public Task RunAsync(ProudSession s, byte[] body, PlayerState p, IServiceProvider services)
            => HandleAsync(s, body, p, services, CancellationToken.None);
    }

 /// <summary>Two connected PCs on one map: the caster (session 0) and a dead PC three units away (session 1).</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required Probe Handler { get; init; }
        public required PlayerState Caster { get; init; }
        public required PlayerState Dead { get; init; }
        public required ServiceProvider Services { get; init; }

        public static async Task<Rig> StartAsync()
        {
            var net = await Harness.StartAsync(2);
            var players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);
            PlayerState Pc(int i, float x, bool dead)
            {
                var sid = net.Sessions[i].SessionId;
                var p = new PlayerState { SessionId = sid, EntityId = PlayerStateManager.BuildActorHandle(sid), CharacterId = (uint)(70 + i),
                    MapId = Map, X = x, Y = 0, Hp = dead ? 0 : 500, MaxHp = 500, IsDead = dead, MapLoaded = true };
                players.AddPlayer(sid, p);
                return p;
            }
            var caster = Pc(0, 0f, false);
            var target = Pc(1, 3f, true);

            var collection = new ServiceCollection();
            collection.AddScoped<IInventoryRepository>(_ => new NoGearInventory());
            var bootstrap = collection.BuildServiceProvider();
            collection.AddSingleton(new DurabilityService(new GameDataStore(), NullLogger<DurabilityService>.Instance,
                bootstrap.GetRequiredService<IServiceScopeFactory>(), broadcast));
            var services = collection.BuildServiceProvider();
            return new Rig { Net = net, Handler = new Probe(players, broadcast), Caster = caster, Dead = target, Services = services };
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Net.DisposeAsync();
        }
    }
}
