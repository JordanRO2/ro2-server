using RO2.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

public class ActorGateTests
{
    private const uint Map = 1100;

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

    private static PlayerState Hero(int weaponRange = 0) => new()
    {
        MapId = Map, X = 0, Y = 0, AutoAttackRange = weaponRange,
        LearnedSkills = new Dictionary<uint, byte> { [4121] = 5, [4122] = 0 },
    };

    private static NpcRuntimeState Mob(float x, float y, uint map = Map, bool dead = false)
        => new() { MapId = map, PosX = x, PosY = y, IsDead = dead };

    private static ActorRefusal Melee(PlayerState p, NpcRuntimeState n, bool alive = true)
        => ActorGate.CanAct(ActorGate.ActorOf(p), ActorGate.TargetOf(n),
            new GateContext(ActorGate.MeleeReach(p), TargetMustBeAlive: alive));

    [Fact]
    public void AllOkPasses()
        => Assert.Equal(ActorRefusal.None, Melee(Hero(), Mob(3, 0)));

    [Fact]
    public void UnresolvedTargetIsRefused()
        => Assert.Equal(ActorRefusal.TargetNotFound,
            ActorGate.CanAct(ActorGate.ActorOf(Hero()), null, new GateContext(10f)));

    [Fact]
    public void DeadTargetIsRefusedUnlessTheActionWantsOne()
    {
        var hero = Hero();
        var corpse = Mob(1, 0, dead: true);
        Assert.Equal(ActorRefusal.TargetDead, Melee(hero, corpse));
        Assert.Equal(ActorRefusal.None, Melee(hero, corpse, alive: false));    // e.g. a revive
    }

    [Fact]
    public void DeadActorIsRefused()
    {
        var hero = Hero();
        hero.IsDead = true;
        Assert.Equal(ActorRefusal.ActorDead, Melee(hero, Mob(1, 0)));
    }

    [Fact]
    public void CrossMapIsRefusedEvenWhenCoordinatesAreClose()
        => Assert.Equal(ActorRefusal.CrossMap, Melee(Hero(), Mob(1, 0, map: 1200)));

    [Fact]
    public void MeleeRangeComesFromTheWeaponOrTheMeasuredFallback()
    {
        // Unarmed: MaxAttackRange + tolerance (3.890625) = 7.890625. Computed, not hard-coded, so it tracks the constant.
        var meleeReach = AutoAttackState.MaxAttackRange + AutoAttackState.AttackRangeTolerance;
        Assert.Equal(ActorRefusal.None, Melee(Hero(), Mob(meleeReach - 0.01f, 0)));
        Assert.Equal(ActorRefusal.OutOfRange, Melee(Hero(), Mob(meleeReach + 0.01f, 0)));
        // Bow: Auto_Attack_Range 20 + tolerance = 23.890625.
        var bowReach = 20f + AutoAttackState.AttackRangeTolerance;
        Assert.Equal(ActorRefusal.None, Melee(Hero(20), Mob(bowReach - 0.01f, 0)));
        Assert.Equal(ActorRefusal.OutOfRange, Melee(Hero(20), Mob(bowReach + 0.01f, 0)));
        // Same answer as the existing attack gate.
        Assert.Equal(AutoAttackState.InRange(Hero(20), bowReach - 0.01f, 0), Melee(Hero(20), Mob(bowReach - 0.01f, 0)) == ActorRefusal.None);
        Assert.Equal(AutoAttackState.InRange(Hero(20), bowReach + 0.01f, 0), Melee(Hero(20), Mob(bowReach + 0.01f, 0)) == ActorRefusal.None);
    }

    [Fact]
    public void NonFinitePositionsAndReachAreOutOfRange()
    {
        Assert.Equal(ActorRefusal.OutOfRange, Melee(Hero(), Mob(float.NaN, 0)));
        Assert.Equal(ActorRefusal.OutOfRange, Melee(Hero(), Mob(float.PositiveInfinity, 0)));
        Assert.Equal(ActorRefusal.OutOfRange,
            ActorGate.CanAct(ActorGate.ActorOf(Hero()), ActorGate.TargetOf(Mob(1, 0)), new GateContext(float.NaN)));
    }

    [Fact]
    public void MissingReachIsRefusedNotAssumed()
        => Assert.Equal(ActorRefusal.RangeUnknown,
            ActorGate.CanAct(ActorGate.ActorOf(Hero()), ActorGate.TargetOf(Mob(1, 0)), new GateContext(null)));

