namespace RO2.Economy.Application;

/// <summary>
/// The operational log of the NPC-shop buy, sell and buy-back flows, as events. The application raises an intention
/// ("a buyer cannot afford it", "a sale went through"); the host implements it over its logger and owns the levels and
/// templates, so the application layer stays free of a logging-framework dependency, like <see cref="IAuctionLog"/>.
/// </summary>
public interface IShopLog
{
    /// <summary>A buy was refused because the wallet is below the cost.</summary>
    void CannotAfford(string playerName, uint quantity, string itemName, long cost, long have);

    /// <summary>A buy was refused because the bag could not be read; nothing was charged.</summary>
    void BagUnreadable(Exception ex, string playerName, uint quantity, string itemName);

    /// <summary>A buy was refused because it would pass the item's possession limit.</summary>
    void PossessionExceeded(string playerName, uint quantity, string itemName, long held, int limit);

    /// <summary>A token buy was refused because the bag holds too few tokens.</summary>
    void TokensShort(string playerName, uint quantity, string itemName, ushort tokenRow);

    /// <summary>A buy was refused because the bag has no free cell for the new stacks.</summary>
    void NoFreeCell(string playerName, uint quantity, string itemName);

    /// <summary>A buy was accepted and is about to be written.</summary>
    void Buying(string playerName, uint quantity, string itemName, long cost, uint npcHandle);

    /// <summary>A buy could not be saved; nothing was delivered or charged.</summary>
    void BuyNotSaved(Exception ex, string playerName, uint quantity, string itemName);

    /// <summary>A stack was sold to the shop.</summary>
    void Sold(string playerName, ulong uid, string itemName, int quantity, long gold);

    /// <summary>A sale request resolved to no sellable bag stack.</summary>
    void SoldNothing(string playerName, int requested);

    /// <summary>A buy-back was refused: the uid is not on the buy-back list.</summary>
    void BuyBackNotListed(string playerName, ulong uid);

    /// <summary>A buy-back was refused because the wallet is below the price the sale paid.</summary>
    void BuyBackCannotAfford(string playerName, ulong uid, long cost, long have);

    /// <summary>A buy-back was refused because the item could not be written; the price was handed back.</summary>
    void BuyBackNotWritten(Exception ex, string playerName, ulong uid);

    /// <summary>A buy-back was refused because the bag has no free cell; the price was handed back.</summary>
    void BuyBackNoFreeCell(string playerName, ulong uid);

    /// <summary>A stack was bought back.</summary>
    void BoughtBack(string playerName, ulong uid, uint itemId, int count, long cost);
}
