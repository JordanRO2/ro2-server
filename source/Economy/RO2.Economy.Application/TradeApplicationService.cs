using RO2.BuildingBlocks;

namespace RO2.Economy.Application;

/// <summary>Pre-validated inputs for the confirm-time swap of a trade: both offers as the window agreed them.</summary>
/// <param name="TradeId">Identifies the trade in the logs.</param>
/// <param name="NameA">Side A's (the confirmer's) name, for the log.</param>
/// <param name="NameB">Side B's (the partner's) name, for the log.</param>
/// <param name="GoldA">The zeny side A offers (never negative).</param>
/// <param name="GoldB">The zeny side B offers (never negative).</param>
/// <param name="SideA">Side A's character and offered items.</param>
/// <param name="SideB">Side B's character and offered items.</param>
public sealed record TradeSwapCommand(
    Guid TradeId, string NameA, string NameB, long GoldA, long GoldB, TradeSwapSide SideA, TradeSwapSide SideB);

/// <summary>How the swap ended, for the host to pick the frames to send.</summary>
public enum TradeResultKind
{
    /// <summary>Items and zeny changed hands.</summary>
    Completed,

    /// <summary>The trade could not be carried out (ownership, affordability or a write failure); the host closes both
    /// windows with the cancelled frame. Zeny is back with its owners; after a mid-swap failure the items already moved
    /// stay moved.</summary>
    Cancelled,

    /// <summary>A recipient's bag has no room; the host answers each trader with the full-bag refusal. Nothing moved.</summary>
    NoRoom,
}

/// <summary>What the swap did, with what the host needs to build its frames.</summary>
/// <param name="Kind">How the swap ended.</param>
public sealed record TradeSwapResult(TradeResultKind Kind)
{
    /// <summary>With <see cref="TradeResultKind.Completed"/>: side A's balance after the trade.</summary>
    public long GoldA { get; init; }

    /// <summary>With <see cref="TradeResultKind.Completed"/>: side B's balance after the trade.</summary>
    public long GoldB { get; init; }

    /// <summary>With <see cref="TradeResultKind.Completed"/>: the distinct templates that changed hands (A's first).</summary>
    public IReadOnlyList<uint> MovedTemplates { get; init; } = [];

    /// <summary>With <see cref="TradeResultKind.Completed"/>: the templates side A received, in offer order.</summary>
    public IReadOnlyList<uint> ReceivedByA { get; init; } = [];

    /// <summary>With <see cref="TradeResultKind.Completed"/>: the templates side B received, in offer order.</summary>
    public IReadOnlyList<uint> ReceivedByB { get; init; } = [];
}

