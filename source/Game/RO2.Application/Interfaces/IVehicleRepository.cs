using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for the vehicles each character owns. The list is sent on every map load (2522, after 2523).
/// A reins item (ItemInfo Item_Type 11) grants a vehicle when used (request 1323). The grant, extension and
/// cut rules are <see cref="VehicleRules"/>.
/// </summary>
public interface IVehicleRepository
{
 /// <summary>The character's vehicles (empty when it owns none).</summary>
    Task<IReadOnlyList<OwnedVehicle>> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>
 /// Applies <see cref="VehicleRules.Grant"/> to the character's stored list and stores the result as one atomic
 /// read-modify-write (a concurrent grant is never lost). Returns the new list, sorted by RidingInfo.ID.
 /// Throws when it cannot store it.
 /// </summary>
    Task<IReadOnlyList<OwnedVehicle>> GrantAsync(uint characterId, uint ridingId, uint? days, double nowOle, CancellationToken ct = default);

 /// <summary>
 /// Extends <paramref name="ridingId"/> by <paramref name="days"/> with the grant rule, atomically. Null (nothing
 /// stored) when the character does not own that vehicle or it is permanent (expiry 0). Throws when it cannot store it.
 /// </summary>
    Task<IReadOnlyList<OwnedVehicle>?> TryExtendTimedAsync(uint characterId, uint ridingId, uint days, double nowOle, CancellationToken ct = default);

 /// <summary>
 /// Returns the character's vehicles with every expiry cut to whole seconds (<see cref="VehicleRules.CutToWholeSeconds"/>),
 /// as sent on character load. The cut list is stored when it differs from the stored one, on a best-effort basis:
 /// a failed or contended store is skipped and retried on the next load. Throws when the read fails.
 /// </summary>
    Task<IReadOnlyList<OwnedVehicle>> LoadAndCutToWholeSecondsAsync(uint characterId, CancellationToken ct = default);
}
