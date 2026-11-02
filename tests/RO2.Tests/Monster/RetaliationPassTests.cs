using RO2.Monster.Application;
using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Monster;

/// <summary>The pre-swing decision of a monster (acquire, reselect, reset) and the order of its effects, over fake ports.</summary>
public class RetaliationPassTests
{
    private const uint Mon = 100, Map = 1100, A = 0x111, B = 0x211;

    private sealed class Fake : IMonsterReadView, IMonsterCommands, IRetaliationNotifier
    {
        public readonly List<string> Log = [];
        public uint Aggro;
        public Dictionary<uint, PlayerFacts> Players = new();
        public uint Reselected;
        public bool ResetChanges;

        public PlayerFacts? FindPlayer(uint h) => Players.GetValueOrDefault(h);
        public uint AggroTargetOf(uint oid) => Aggro;
        public bool AddThreat(uint oid, uint h, string? n, long amt) { Log.Add($"threat {h:X} {amt}"); Aggro = h; return true; }
        public bool LeashReset(uint oid) { Log.Add("reset"); Aggro = 0; return ResetChanges; }
        public uint ReselectLiveTarget(uint oid) { Log.Add("reselect"); Aggro = Reselected; return Reselected; }
        public Task EnterCombatAsync(uint oid, uint h) { Log.Add($"enter {h:X}"); return Task.CompletedTask; }
        public Task NotifyLeashHealedAsync(uint oid) { Log.Add("healed"); return Task.CompletedTask; }
    }

    private static PlayerFacts P(uint h, bool dead = false, uint map = Map) => new(h, "p", dead, map);

    private static Task<PreSwingVerdict> Run(Fake f, NpcAggressionMode mode, bool iterating, PlayerFacts? scan = null)
        => new RetaliationPass(f, f, f).RunAsync(new MonsterFacts(Mon, Map, mode), iterating, scan, s => s);

    [Fact]
    public async Task PassiveIdleMonster_Skips_WithoutScanning()
    {
        var f = new Fake();
        bool scanned = false;
        var v = await new RetaliationPass(f, f, f).RunAsync(new MonsterFacts(Mon, Map, NpcAggressionMode.None), false, 0,
            _ => { scanned = true; return null; });
        Assert.Equal(new PreSwingVerdict(PreSwingAction.Skip, 0, false), v);
        Assert.False(scanned);
        Assert.Empty(f.Log);
    }

    [Fact]
    public async Task AggressiveIdleMonster_NothingInSight_Skips()
    {
        var f = new Fake();
        var v = await Run(f, NpcAggressionMode.Always, false, null);
        Assert.Equal(PreSwingAction.Skip, v.Action);
        Assert.Empty(f.Log);
    }

    [Fact]
    public async Task Acquire_SeatsOnePointThreat_ThenEntersCombat_ThenActsOnTheAcquired()
    {
        var f = new Fake();
        f.Players[A] = P(A);
        var v = await Run(f, NpcAggressionMode.Always, false, P(A));
        Assert.Equal(["threat 111 1", "enter 111"], f.Log);
        Assert.Equal(new PreSwingVerdict(PreSwingAction.Act, A, true), v);
    }

    [Theory]
    [InlineData(true, Map)]   // dead
    [InlineData(false, 999u)] // other map
    public async Task TargetGone_ReselectsTheNextLiveAttacker(bool dead, uint map)
    {
        var f = new Fake { Aggro = A, Reselected = B };
        f.Players[A] = P(A, dead, map);
        f.Players[B] = P(B);
        var v = await Run(f, NpcAggressionMode.None, false);
        Assert.Equal(["reselect"], f.Log);
        Assert.Equal(new PreSwingVerdict(PreSwingAction.Act, B, true), v);
    }

    [Fact]
    public async Task TargetStaleHandle_NoOneLeft_Resets_AndHealsOnlyWhenSomethingChanged()
    {
        var f = new Fake { Aggro = A, Reselected = 0, ResetChanges = true };
        var v = await Run(f, NpcAggressionMode.None, false);
        Assert.Equal(["reselect", "reset", "healed"], f.Log);
        Assert.Equal(new PreSwingVerdict(PreSwingAction.Reset, 0, true), v);

        var g = new Fake { Aggro = A, Reselected = 0, ResetChanges = false };
        v = await Run(g, NpcAggressionMode.None, false);
        Assert.Equal(["reselect", "reset"], g.Log);
        Assert.Equal(new PreSwingVerdict(PreSwingAction.Reset, 0, true), v);   // the reselect itself retargeted
    }

    [Fact]
    public async Task ValidTarget_Acts_NoEffects()
    {
        var f = new Fake { Aggro = A };
        f.Players[A] = P(A);
        var v = await Run(f, NpcAggressionMode.Always, false);
        Assert.Empty(f.Log);
        Assert.Equal(new PreSwingVerdict(PreSwingAction.Act, A, false), v);
    }

    [Fact]
    public async Task EngagedPlayerWithoutThreatTarget_Acts_WithNoTarget()
    {
        var f = new Fake();
        var v = await Run(f, NpcAggressionMode.Always, true);
        Assert.Empty(f.Log);
        Assert.Equal(new PreSwingVerdict(PreSwingAction.Act, 0, false), v);
    }
}
