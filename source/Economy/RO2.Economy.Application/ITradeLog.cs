namespace RO2.Economy.Application;

/// <summary>
/// The operational log of the player-trade swap, as events. The application raises an intention ("the swap failed
/// mid-way", "the trade completed"); the host implements it over its logger and owns the levels and templates, so the
/// application layer stays free of a logging-framework dependency, like <see cref="IShopLog"/>.
/// </summary>
public interface ITradeLog
{
    /// <summary>The trade was aborted: an ownership check failed, or a trader cannot afford their offer.</summary>
    void OwnershipOrAffordabilityFailed(string nameA, string nameB);

    /// <summary>The trade was refused because a recipient's bag has no free cell; nothing moved.</summary>
    void NoBagRoom(string nameA, string nameB, bool noRoomForA, bool noRoomForB);

    /// <summary>The trade was aborted at the zeny escrow: <paramref name="payer"/> can no longer pay the offer.</summary>
    void CannotPay(string nameA, string nameB, string payer, long gold);

    /// <summary>The swap failed before any item moved; the zeny was returned.</summary>
    void AbortedBeforeAnyMove(Exception ex, Guid tradeId, string nameA, string nameB);

    /// <summary>The swap failed after some items moved; the zeny was returned, the items stay moved.</summary>
    void FailedMidSwap(Exception ex, Guid tradeId, string nameA, string nameB, int writes,
        IReadOnlyList<ulong> notYetMoved, long goldA, long goldB);

    /// <summary>A trader's wallet row could not be written after the swap; the write-behind flush persists it.</summary>
    void WalletNotPersisted(Exception ex, Guid tradeId, string name, long gold);

    /// <summary>The trade completed.</summary>
    void Completed(string nameA, int itemsA, long goldA, string nameB, int itemsB, long goldB);
}
