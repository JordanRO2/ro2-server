using Microsoft.Extensions.Logging;
using RO2.Economy.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="ITradeLog"/> over the handler's logger. It owns the log levels and the message
/// templates; the application layer only raises the events, so it stays free of a logging-framework dependency.
/// </summary>
/// <param name="log">The logger of the trade confirm handler.</param>
public sealed class TradeLogAdapter(ILogger log) : ITradeLog
{
    private readonly ILogger _log = log;

    /// <inheritdoc />
    public void OwnershipOrAffordabilityFailed(string nameA, string nameB)
        => _log.LogWarning("Trade {A}<->{B} aborted: ownership/affordability check failed", nameA, nameB);

    /// <inheritdoc />
    public void NoBagRoom(string nameA, string nameB, bool noRoomForA, bool noRoomForB)
        => _log.LogInformation("Trade {A}<->{B} refused: no free bag cell for {Who}", nameA, nameB,
            noRoomForB && noRoomForA ? "either side" : noRoomForB ? nameB : nameA);

    /// <inheritdoc />
    public void CannotPay(string nameA, string nameB, string payer, long gold)
        => _log.LogWarning("Trade {A}<->{B} aborted: {Payer} can no longer pay {Gold}z", nameA, nameB, payer, gold);

    /// <inheritdoc />
    public void AbortedBeforeAnyMove(Exception ex, Guid tradeId, string nameA, string nameB)
        => _log.LogWarning(ex, "Trade {TradeId} {A}<->{B} aborted before any item moved; zeny returned",
            tradeId, nameA, nameB);

    /// <inheritdoc />
    public void FailedMidSwap(Exception ex, Guid tradeId, string nameA, string nameB, int writes,
        IReadOnlyList<ulong> notYetMoved, long goldA, long goldB)
        => _log.LogError(ex,
            "Trade {TradeId} {A}<->{B} failed MID-SWAP after {Writes} writes: not yet moved uids [{Remaining}], "
            + "{A} offered {AGold}z and {B} offered {BGold}z (both returned to their owners); items already moved stay moved",
            tradeId, nameA, nameB, writes,
            string.Join(",", notYetMoved.Select(u => u.ToString("X"))),
            nameA, goldA, nameB, goldB);

    /// <inheritdoc />
    public void WalletNotPersisted(Exception ex, Guid tradeId, string name, long gold)
        => _log.LogError(ex, "Trade {TradeId}: items moved and zeny credited in memory, but the wallet row of {Name} "
            + "was not written ({Gold}z); the write-behind flush persists it", tradeId, name, gold);

    /// <inheritdoc />
    public void Completed(string nameA, int itemsA, long goldA, string nameB, int itemsB, long goldB)
        => _log.LogInformation(
            "Trade complete: {A} gave {AItems} items + {AGold}z, {B} gave {BItems} items + {BGold}z",
            nameA, itemsA, goldA, nameB, itemsB, goldB);
}
