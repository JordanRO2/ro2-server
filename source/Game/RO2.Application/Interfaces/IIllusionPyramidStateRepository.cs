using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Document persistence for a character's Illusion Pyramid state (one <see cref="IllusionPyramidStateDocument"/> per
/// character: Mystical and Bright balances and the top record). The entry-cost, mileage and top-record rules live in
/// the Zone IllusionPyramidService, which changes a character's state only under its run lock; this interface is only
/// the whole-document read and upsert.
/// </summary>
public interface IIllusionPyramidStateRepository
{
 /// <summary>The character's state, or a zero one (never explored) when it has none.</summary>
    Task<IllusionPyramidStateDocument> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Replaces the character's state (create on first save) and stamps <c>UpdatedUtc</c>. Whole-document
 /// writes cannot interleave because the Zone service changes a character's state only under its run lock.</summary>
    Task SaveAsync(IllusionPyramidStateDocument doc, CancellationToken ct = default);
}
