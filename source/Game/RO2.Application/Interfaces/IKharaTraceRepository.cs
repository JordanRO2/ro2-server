namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for a character's Khara tracker pins — the list the client last saved with RMI 1434
/// ReqSaveKharaTraceList and reads back with 1435. One document per character; the list is stored exactly
/// as the client sent it (order and duplicates preserved), because retail echoes it back verbatim.
/// </summary>
public interface IKharaTraceRepository
{
 /// <summary>The character's saved trace list, or an empty list if it never saved one.</summary>
    Task<IReadOnlyList<uint>> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Replaces the character's trace list (1434 always carries the full set, never a delta).</summary>
    Task SaveAsync(uint characterId, IReadOnlyList<uint> kharaIds, CancellationToken ct = default);
}
