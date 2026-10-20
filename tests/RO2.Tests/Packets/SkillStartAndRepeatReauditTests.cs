using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Skill start and repeat handling: the 1561 repeat refusal, the effects a skill reset (2544) ends, the 2716 angle and
/// position echo, and the answer to a burst-duplicate 1508 (2715). Shipped data tables are found by walking up from the
/// test output directory.
/// </summary>
public class SkillStartAndRepeatReauditTests
{
    
 /// <summary>The refusal is 2711 (skill 9000211). A repeat within 1,000 ms of the last answered request is refused, and a refused request does not move the window.</summary>
    [Fact]
    public void ItemSkillRepeat_RefusesWithinTheWindowOfTheLastAnswer()
    {
        Assert.Equal("0110100049e40000000011e2ae0a13558900", Hex(ActorStatePackets.BuildNfySkillCastingCancel(0x0AAEE211, 9000211)));
        var player = new PlayerState();
        var t0 = new DateTime(2026, 7, 3, 1, 0, 0, DateTimeKind.Utc);
        
        
        Assert.False(ItemSkillRepeatGuard.IsRepeat(player, t0));
        ItemSkillRepeatGuard.MarkAnswered(player, t0);
        Assert.True(ItemSkillRepeatGuard.IsRepeat(player, t0.AddMilliseconds(128)));
        Assert.True(ItemSkillRepeatGuard.IsRepeat(player, t0.AddMilliseconds(999)));   
        Assert.False(ItemSkillRepeatGuard.IsRepeat(player, t0.AddMilliseconds(1000)));
        Assert.False(ItemSkillRepeatGuard.IsRepeat(new PlayerState(), t0.AddMilliseconds(10)));
    }

    
 /// <summary>
 /// The reset ends the Init_Handling 0 skills, in ascending order, and keeps the Init_Handling 1 skills. A skill id
 /// missing from the table is kept.
 /// </summary>
    [Fact]
    public async Task SkillReset_EndsTheInitHandling0Effects_Ascending()
    {
        var store = await LoadStoreAsync("Effect_Table.json");
        var player = new PlayerState();
        foreach (var id in new uint[] { 40011, 102002, 40000, 105202, 40012, 40009, 40010, 40000, 999999999 })
            player.ActiveEffects.Add(new ActiveEffect { EffectId = id });
        Assert.Equal(new uint[] { 40000, 40009, 40010, 40011, 40012 }, TalentResetEffects.EffectsEndedByReset(player, store));
        Assert.True(TalentResetEffects.IsEndedByReset(store, RidingService.RidingEffectId));
        Assert.False(TalentResetEffects.IsEndedByReset(store, 60205));
    }

    
 /// <summary>
 /// The caster casts skill 220703 on itself. The 2716 blob echoes the request's f32 and position triple at blob
 /// offsets 32 and 36-47.
 /// </summary>
    [Fact]
    public void UseSkillStart2716_EchoesTheRequestsAngleAndTriple_IsTheArchiveFrame()
    {
        var request = Convert.FromHexString(
            "1f5e030097774144334f3d44cc675d42110efc06013304e7c7ee7bf3028a14ae3f90963c44be403c4486425c420000");
        var echo = SkillStartHandler.ReadAnswerEcho(request);
        var body = SkillPackets.BuildUseSkillStart2716(0x06FC0E11, BinaryPrimitives.ReadUInt32LittleEndian(request),
            53434, 11704, echo.TargetHandle, variant: echo.Variant,
            angle: SkillStartHandler.ReadCastAngle(request), x: echo.X, y: echo.Y, z: echo.Z);
        Assert.Equal("014141004ae4000000001f5e0300110efc06bad00000b82d000000000000000000008a14ae3f90963c44be403c4486425c42"
            + "01110efc06000000000000000000000000", Hex(body));
    }

 /// <summary>
 /// A burst-duplicate 1508 while the first cast's cooldown runs. Before the cast's 1509 it gets no answer; after it, it
 /// gets the 20152 refusal (2715 with cooldown 0, target and variant echoed) and a 2962.
 /// </summary>
    [Fact]
    public async Task SkillStartBurstDuplicate_IsSilentBeforeTheLanding_AndRefusedAfterIt()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var sid = net.Sessions[0].SessionId;
        var caster = new PlayerState { SessionId = sid, EntityId = (sid << 8) | 0x11, MapId = 1100, MapLoaded = true,
            Level = 1, Hp = 500, Mp = 100 };
        players.AddPlayer(sid, caster);
        var collection = new ServiceCollection();
        collection.AddSingleton(await LoadStoreAsync("Skill_Table_SkillLevel.json"));
        await using var services = collection.BuildServiceProvider();
        
        
        
        
        var castInstant = new DateTime(2026, 7, 21, 15, 55, 2, DateTimeKind.Utc);
        var handler = new SkillStartHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillStartHandler>.Instance, players, broadcast) { Clock = () => castInstant };

        var first = Convert.FromHexString(
            "c9490600a6f72d4471ef8243506b3e42130d030101eb2fea08017cf302000000000000000000000000000000000100");
        var copy = Convert.FromHexString(
            "c9490600a6f72d4471ef8243506b3e42130d030101bab8eb08017cf302000000000000000000000000000000000100");

        await handler.HandleAsync(net.Sessions[0], first);
        var accept = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, accept.Op);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(accept.Body));
        Assert.Equal(ZoneS2COpcodes.NfyUseSkillStart, (await net.Readers[0].NextAsync()).Op);
        Assert.Equal(87, caster.Mp);   

        
        await handler.HandleAsync(net.Sessions[0], copy);
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal(87, caster.Mp);

        
        SkillCastLandings.MarkLanded(caster, 412105);
        await handler.HandleAsync(net.Sessions[0], copy);
        Assert.Equal((ZoneS2COpcodes.AnsUseSkillStart, "b84ec9490600130d030101000000000000000000000000000000000100"),
            Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal(ZoneS2COpcodes.NfySkillStartError, (await net.Readers[0].NextAsync()).Op);
    }

 /// <summary>The landing tracker: a group never accepted counts as landed, an accepted 1508 puts it in flight, and the
 /// 1509 of any skill of the group lands it.</summary>
    [Fact]
    public void SkillCastLandings_TrackTheGroupInFlight()
    {
        var player = new PlayerState();
        Assert.True(SkillCastLandings.HasLanded(player, 4121));
        SkillCastLandings.BeginCast(player, 4121);
        Assert.False(SkillCastLandings.HasLanded(player, 4121));
        Assert.True(SkillCastLandings.HasLanded(player, 4122));
        SkillCastLandings.MarkLanded(player, 412105);
        Assert.True(SkillCastLandings.HasLanded(player, 4121));
    }
}
