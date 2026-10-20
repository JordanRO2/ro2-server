using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The hit set of a 1549 tick: SkillTickTargets keeps the actors the skill's last 2718 carried (recorded by
/// SkillDamageHandler), and SkillDamageTickHandler ticks the ones still alive, in that order.
/// </summary>
/// <remarks>
/// The reference payloads use the enc-driven framing: UNENC uses a 3-byte header, RC4 and AES a 5-byte header, and the
/// opcode is read from the bytes.
/// </remarks>
public class SkillTickTargetsTests
{
    private const uint Map = 3101;

 /// <summary>
 /// A PC casts skill 210706 on five NPC targets. Its 2718 lists the targets, its 1549 asks for tick 1, and the 2797 hits
 /// the same five in the 2718's order. The tick's damage matches the reference body byte for byte.
 /// </summary>
    [Fact]
    public void TickHitsTheRecordedSetInTheOrderOfTheCastsNotify()
    {
        var caster = new PlayerState { EntityId = 0x072BAD11, MapId = Map };
        uint[] cast = { 0x000F5413, 0x000F5213, 0x000F5313, 0x000F5713, 0x000F5813 };
        SkillTickTargets.Record(caster, Map, 210706, cast);

        var npcs = cast.ToDictionary(h => h, h => Npc(h));
        var live = SkillTickTargets.LiveTargets(SkillTickTargets.Recorded(caster, Map, 210706), Map, h => npcs.GetValueOrDefault(h));
        Assert.Equal(cast, live.Select(t => t.Handle).ToArray());

        
        (uint Hp, uint Sp, int Damage)[] after =
        {
            (113597, 13251, 5691), (112449, 13251, 6835), (112544, 13251, 6740), (113201, 13314, 6758), (113133, 13314, 6826),
        };
        var hits = live.Select((t, i) => new SkillTickHit(t.Handle, after[i].Hp, after[i].Sp, new SkillHitEffect(1, after[i].Damage))).ToArray();
        var body = SkillDamagePackets.BuildSkillDamageTick2797(caster.EntityId, 210706, 1, hits);

        Assert.Equal(Convert.FromHexString(
            "02900090006fe400000000123703000111ad2b07050013540f00bdbb0100c333000000000000013b160000000000" +
            "0013520f0041b70100c33300000000000001b31a00000000000013530f00a0b70100c3330000000000000154" +
            "1a00000000000013570f0031ba0100023400000000000001661a00000000000013580f00edb90100023400000000" +
            "000001aa1a000000000000"), body);
    }

 /// <summary>
 /// Actors that die drop out and the rest keep their place. A four-target cast ticks all four on tick 1, then each target
 /// is dropped as it dies while the remaining targets keep their order.
 /// </summary>
    [Fact]
    public void DeadActorsDropOutOfLaterTicks()
    {
        var caster = new PlayerState { EntityId = 0x072BE011, MapId = Map };
        uint[] cast = { 0x000F4713, 0x000F4913, 0x000F4C13, 0x000F4F13 };
        SkillTickTargets.Record(caster, Map, 121101, cast);
        var npcs = cast.ToDictionary(h => h, h => Npc(h));

        uint[] Tick() => SkillTickTargets.LiveTargets(SkillTickTargets.Recorded(caster, Map, 121101), Map,
            h => npcs.GetValueOrDefault(h)).Select(t => t.Handle).ToArray();

        Assert.Equal(cast, Tick());
        npcs[0x000F4C13].IsDead = true;
        Assert.Equal(new uint[] { 0x000F4713, 0x000F4913, 0x000F4F13 }, Tick());
        npcs[0x000F4913].IsDead = true;
        Assert.Equal(new uint[] { 0x000F4713, 0x000F4F13 }, Tick());
        npcs[0x000F4F13].IsDead = true;
        Assert.Equal(new uint[] { 0x000F4713 }, Tick());
        npcs[0x000F4713].IsDead = true;
        Assert.Empty(Tick()); 
    }

 /// <summary>
 /// A new cast of the skill replaces the record; another skill id (a level is its own id) has none; a record
 /// made on another map is not used; zero handles and repeats are left out.
 /// </summary>
    [Fact]
    public void RecordIsPerExactSkillIdAndMap()
    {
        var caster = new PlayerState { EntityId = 0x072BAD11, MapId = Map };
        SkillTickTargets.Record(caster, Map, 210706, new uint[] { 0x000F5413, 0, 0x000F5213, 0x000F5413 });
        Assert.Equal(new uint[] { 0x000F5413, 0x000F5213 }, SkillTickTargets.Recorded(caster, Map, 210706));

        SkillTickTargets.Record(caster, Map, 210706, new uint[] { 0x000F5813 });
        Assert.Equal(new uint[] { 0x000F5813 }, SkillTickTargets.Recorded(caster, Map, 210706));

        Assert.Empty(SkillTickTargets.Recorded(caster, Map, 210705));
        Assert.Empty(SkillTickTargets.Recorded(caster, Map + 1, 210706));
        Assert.Empty(SkillTickTargets.Recorded(new PlayerState { EntityId = 0x072BE011 }, Map, 210706));

        SkillTickTargets.Record(caster, Map, 210706, Array.Empty<uint>());
        Assert.Empty(SkillTickTargets.Recorded(caster, Map, 210706));
    }

 /// <summary>
 /// Only monsters on the caster's map that the NPC manager still knows are ticked, and never more than 20 (the observed
 /// maximum count).
 /// </summary>
    [Fact]
    public void LiveTargetsKeepsMonstersOnTheMapAndCapsAtTwenty()
    {
        var npcs = new Dictionary<uint, NpcRuntimeState>
        {
            [0x00000113] = Npc(0x00000113),
            [0x00000213] = Npc(0x00000213, camp: 1),       
            [0x00000313] = Npc(0x00000313, map: Map + 1),  
        };
        var live = SkillTickTargets.LiveTargets(new uint[] { 0x00000113, 0x00000213, 0x00000313, 0x00000413 }, Map,
            h => npcs.GetValueOrDefault(h));
        Assert.Equal(new uint[] { 0x00000113 }, live.Select(t => t.Handle).ToArray());

        var many = Enumerable.Range(1, 25).Select(i => (uint)(i << 8) | 0x13).ToArray();
        var all = many.ToDictionary(h => h, h => Npc(h));
        var capped = SkillTickTargets.LiveTargets(many, Map, h => all.GetValueOrDefault(h));
        Assert.Equal(SkillDamagePackets.MaxSkillDamageTickTargets, capped.Count);
        Assert.Equal(many.Take(20), capped.Select(t => t.Handle));
    }

    private static NpcRuntimeState Npc(uint handle, int camp = 2, uint map = Map) =>
        new() { EntityOid = handle, MapId = map, Camp = camp, Hp = 100000, MaxHp = 120000 };
}
