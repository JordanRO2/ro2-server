using RO2.Monster.Application;
using Xunit;

namespace RO2.Tests.Monster;

/// <summary>The victim-left-the-fight decision: which monsters drop the victim, who is released, and the order of the effects.</summary>
public class VictimDropPassTests
{
    private const uint Victim = 0x111;

    private sealed class Fake : IVictimThreatPorts
    {
        public readonly List<string> Log = [];
        public readonly Dictionary<uint, VictimRemoval> Removals = new();

        public VictimRemoval RemoveVictim(uint oid, uint victim)
        {
            Log.Add($"remove {oid}");
            return Removals.GetValueOrDefault(oid);
        }

        public Task NotifyVictimRemovedAsync(uint oid, VictimRemoval r) { Log.Add($"notify {oid}->{r.Target:X}"); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    public void IsReleased_HeldOrKiller_AndNobodyEngaged(bool held, bool killer, bool engaged, bool expected)
        => Assert.Equal(expected, VictimDropPass.IsReleased(held, killer, engaged));

    [Fact]
    public async Task EachMonsterIsRemovedInOrder_NotifiedOnlyWhenHeld_AndReleasedSetKeepsOrder()
    {
        var f = new Fake();
        f.Removals[1] = new VictimRemoval(true, 0x222);
        f.Removals[2] = new VictimRemoval(false, 0x333);
        f.Removals[3] = new VictimRemoval(true, 0);
        f.Removals[4] = new VictimRemoval(true, 0x444);

        // 2 is the killer (not held, still released); 4 still has an engaged player (held, not released).
        var released = await VictimDropPass.RunAsync(f, Victim, killerHandle: 2, [1u, 2u, 3u, 4u], new HashSet<uint> { 4 });

        Assert.Equal(["remove 1", "notify 1->222", "remove 2", "remove 3", "notify 3->0", "remove 4", "notify 4->444"], f.Log);
        Assert.Equal([1u, 2u, 3u], released);
    }

    [Fact]
    public async Task NoMonsters_ReleasesNothing()
    {
        var f = new Fake();
        Assert.Empty(await VictimDropPass.RunAsync(f, Victim, 0, [], new HashSet<uint>()));
        Assert.Empty(f.Log);
    }
}
