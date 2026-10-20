using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Document persistence for the per-account play-time reward chain. The timer's cache, clock and chain rules
/// live in the Zone service; this interface is only the Mongo document read and the single/bulk upserts.
/// </summary>
public interface IPlayTimeRewardRepository
{
 /// <summary>The stored document for <paramref name="accountId"/>, or null when none exists.</summary>
    Task<PlayTimeRewardDocument?> GetDocumentAsync(uint accountId, CancellationToken ct = default);

 /// <summary>Upserts one account's document.</summary>
    Task SaveDocumentAsync(PlayTimeRewardDocument doc, CancellationToken ct = default);

 /// <summary>Upserts several accounts' documents in one unordered bulk write.</summary>
    Task SaveManyDocumentsAsync(IReadOnlyCollection<PlayTimeRewardDocument> docs, CancellationToken ct = default);
}
