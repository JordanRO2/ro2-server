using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Document persistence for a character's Khara state (one <see cref="KharaStateDocument"/> per character:
/// point balance, accepted Kharas with their per-row progress, completed ids). The progress and completion
/// rules, the published-state cache and the per-character gate live in the Zone service; this interface is
/// only the Mongo read and the whole-document upsert.
/// </summary>
public interface IKharaStateRepository
{
 /// <summary>The character's Khara state, or null when it never accepted a Khara on this server.</summary>
    Task<KharaStateDocument?> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Replaces the character's state (create on first save). Every change rewrites the whole
 /// document; the Zone service serializes the changes of one character.</summary>
    Task SaveAsync(KharaStateDocument doc, CancellationToken ct = default);
}
