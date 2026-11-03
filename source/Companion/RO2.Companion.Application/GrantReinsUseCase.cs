using RO2.Companions.Domain;
using RO2.GameData.Tables;

namespace RO2.Companions.Application;

/// <summary>How a reins use ended: the item resolved and the grant was stored, the tables do not give the grant, or the store threw.</summary>
public enum GrantReinsStatus
{
    /// <summary>The vehicle was granted; <see cref="GrantReinsResult.Vehicles"/> is the updated list.</summary>
    Granted,

    /// <summary>No RidingDurationGroup / RidingInfo / ItemCashInfo row resolves the item; nothing was stored.</summary>
    Unresolved,

    /// <summary>The grant could not be stored (<see cref="GrantReinsResult.Error"/>); the item must not be consumed.</summary>
    StoreFailed,
}

/// <summary>
/// The outcome of a reins use. <see cref="RidingId"/> and <see cref="Days"/> (null = permanent) are set once the item resolved
/// (Granted and StoreFailed); <see cref="Vehicles"/> only on Granted; <see cref="Error"/> only on StoreFailed.
/// </summary>
public readonly record struct GrantReinsResult(GrantReinsStatus Status, uint RidingId, uint? Days, IReadOnlyList<OwnedVehicle>? Vehicles, Exception? Error);

/// <summary>
/// The store side of a reins use (Item_Type 11): <see cref="VehicleRules.TryResolveReins(IReadOnlyList{RidingDurationGroup}, IReadOnlyList{RidingInfo}, IReadOnlyList{ItemCashInfo}, uint, out uint, out uint?)"/>
/// names the vehicle and duration, then <see cref="IVehicleRepository.GrantAsync"/> stores it atomically. The three rows come in
/// as arguments so this use case never reaches for the game-data store. The item consume, the clock (<c>nowOle</c>) and the
/// 2523 -> 2522 -> 2329 wire (with <c>VehicleClientList.Record</c> ahead of the 2522) stay in the Zone host.
/// </summary>
public sealed class GrantReinsUseCase(IVehicleRepository vehicles)
{
    /// <summary>Resolves and grants the vehicle of reins item <paramref name="itemId"/>; see the type remarks. A cancelled call still throws.</summary>
    public async Task<GrantReinsResult> GrantAsync(uint characterId, uint itemId, IReadOnlyList<RidingDurationGroup> groups,
        IReadOnlyList<RidingInfo> ridings, IReadOnlyList<ItemCashInfo> cashInfo, double nowOle, CancellationToken ct = default)
    {
        if (!VehicleRules.TryResolveReins(groups, ridings, cashInfo, itemId, out var ridingId, out var days))
            return new GrantReinsResult(GrantReinsStatus.Unresolved, 0, null, null, null);
        try
        {
            var list = await vehicles.GrantAsync(characterId, ridingId, days, nowOle, ct);
            return new GrantReinsResult(GrantReinsStatus.Granted, ridingId, days, list, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GrantReinsResult(GrantReinsStatus.StoreFailed, ridingId, days, null, ex);
        }
    }
}
