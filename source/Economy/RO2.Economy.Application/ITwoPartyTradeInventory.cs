using RO2.Economy.Domain;

namespace RO2.Economy.Application;

/// <summary>One item a trade side has in the window.</summary>
/// <param name="Uid">The offered item instance uid.</param>
/// <param name="Qty">The offered quantity; null when none was recorded (the whole stack is meant).</param>
/// <param name="Snapshot">The item as the window showed it; null when none was recorded.</param>
public sealed record TradeOfferedItem(ulong Uid, int? Qty, TradeOfferSnapshot? Snapshot);

/// <summary>One trader in a swap request.</summary>
/// <param name="CharacterId">The trader's character id.</param>
/// <param name="Items">The items the trader offers, in offer order.</param>
public sealed record TradeSwapSide(uint CharacterId, IReadOnlyList<TradeOfferedItem> Items);

/// <summary>Both traders' offers for the two-sided item exchange.</summary>
/// <param name="TradeId">Identifies the trade in the port's own logs.</param>
/// <param name="A">The confirmer, side A of the swap.</param>
/// <param name="B">The partner, side B of the swap.</param>
public sealed record TradeSwapRequest(Guid TradeId, TradeSwapSide A, TradeSwapSide B);

/// <summary>How the item exchange ended.</summary>
public enum TradeSwapStatus
{
    /// <summary>Every offered item moved to the other trader.</summary>
    Swapped,

    /// <summary>An offered item is no longer what the window showed, or no longer its offerer's. Nothing moved.</summary>
    OwnershipFailed,

    /// <summary>A recipient's bag cannot take the goods. Nothing moved.</summary>
    NoRoom,

    /// <summary>A write threw before any item moved. Nothing moved.</summary>
    FaultedBeforeAnyMove,

    /// <summary>A write threw after some items moved. The items already moved stay moved (no rollback).</summary>
    FaultedMidSwap,
}

/// <summary>What the item exchange did.</summary>
/// <param name="Status">How the exchange ended.</param>
public sealed record TradeSwapOutcome(TradeSwapStatus Status)
{
    /// <summary>With <see cref="TradeSwapStatus.NoRoom"/>: side A's bag cannot take side B's goods.</summary>
    public bool NoRoomForA { get; init; }

    /// <summary>With <see cref="TradeSwapStatus.NoRoom"/>: side B's bag cannot take side A's goods.</summary>
    public bool NoRoomForB { get; init; }

    /// <summary>With <see cref="TradeSwapStatus.Swapped"/>: the template of each item A gave, in offer order.</summary>
    public IReadOnlyList<uint> TemplatesFromA { get; init; } = [];

    /// <summary>With <see cref="TradeSwapStatus.Swapped"/>: the template of each item B gave, in offer order.</summary>
    public IReadOnlyList<uint> TemplatesFromB { get; init; } = [];

    /// <summary>With a faulted status: the write failure.</summary>
    public Exception? Fault { get; init; }

    /// <summary>With a faulted status: how many writes landed before the failure.</summary>
    public int Writes { get; init; }

    /// <summary>With a faulted status: the offered uids (A's then B's) not yet taken off their giver, for reconciliation.</summary>
    public IReadOnlyList<ulong> NotYetMoved { get; init; } = [];
}

/// <summary>
/// The two-sided inventory exchange of a player trade. The implementation holds BOTH traders' inventory gates (the
/// lower character id first, one gate when the ids are equal), re-validates the offered items against the live rows
/// and the snapshots the window showed, plans both directions with <see cref="TradePlanner"/> and applies the moves
/// with the repository's owner-checked writes. It moves NO zeny, and it does NOT roll items back when a write fails
/// part-way: a failure after the first move leaves those items moved, which the caller reports for reconciliation.
/// </summary>
public interface ITwoPartyTradeInventory
{
    /// <summary>
    /// Validates, plans and moves both offers. A write failure during the move is reported as
    /// <see cref="TradeSwapStatus.FaultedBeforeAnyMove"/> or <see cref="TradeSwapStatus.FaultedMidSwap"/>, not thrown;
    /// a failure while validating or planning throws.
    /// </summary>
    /// <param name="request">Both traders' offers.</param>
    Task<TradeSwapOutcome> SwapItemsAsync(TradeSwapRequest request);
}
