using RO2.Server.Zone.Models;
using RO2.Skills.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wraps one <see cref="PlayerState"/>'s learned-skill tree as an <see cref="ILearnedSkillsSession"/>, built per
/// request. The members forward one-to-one to the PlayerState copy-on-write helpers and its write gate, so the
/// reads, the mirror, and the gating behave exactly as they did in the handler.
/// </summary>
internal sealed class LearnedSkillsSession(PlayerState player) : ILearnedSkillsSession
{
    public IReadOnlyDictionary<uint, byte> Snapshot() => player.SnapshotLearnedSkills();

    public void Overlay(IEnumerable<(uint Group, byte Level)> changes) => player.OverlayLearnedSkills(changes);

    public async Task<T> WithWriteGateAsync<T>(Func<Task<T>> body, CancellationToken ct = default)
    {
        await player.LearnedSkillsWriteGate.WaitAsync(ct);
        try { return await body(); }
        finally { player.LearnedSkillsWriteGate.Release(); }
    }
}
