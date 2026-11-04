using RO2.Companions.Domain;
using RO2.GameData.Tables;
using RO2.Inventory.Domain;

namespace RO2.Companions.Application;

/// <summary>How a vehicle extension ended; every status but <see cref="Extended"/> leaves the ticket untouched.</summary>
public enum ExtendRidingStatus
{
    /// <summary>The vehicle was extended and the ticket consumed; the rest of <see cref="ExtendRidingResult"/> is set.</summary>
    Extended,

    /// <summary>The ticket uid is not a bag item (count above 0) of the character.</summary>
    TicketNotHeld,

    /// <summary>No DurationExtendGroup / ItemCashInfo / reins row lets the ticket extend the vehicle.</summary>
    NotExtendable,

    /// <summary>The extension could not be stored (<see cref="ExtendRidingResult.Error"/>).</summary>
    StoreFailed,

    /// <summary>The vehicle is not a timed vehicle the character owns.</summary>
    NotOwnedTimed,
}

/// <summary>
/// The outcome of an extension. <see cref="Ticket"/> is set from NotExtendable on (and after the consume on Extended, with
/// <see cref="Remaining"/> already applied); <see cref="Days"/>, <see cref="ExpiryOle"/> and <see cref="Remaining"/> only on Extended.
/// </summary>
public readonly record struct ExtendRidingResult(ExtendRidingStatus Status, InventoryItem? Ticket, uint Days, double ExpiryOle, int Remaining, Exception? Error);

/// <summary>
/// The store side of 1691 ReqRidingExtendItem: the ticket check, <see cref="VehicleRules.ResolveExtension"/>,
/// <see cref="IVehicleRepository.TryExtendTimedAsync"/>, then the ticket consume. The four game-data tables come in as arguments so this use
/// case never reaches for the game-data store. The clock (<c>nowOle</c>), the 2329 -> 3023 wire and the silent return on every refusal stay in the Zone host.
/// </summary>
public sealed class ExtendRidingUseCase(IVehicleRepository vehicles, IInventoryRepository inventory)
{
    /// <summary>Extends <paramref name="ridingId"/> with ticket <paramref name="ticketUid"/>; see the type remarks. A cancelled call still throws.</summary>
    public async Task<ExtendRidingResult> ExtendAsync(uint characterId, uint ridingId, ulong ticketUid, IReadOnlyList<DurationExtendGroup> extendGroups,
        IReadOnlyList<ItemCashInfo> cashInfo, IReadOnlyList<RidingInfo> ridings, IReadOnlyList<RidingDurationGroup> reinsGroups, double nowOle,
        CancellationToken ct = default)
    {
        var ticket = await inventory.GetByIdAsync(ticketUid);
        if (ticket is null || ticket.CharacterId != characterId || ticket.StorageType != 0 || ticket.Count <= 0)
            return new ExtendRidingResult(ExtendRidingStatus.TicketNotHeld, null, 0, 0, 0, null);
        if (!VehicleRules.ResolveExtension(extendGroups, cashInfo, ridings, reinsGroups, ridingId, ticket.ItemId, out var days))
            return new ExtendRidingResult(ExtendRidingStatus.NotExtendable, ticket, 0, 0, 0, null);

        IReadOnlyList<OwnedVehicle>? list;
        try
        {
            list = await vehicles.TryExtendTimedAsync(characterId, ridingId, days, nowOle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExtendRidingResult(ExtendRidingStatus.StoreFailed, ticket, days, 0, 0, ex);
        }
        if (list is null) return new ExtendRidingResult(ExtendRidingStatus.NotOwnedTimed, ticket, days, 0, 0, null);
        var expiry = list.First(v => v.RidingId == ridingId).ExpiryOle;

        var remaining = ticket.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(ticket.Id); remaining = 0; }
        else { ticket.Count = remaining; await inventory.UpdateItemAsync(ticket); }
        return new ExtendRidingResult(ExtendRidingStatus.Extended, ticket, days, expiry, remaining, null);
    }
}
