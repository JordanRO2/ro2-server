namespace RO2.Skills.Application;

/// <summary>
/// A port onto one player's live learned-skill tree, built per request by the host around its runtime player
/// object. The tree itself is runtime state the host owns; flows read a snapshot, overlay a change, and run the
/// decide-persist-mirror sequence inside the per-player write gate so two requests cannot race the same tree.
/// </summary>
public interface ILearnedSkillsSession
{
    /// <summary>A private copy of the learned tree (group id → level), safe to enumerate and validate against.</summary>
    IReadOnlyDictionary<uint, byte> Snapshot();

    /// <summary>Sets each change's group to its level in the tree (copy-on-write); groups not listed are untouched.</summary>
    void Overlay(IEnumerable<(uint Group, byte Level)> changes);

    /// <summary>Removes each <paramref name="dropped"/> group and sets each <paramref name="starters"/> group to level 1 (one copy-on-write swap).</summary>
    void ResetLearnedSkills(IEnumerable<uint> dropped, IEnumerable<uint> starters);

    /// <summary>
    /// Runs <paramref name="body"/> holding the per-player write gate, releasing it on every path, so a validate →
    /// persist → mirror sequence sees a tree no concurrent request is changing under it.
    /// </summary>
    Task<T> WithWriteGateAsync<T>(Func<Task<T>> body, CancellationToken ct = default);
}
