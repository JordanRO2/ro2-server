using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for a character's saved key bindings (the 1423 ReqUpdateKeyConfig state the server returns
/// in 2558 at world entry). One record per character.
/// </summary>
public interface IKeyConfigRepository
{
 /// <summary>The character's saved bindings, or null when it never saved any.</summary>
    Task<KeyConfigDocument?> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Replaces the character's bindings (created on first save).</summary>
    Task SaveAsync(KeyConfigDocument doc, CancellationToken ct = default);

 /// <summary>
 /// Stores one parsed 1423 for a character. A flag-only request (mode 0 with no records) sets Mode and
 /// UpdatedUtc and keeps the stored Entries, writing an empty list only when the character has no record
 /// yet; every other request replaces the record. The update is atomic.
 /// </summary>
    Task ApplyUpdateAsync(uint characterId, ushort mode, byte[] entries, DateTime updatedUtc, CancellationToken ct = default);
}
