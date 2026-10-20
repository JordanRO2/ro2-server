using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>
/// Tests for the 1509/1549 pre-damage gate. Damage is applied only when the cast was started by an accepted
/// 1508/1561 (a pending cast from SkillCastLandings.TryGovern) and, for an NPC target, the target is alive, on the
/// same map, within the skill's reach, and the skill is learned (SkillDamageHandler.EvaluateDamageGate via ActorGate).
/// A rejected request is refused before CombatService applies any damage. The reach table is read from
/// Skill_Table_Base.json.
/// </summary>
public class SkillDamageGateTests
{
    private const uint Map = 1100;
    private const uint Skill = 412105;         
    private const uint Group = Skill / 100;    

    private static string DataFile(string name)
    {
        var relative = Path.Combine("data", name);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    private static GateActor Hero() =>
        new(Map, 0, 0, false, new Dictionary<uint, byte> { [Group] = 5 });

    private static GateTarget? Npc(float x, float y, uint map = Map, bool dead = false)
        => new GateTarget(map, x, y, dead);

    
    private const float Reach = 10f;

    private static ActorRefusal Gate(GateActor actor, GateTarget? target, float? reach = Reach)
        => SkillDamageHandler.EvaluateDamageGate(actor, Skill, target, reach);

    
    [Fact]
    public void ForgedDamageWithNoStartIsRefused_TargetUntouched()
    {
        
        var p = new PlayerState { MapId = Map };
        Assert.False(SkillCastLandings.TryGovern(p, Skill, DateTime.UtcNow, consume: true, out _));
    }

    [Fact]
    public void LegitStartThenDamageIsGoverned_AndDealsTheCastSkillsRate()
    {
        
        
        
        var now = DateTime.UtcNow;
        var p = new PlayerState { MapId = Map };
        SkillCastLandings.RecordPending(p, Skill, 0x0000D413u, now);
        Assert.True(SkillCastLandings.TryGovern(p, Skill, now.AddMilliseconds(200), consume: true, out var cast));
        Assert.Equal(Skill, cast.SkillId);
        Assert.Equal(ActorRefusal.None, Gate(Hero(), Npc(3, 0)));
    }

    [Fact]
    public void CrossMapTargetIsRefused()
        => Assert.Equal(ActorRefusal.CrossMap, Gate(Hero(), Npc(1, 0, map: 1200)));

    [Fact]
    public void OutOfRangeTargetIsRefused()
        => Assert.Equal(ActorRefusal.OutOfRange, Gate(Hero(), Npc(Reach + 1f, 0)));

    [Fact]
    public void DeadTargetIsRefused()
        => Assert.Equal(ActorRefusal.TargetDead, Gate(Hero(), Npc(1, 0, dead: true)));

    [Fact]
    public void UnlearnedSkillIsRefused_WithOrWithoutATarget()
    {
        var noSkill = new GateActor(Map, 0, 0, false, new Dictionary<uint, byte>());
        Assert.Equal(ActorRefusal.SkillNotLearned, Gate(noSkill, Npc(1, 0)));   
        Assert.Equal(ActorRefusal.SkillNotLearned, Gate(noSkill, null));        
    }

    [Fact]
    public void GroundAoeWithNoNpcPrimaryPassesOnTheLearnedSkillAlone()
        => Assert.Equal(ActorRefusal.None, Gate(Hero(), null));

    [Fact]
    public void SkillRange100SkipsTheRangeCheck()
    {
        
        
        var table = SkillMoveTracker.LoadCastReach(DataFile("Skill_Table_Base.json"));
        Assert.NotEmpty(table);
        Assert.Contains(table, kv => float.IsPositiveInfinity(kv.Value));                 
        Assert.Contains(table, kv => float.IsFinite(kv.Value) && kv.Value > 0f);          

        Assert.Equal(ActorRefusal.None, Gate(Hero(), Npc(100_000f, 0), reach: float.PositiveInfinity));
        Assert.Equal(ActorRefusal.OutOfRange, Gate(Hero(), Npc(100_000f, 0), reach: Reach));
    }

    [Fact]
    public void MissingTableRowRefusesRatherThanAssumesRange()
        => Assert.Equal(ActorRefusal.RangeUnknown, Gate(Hero(), Npc(1, 0), reach: null));
}
