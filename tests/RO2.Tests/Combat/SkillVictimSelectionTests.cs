using System.Collections.Generic;
using System.Linq;
using RO2.Combats.Application;
using Xunit;
using Rules = RO2.Combats.Application.SkillVictimSelection.VictimSelectionRules;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="SkillVictimSelection"/> (the 1509 NPC victim rule moved into Combat.Application in B4 S18).</summary>
public class SkillVictimSelectionTests
{
    // Host conventions: wire cap 20, PC handle tag 0x11 (mask 0xFF), friendly camp <= 1.
    private static readonly Rules Std = new(MaxHits: 20, PcActorTag: 0x11, ActorTagMask: 0xFF, FriendlyCampMax: 1);

    /// <summary>A fake view: monsters keyed by handle, each with (EntityOid, IsDead, Camp). Unlisted handles are not live NPCs.</summary>
    private sealed class FakeView : ICombatantView
    {
        public readonly Dictionary<uint, Combatant> Npcs = new();
        public Combatant? Find(uint handle) => Npcs.TryGetValue(handle, out var c) ? c : null;
    }

    private static FakeView WithMonsters(params uint[] handles)
    {
        var v = new FakeView();
        foreach (var h in handles) v.Npcs[h] = new Combatant(EntityOid: h, IsDead: false, Camp: 2);
        return v;
    }

    private static List<uint> Select(byte hasTarget, uint primary, uint[] targets, uint caster, ICombatantView view, int? qty)
        => SkillVictimSelection.SelectNpcVictims(hasTarget, primary, targets, caster, view, qty, Std);

    [Fact]
    public void NpcPrimary_SelectedAlone()
    {
        var view = WithMonsters(0x00112E13);
        Assert.Equal(new uint[] { 0x00112E13 },
            Select(1, 0x00112E13, new uint[] { 0x00112E13, 0x00112F13 }, 0x0789A111, view, 5).ToArray());
    }

    [Fact]
    public void PcPrimaryOtherThanCaster_SelectsNothing()
    {
        var view = WithMonsters(0x00112E13, 0x00112F13);
        // primary 0x...11 is a PC handle (tag 0x11) and not the caster -> empty, even with monsters in the list.
        Assert.Empty(Select(1, 0x06FC0E11, new uint[] { 0x00112E13, 0x00112F13 }, 0x0789A111, view, 10));
    }

    [Fact]
    public void PcPrimaryIsCaster_FallsThroughToTheList()
    {
        var caster = 0x0789A111u;
        var view = WithMonsters(0x00112E13, 0x00112F13);
        // primary == caster: not short-circuited; the hit-list monsters are selected.
        Assert.Equal(new uint[] { 0x00112E13, 0x00112F13 },
            Select(1, caster, new uint[] { 0x00112E13, 0x00112F13 }, caster, view, 10).ToArray());
    }

    [Fact]
    public void GroundCast_SelectsLiveListMonsters_CappedByDamageQuantity()
    {
        var view = WithMonsters(0x00000313, 0x00000413, 0x00000513);
        // no primary (hasTarget 0): the list, capped at damageQuantity 2.
        Assert.Equal(new uint[] { 0x00000313, 0x00000413 },
            Select(0, 0, new uint[] { 0x00000313, 0x00000413, 0x00000513 }, 0x06FC0E11, view, 2).ToArray());
    }

    [Fact]
    public void SkipsDeadWrongOidFriendlyAndDuplicates()
    {
        var view = new FakeView();
        view.Npcs[0x00000313] = new Combatant(0x00000313, IsDead: false, Camp: 2); // ok
        view.Npcs[0x00000413] = new Combatant(0x00000413, IsDead: true, Camp: 2);  // dead -> skip
        view.Npcs[0x00000513] = new Combatant(0x00000513, IsDead: false, Camp: 1); // friendly -> skip
        view.Npcs[0x00000613] = new Combatant(EntityOid: 0x00009999, IsDead: false, Camp: 2); // stale oid -> skip
        var targets = new uint[] { 0x00000313, 0x00000413, 0x00000513, 0x00000613, 0x00000313 };
        Assert.Equal(new uint[] { 0x00000313 }, Select(0, 0, targets, 0x06FC0E11, view, null).ToArray());
    }

    [Fact]
    public void NoDamageQuantity_CappedAtWireMax20()
    {
        var view = new FakeView();
        var targets = Enumerable.Range(0, 30).Select(i => 0x00001000u + (uint)i).ToArray();
        foreach (var h in targets) view.Npcs[h] = new Combatant(h, false, 2);
        Assert.Equal(20, Select(0, 0, targets, 0x06FC0E11, view, null).Count);
    }
}