    [Fact]
    public void NpcAttackerReachComesFromNpcInfo()
    {
        var info = System.Text.Json.JsonSerializer.Deserialize<List<NpcData>>(File.ReadAllBytes(DataFile("NPCInfo.json")))!
            .First(n => n.Attack_Range >= 1f && n.Attack_Range < 30f);
        var reach = ActorGate.NpcReach(info);
        Assert.Equal(info.Attack_Range + AutoAttackState.AttackRangeTolerance, reach);

        var mob = new GateActor(Map, 0, 0, false, null);
        var victim = ActorGate.TargetOf(new PlayerState { MapId = Map, X = reach - 0.01f, Y = 0 });
        var far = ActorGate.TargetOf(new PlayerState { MapId = Map, X = reach + 1f, Y = 0 });
        Assert.Equal(ActorRefusal.None, ActorGate.CanAct(mob, victim, new GateContext(reach)));
        Assert.Equal(ActorRefusal.OutOfRange, ActorGate.CanAct(mob, far, new GateContext(reach)));
    }

    [Fact]
    public void SkillRangeComesFromSkillTableBase()
    {
        var table = SkillMoveTracker.LoadReach(DataFile("Skill_Table_Base.json"));
        Assert.NotEmpty(table);
        var (group, reach) = table.Where(kv => kv.Value is > 0f and < 100f).OrderBy(kv => kv.Key).First();
        var full = reach + AutoAttackState.AttackRangeTolerance;

        // The gate's helper reads data/ relative to the working directory; when the host runs elsewhere it is null,
        // which the gate refuses rather than guesses. Feed the table value the way a wired caller would.
        var helper = ActorGate.SkillReach(group);
        if (helper is not null) Assert.Equal(full, helper.Value);

        var near = ActorGate.TargetOf(Mob(full - 0.01f, 0));
        var far = ActorGate.TargetOf(Mob(full + 1f, 0));
        var ctx = new GateContext(full, SkillGroup: group);
        var learned = new GateActor(Map, 0, 0, false, new Dictionary<uint, byte> { [group] = 1 });
        Assert.Equal(ActorRefusal.None, ActorGate.CanAct(learned, near, ctx));
        Assert.Equal(ActorRefusal.OutOfRange, ActorGate.CanAct(learned, far, ctx));
    }

    [Fact]
    public void UnknownSkillGroupHasNoReach()
        => Assert.Null(ActorGate.SkillReach(uint.MaxValue));

    [Fact]
    public void UnlearnedSkillIsRefused()
    {
        var hero = Hero();
        var near = ActorGate.TargetOf(Mob(1, 0));
        Assert.Equal(ActorRefusal.None, ActorGate.CanAct(ActorGate.ActorOf(hero), near, new GateContext(10f, SkillGroup: 4121)));
        Assert.Equal(ActorRefusal.SkillNotLearned, ActorGate.CanAct(ActorGate.ActorOf(hero), near, new GateContext(10f, SkillGroup: 9999)));
        Assert.Equal(ActorRefusal.SkillNotLearned, ActorGate.CanAct(ActorGate.ActorOf(hero), near, new GateContext(10f, SkillGroup: 4122))); // level 0
        Assert.Equal(ActorRefusal.SkillNotLearned,
            ActorGate.CanAct(new GateActor(Map, 0, 0, false, null), near, new GateContext(10f, SkillGroup: 4121)));
    }

    [Fact]
    public void UnownedItemIsRefused()
    {
        var a = ActorGate.ActorOf(Hero());
        var near = ActorGate.TargetOf(Mob(1, 0));
        Assert.Equal(ActorRefusal.ItemNotOwned, ActorGate.CanAct(a, near, new GateContext(10f, ItemOwned: false)));
        Assert.Equal(ActorRefusal.None, ActorGate.CanAct(a, near, new GateContext(10f, ItemOwned: true)));
    }

    [Fact]
    public void ChecksRunInOrderLivenessMapRangeOwnership()
    {
        var a = ActorGate.ActorOf(Hero());
        var ctx = new GateContext(1f, SkillGroup: 9999, ItemOwned: false);
        Assert.Equal(ActorRefusal.TargetDead, ActorGate.CanAct(a, new GateTarget(1200, 500, 0, true), ctx));
        Assert.Equal(ActorRefusal.CrossMap, ActorGate.CanAct(a, new GateTarget(1200, 500, 0, false), ctx));
        Assert.Equal(ActorRefusal.OutOfRange, ActorGate.CanAct(a, new GateTarget(Map, 500, 0, false), ctx));
        Assert.Equal(ActorRefusal.SkillNotLearned, ActorGate.CanAct(a, new GateTarget(Map, 0.5f, 0, false), ctx));
    }
}
