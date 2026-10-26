namespace RO2.Combats.Domain;

/// <summary>
/// One caster's record of the actors each skill last hit, as its 2718 NfySkillDamage carried them: written by
/// the 1509 ReqSkillDamage answer and read by the 1549 ReqSkillDamageTick answer, whose 2797 NfySkillDamageTick
/// hits that same set. One cast per caster per skill id (a level is its own id); the recorded order is the
/// damage order. The host holds one instance per player (lifetime = the player; lost on reload) and filters the
/// recorded handles against the live world itself.
/// </summary>
public sealed class SkillTickTargetLedger
{
    /// <summary>The actors one cast's 2718 carried, and the map it was cast on.</summary>
    public readonly record struct CastTargets(uint MapId, uint[] Handles);

    private readonly object _gate = new();
    private readonly Dictionary<uint, CastTargets> _bySkill = new();

    /// <summary>
    /// Records the actors a 2718 for <paramref name="skillId"/> carried, replacing the previous cast of that
    /// exact skill id. Zero handles and repeats are left out; the order is kept.
    /// </summary>
    public void Record(uint mapId, uint skillId, IEnumerable<uint> handles)
    {
        var list = new List<uint>();
        foreach (var h in handles)
        {
            if (h != 0 && !list.Contains(h))
                list.Add(h);
        }

        lock (_gate)
            _bySkill[skillId] = new CastTargets(mapId, list.ToArray());
    }

    /// <summary>Empty when there is no record or the cast was on another map.</summary>
    public IReadOnlyList<uint> Recorded(uint mapId, uint skillId)
    {
        lock (_gate)
        {
            return _bySkill.TryGetValue(skillId, out var cast) && cast.MapId == mapId
                ? cast.Handles
                : Array.Empty<uint>();
        }
    }
}
