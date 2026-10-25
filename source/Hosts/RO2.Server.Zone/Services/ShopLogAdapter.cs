using Microsoft.Extensions.Logging;
using RO2.Economy.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IShopLog"/> over the handler's logger. It owns the log levels and the message
/// templates; the application layer only raises the events, so it stays free of a logging-framework dependency.
/// </summary>
/// <param name="log">The logger of the buy, sell or buy-back handler.</param>
public sealed class ShopLogAdapter(ILogger log) : IShopLog
{
    private readonly ILogger _log = log;

    /// <inheritdoc />
    public void CannotAfford(string playerName, uint quantity, string itemName, long cost, long have)
        => _log.LogInformation("{Name} cannot afford {Count}x '{Item}' ({Cost}g, has {Have}g)",
            playerName, quantity, itemName, cost, have);

    /// <inheritdoc />
    public void BagUnreadable(Exception ex, string playerName, uint quantity, string itemName)
        => _log.LogWarning(ex, "{Name}: bag unreadable for a buy of {Count}x '{Item}'; refused, nothing charged",
            playerName, quantity, itemName);

    /// <inheritdoc />
    public void PossessionExceeded(string playerName, uint quantity, string itemName, long held, int limit)
        => _log.LogInformation("{Name} cannot hold {Count} more '{Item}' ({Held} held, possession limit {Max})",
            playerName, quantity, itemName, held, limit);

    /// <inheritdoc />
    public void TokensShort(string playerName, uint quantity, string itemName, ushort tokenRow)
        => _log.LogInformation("{Name} cannot pay {Count}x '{Item}' with BuyTokenInfo {Token}: the bag holds too few tokens; refused",
            playerName, quantity, itemName, tokenRow);

    /// <inheritdoc />
    public void NoFreeCell(string playerName, uint quantity, string itemName)
        => _log.LogInformation("{Name}: no free bag cell for {Count}x '{Item}'; refused", playerName, quantity, itemName);

    /// <inheritdoc />
    public void Buying(string playerName, uint quantity, string itemName, long cost, uint npcHandle)
        => _log.LogInformation("{Name} buys {Count}x '{Item}' for {Gold}g from NPC 0x{Npc:X8}",
            playerName, quantity, itemName, cost, npcHandle);

    /// <inheritdoc />
    public void BuyNotSaved(Exception ex, string playerName, uint quantity, string itemName)
        => _log.LogWarning(ex, "{Name}: buy of {Count}x '{Item}' could not be saved; refused, nothing delivered or charged",
            playerName, quantity, itemName);

    /// <inheritdoc />
    public void Sold(string playerName, ulong uid, string itemName, int quantity, long gold)
        => _log.LogInformation("{Name} sold uid {Uid} ('{Item}') x{Qty} for {Gold}g", playerName, uid, itemName, quantity, gold);

    /// <inheritdoc />
    public void SoldNothing(string playerName, int requested)
        => _log.LogInformation("{Name} sold nothing: none of the {Count} requested uid(s) is a sellable bag stack",
            playerName, requested);

    /// <inheritdoc />
    public void BuyBackNotListed(string playerName, ulong uid)
        => _log.LogInformation("{Name} tried to buy back uid {Uid}, which is not on the buy-back list; refused", playerName, uid);

    /// <inheritdoc />
    public void BuyBackCannotAfford(string playerName, ulong uid, long cost, long have)
        => _log.LogInformation("{Name} cannot afford to buy back uid {Uid} ({Cost}g, has {Have}g)", playerName, uid, cost, have);

    /// <inheritdoc />
    public void BuyBackNotWritten(Exception ex, string playerName, ulong uid)
        => _log.LogWarning(ex, "{Name}: buy-back of uid {Uid} could not be written; refused", playerName, uid);

    /// <inheritdoc />
    public void BuyBackNoFreeCell(string playerName, ulong uid)
        => _log.LogInformation("{Name}: no free bag cell to buy back uid {Uid}; refused", playerName, uid);

    /// <inheritdoc />
    public void BoughtBack(string playerName, ulong uid, uint itemId, int count, long cost)
        => _log.LogInformation("{Name} bought back uid {Uid} (item {Item} x{Count}) for {Cost}g", playerName, uid, itemId, count, cost);
}