/// <summary>
/// The confirm-time swap of a player trade as application logic: the zeny escrow, the two-sided item exchange through
/// <see cref="ITwoPartyTradeInventory"/> and the zeny hand-over, in the order the original handler ran them. It knows
/// nothing of the wire, the player object or the logger: the host supplies the wallets, the inventory and log ports
/// and the wallets' persistence, and builds and sends every frame from the result. The service itself sends nothing.
/// </summary>
public sealed class TradeApplicationService
{
    /// <summary>
    /// Carries out a trade that both sides locked and confirmed. Each offer is TAKEN from its wallet before anything
    /// moves, with no clamp (a wallet that spent the funds since the window refuses the trade instead of paying out of
    /// nothing); both debits succeed or neither does. The items then move through <paramref name="inventory"/>; the
    /// zeny changes hands only after every item has (it is escrowed meanwhile, so nothing is spendable). When the
    /// exchange is refused (ownership, no room) or a write fails, both offers go back to their owners; items that
    /// already moved before a mid-swap failure stay moved (not rolled back), and that is logged for reconciliation. On
    /// success each wallet is credited with the other side's offer and then persisted, side A first, a failed row
    /// write being logged and not undoing the trade.
    /// </summary>
    /// <param name="cmd">The pre-validated swap.</param>
    /// <param name="walletA">Side A's (the confirmer's) wallet.</param>
    /// <param name="walletB">Side B's (the partner's) wallet.</param>
    /// <param name="inventory">The two-sided inventory port.</param>
    /// <param name="log">The flow's operational log.</param>
    /// <param name="persistA">Writes side A's wallet through.</param>
    /// <param name="persistB">Writes side B's wallet through.</param>
    public async Task<TradeSwapResult> ExecuteSwapAsync(TradeSwapCommand cmd, IWallet walletA, IWallet walletB,
        ITwoPartyTradeInventory inventory, ITradeLog log, Func<Task> persistA, Func<Task> persistB)
    {
        // Affordability up front, as the validation boundary: a trader that cannot cover its offer ends the trade
        // before anything is taken or touched.
        if (walletA.Gold < cmd.GoldA || walletB.Gold < cmd.GoldB)
        {
            log.OwnershipOrAffordabilityFailed(cmd.NameA, cmd.NameB);
            return new TradeSwapResult(TradeResultKind.Cancelled);
        }

        // ---- ESCROW THE ZENY (atomic, before anything moves) ----
        // Each side's offer is taken from its wallet here, with no clamp: a wallet that spent the funds since the
        // check above refuses the trade instead of paying out of nothing. Both debits succeed or neither does.
        if (!walletA.TryDebit(cmd.GoldA))
        {
            log.CannotPay(cmd.NameA, cmd.NameB, cmd.NameA, cmd.GoldA);
            return new TradeSwapResult(TradeResultKind.Cancelled);
        }
        if (!walletB.TryDebit(cmd.GoldB))
        {
            walletA.Credit(cmd.GoldA);
            log.CannotPay(cmd.NameA, cmd.NameB, cmd.NameB, cmd.GoldB);
            return new TradeSwapResult(TradeResultKind.Cancelled);
        }

        // ---- APPLY (move, not copy) ----
        TradeSwapOutcome outcome;
        try
        {
            outcome = await inventory.SwapItemsAsync(new TradeSwapRequest(cmd.TradeId, cmd.SideA, cmd.SideB));
        }
        catch
        {
            // Validation or planning failed with the offers escrowed and nothing moved: hand them back, then let the
            // failure propagate as it always did.
            Refund(cmd, walletA, walletB);
            throw;
        }

        switch (outcome.Status)
        {
            case TradeSwapStatus.Swapped:
                break;

            case TradeSwapStatus.NoRoom:
                Refund(cmd, walletA, walletB);
                log.NoBagRoom(cmd.NameA, cmd.NameB, outcome.NoRoomForA, outcome.NoRoomForB);
                return new TradeSwapResult(TradeResultKind.NoRoom);

            case TradeSwapStatus.FaultedBeforeAnyMove:
                // A throw, or a move the giver's row no longer allows, ends the swap: the escrow goes back to its owners
                // and both windows are closed.
                Refund(cmd, walletA, walletB);
                log.AbortedBeforeAnyMove(outcome.Fault!, cmd.TradeId, cmd.NameA, cmd.NameB);
                return new TradeSwapResult(TradeResultKind.Cancelled);

            case TradeSwapStatus.FaultedMidSwap:
                // Items are not rolled back, so a failure after the first move leaves a bounded loss for one side, never
                // a copy; that case is logged for reconciliation with the trade id, the uids not yet moved and the zeny.
                Refund(cmd, walletA, walletB);
                log.FailedMidSwap(outcome.Fault!, cmd.TradeId, cmd.NameA, cmd.NameB, outcome.Writes,
                    outcome.NotYetMoved, cmd.GoldA, cmd.GoldB);
                return new TradeSwapResult(TradeResultKind.Cancelled);

            default: // OwnershipFailed
                Refund(cmd, walletA, walletB);
                log.OwnershipOrAffordabilityFailed(cmd.NameA, cmd.NameB);
                return new TradeSwapResult(TradeResultKind.Cancelled);
        }

        // Wallets: each side loses what it offered (escrowed above) and gains what the other offered. The live wallet
        // is the truth, so a failed row write is logged and the trade still completes.
        walletA.Credit(cmd.GoldB);
        walletB.Credit(cmd.GoldA);
        await PersistAsync(persistA, log, cmd.TradeId, cmd.NameA, walletA);
        await PersistAsync(persistB, log, cmd.TradeId, cmd.NameB, walletB);

        log.Completed(cmd.NameA, outcome.TemplatesFromA.Count, cmd.GoldA, cmd.NameB, outcome.TemplatesFromB.Count, cmd.GoldB);

        return new TradeSwapResult(TradeResultKind.Completed)
        {
            GoldA = walletA.Gold,
            GoldB = walletB.Gold,
            MovedTemplates = outcome.TemplatesFromA.Concat(outcome.TemplatesFromB).Distinct().ToList(),
            ReceivedByA = outcome.TemplatesFromB,
            ReceivedByB = outcome.TemplatesFromA,
        };
    }

    private static void Refund(TradeSwapCommand cmd, IWallet walletA, IWallet walletB)
    {
        walletA.Credit(cmd.GoldA);
        walletB.Credit(cmd.GoldB);
    }

    private static async Task PersistAsync(Func<Task> persist, ITradeLog log, Guid tradeId, string name, IWallet wallet)
    {
        try { await persist(); }
        catch (Exception ex) { log.WalletNotPersisted(ex, tradeId, name, wallet.Gold); }
    }
}
