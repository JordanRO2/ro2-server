using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for the world events' server-wide live state (one <see cref="WorldEventStateDocument"/> per
/// event). The state is stored as each change happens so a restart resumes the current stage and offering
/// counts instead of resetting the event.
/// </summary>
public interface IWorldEventStateRepository
{
 /// <summary>Every stored event state.</summary>
    Task<IReadOnlyList<WorldEventStateDocument>> GetAllAsync(CancellationToken ct = default);

 /// <summary>Replaces one event's state (created on first save).</summary>
    Task SaveAsync(WorldEventStateDocument doc, CancellationToken ct = default);
}
