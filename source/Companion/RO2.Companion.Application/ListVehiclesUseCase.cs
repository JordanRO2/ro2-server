using RO2.Companions.Domain;

namespace RO2.Companions.Application;

/// <summary>
/// The store side of a character-load 2522: the character's vehicles with every expiry cut to the whole second
/// (<see cref="IVehicleRepository.LoadAndCutToWholeSecondsAsync"/> also stores the cut list, best-effort). A read failure
/// throws; the host logs it and sends the empty list. The 2522 / 2523 wire stays in the Zone host.
/// </summary>
public sealed class ListVehiclesUseCase(IVehicleRepository vehicles)
{
    /// <summary>The character's vehicles, expiries cut to whole seconds.</summary>
    public Task<IReadOnlyList<OwnedVehicle>> LoadAndCutAsync(uint characterId, CancellationToken ct = default)
        => vehicles.LoadAndCutToWholeSecondsAsync(characterId, ct);
}
