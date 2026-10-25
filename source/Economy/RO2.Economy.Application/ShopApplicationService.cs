using RO2.BuildingBlocks;
using RO2.Economy.Domain;

namespace RO2.Economy.Application;

/// <summary>What a template pays when sold to an NPC shop; null terms mean the template cannot be sold.</summary>
/// <param name="UnitSellPrice">The item's own sell price (ItemInfo Price_Sell, not half the buy price).</param>
/// <param name="Name">The template's display name, for the log.</param>
public sealed record ShopSellTerms(long UnitSellPrice, string Name);

/// <summary>Pre-validated inputs for a shop buy: the host has resolved the NPC, the Merchant row, the template and the
/// possession limit; the service owns the wallet, bag and placement rules.</summary>
/// <param name="CharacterId">The buyer.</param>
/// <param name="PlayerName">The buyer's name, for the log.</param>
/// <param name="NpcHandle">The shop NPC's actor handle, for the log.</param>
/// <param name="ItemId">The bought template.</param>
/// <param name="ItemName">The template's display name, for the log.</param>
/// <param name="Quantity">The bought quantity (at least 1).</param>
/// <param name="UnitBuyPrice">The template's zeny buy price (ignored by a token buy).</param>
/// <param name="StackMax">The template's stack maximum.</param>
/// <param name="TemplateDurability">The template's durability, carried by a new stack.</param>
/// <param name="PossessionLimit">The item's possession limit; 0 or less for none.</param>
/// <param name="Token">What a token buy costs per unit; null for a zeny buy.</param>
/// <param name="TokenRowId">The BuyTokenInfo row of a token buy, for the log (0 for a zeny buy).</param>
public sealed record ShopBuyCommand(
    uint CharacterId, string PlayerName, uint NpcHandle, uint ItemId, string ItemName, uint Quantity,
    long UnitBuyPrice, byte StackMax, uint TemplateDurability, int PossessionLimit, TokenCost? Token, ushort TokenRowId);

/// <summary>How a shop buy ended: either bought (<see cref="Written"/> is set) or refused with the 2375 failure code
/// (the reserved zeny is already handed back).</summary>
/// <param name="RefusalCode">The 2375 failure code (a <see cref="ShopCodes"/> value); 0 when bought.</param>
/// <param name="Written">What the buy wrote; null when refused.</param>
public sealed record ShopBuyResult(ushort RefusalCode, ShopBuyWrite? Written)
{
    /// <summary>True when the buy went through.</summary>
    public bool Bought => Written is not null;
}

/// <summary>Pre-validated inputs for a shop sale.</summary>
/// <param name="CharacterId">The seller.</param>
/// <param name="PlayerName">The seller's name, for the log.</param>
/// <param name="Entries">The requested stacks, in request order.</param>
/// <param name="TermsOf">What a template pays, or null when it cannot be sold (unknown, or ItemInfo Is_Sell 0).</param>
public sealed record ShopSellCommand(
    uint CharacterId, string PlayerName, IReadOnlyList<ShopSellEntry> Entries, Func<uint, ShopSellTerms?> TermsOf);

/// <summary>How a shop sale ended. When nothing was sold <see cref="Lines"/> is empty and the wallet is untouched.</summary>
/// <param name="Lines">The stacks taken, in request order, each as it was before the sale.</param>
/// <param name="SoldTemplates">The distinct templates sold, in first-sold order.</param>
/// <param name="BuyBackUids">The buy-back list after this request's sales were recorded and before the wallet was credited.</param>
/// <param name="Proceeds">The zeny the sale paid.</param>
public sealed record ShopSellResult(
    IReadOnlyList<ShopSaleLine> Lines, IReadOnlyList<uint> SoldTemplates, ulong[] BuyBackUids, long Proceeds)
{
    /// <summary>True when at least one stack was sold.</summary>
    public bool Sold => Lines.Count > 0;
}

/// <summary>A request to buy back a stack sold to the shop.</summary>
/// <param name="CharacterId">The buyer.</param>
/// <param name="PlayerName">The buyer's name, for the log.</param>
/// <param name="Uid">The uid of the sold stack, as the client's buy-back list names it.</param>
public sealed record ShopBuyBackCommand(uint CharacterId, string PlayerName, ulong Uid);

/// <summary>How a buy-back ended.</summary>
/// <param name="Code">The 2377 result: 0 bought back, else a <see cref="ShopCodes"/> refusal (signed 16-bit).</param>
/// <param name="Restored">The stack as stored in the bag; null when refused.</param>
public sealed record ShopBuyBackResult(short Code, ItemStackSnapshot? Restored);

/// <summary>
/// The NPC-shop economy flows as application logic: a buy (zeny reserved, bag read, possession, tokens, placement,
/// write, persist), a sale (take under the inventory gate, credit, persist, buy-back list) and a buy-back. It knows
/// nothing of the wire, the player object, the game data or the logger: the host validates the request, supplies the
/// wallet, the inventory and log ports and the wallet's persistence, sends every packet and sequences the sends around
/// these calls. The service itself sends nothing.
/// </summary>
public sealed class ShopApplicationService
{
    /// <summary>
    /// Buys <paramref name="cmd"/>'s item. The cost is TAKEN from <paramref name="wallet"/> before anything else (a
    /// check followed by a later deduct would let a concurrent spender drain the same funds in between) and handed
    /// back on every later refusal; a token buy costs no zeny. The order is debit, read bag, possession limit, token
    /// plan, placement plan, write, <paramref name="onWritten"/>, persist (zeny path only), so a failure never leaves
    /// the item delivered with its tokens untaken. A refusal is the 2375 failure code, with the zeny already back.
    /// </summary>
    /// <param name="cmd">The pre-validated buy.</param>
    /// <param name="wallet">The buyer's wallet.</param>
    /// <param name="inventory">The inventory port.</param>
    /// <param name="log">The flow's operational log.</param>
    /// <param name="persistWalletAsync">Writes the wallet through; called after a successful zeny buy, never for a token buy.</param>
    /// <param name="onWritten">Called once the write succeeded and before the wallet is persisted, so the host can send
    /// what must precede the persist (the token 2379s).</param>
    public async Task<ShopBuyResult> BuyAsync(ShopBuyCommand cmd, IWallet wallet, IShopInventory inventory, IShopLog log,
        Func<Task> persistWalletAsync, Func<ShopBuyWrite, Task>? onWritten = null)
    {
        // A token buy costs no zeny (the 2375 wallet is unchanged). A zeny buy costs Price_Buy x count. The timed
        // shop-discount state is not modelled, so no discount is applied.
        long totalCost = cmd.Token is null ? ShopPricing.BuyCost(cmd.UnitBuyPrice, cmd.Quantity) : 0L;

        // Zeny held back from the wallet once the buy is affordable; a refusal after that hands it back.
        long reservedZeny = 0;
        ShopBuyResult Refused(ushort code)
        {
            if (reservedZeny > 0) { wallet.Credit(reservedZeny); reservedZeny = 0; }
            return new ShopBuyResult(code, null);
        }

        // Can't buy what you can't afford. The cost is TAKEN here, atomically (TryDebit), not just compared.
        if (!wallet.TryDebit(totalCost))
        {
            log.CannotAfford(cmd.PlayerName, cmd.Quantity, cmd.ItemName, totalCost, wallet.Gold);
            return Refused(ShopCodes.BuyFailGeneric);
        }
        reservedZeny = totalCost;

        IShopBag bag;
        try
        {
            bag = await inventory.ReadBagAsync(cmd.CharacterId);
        }
        catch (Exception ex)
        {
            log.BagUnreadable(ex, cmd.PlayerName, cmd.Quantity, cmd.ItemName);
            return Refused(ShopCodes.BuyFailGeneric);
        }

        // Possession limit: the bag total after the buy may not pass it.
        if (ShopBuyPlanner.ExceedsPossessionLimit(bag.Stacks, cmd.ItemId, cmd.Quantity, cmd.PossessionLimit, out var held))
        {
            log.PossessionExceeded(cmd.PlayerName, cmd.Quantity, cmd.ItemName, held, cmd.PossessionLimit);
            return Refused(ShopCodes.BuyFailPossession);
        }

        IReadOnlyList<StackTake>? tokenTakes = null;
        if (cmd.Token is not null)
        {
            tokenTakes = ShopBuyPlanner.PlanTokenTake(bag.Stacks, cmd.Token, (int)cmd.Quantity);
            if (tokenTakes is null)
            {
                log.TokensShort(cmd.PlayerName, cmd.Quantity, cmd.ItemName, cmd.TokenRowId);
                return Refused(ShopCodes.BuyFailGeneric);
            }
        }

        // The cells are planned on the bag as it stands before the tokens are taken: a new stack is never put
        // into a cell a token take empties.
        var plan = ShopBuyPlanner.Plan(bag.Stacks, bag.FreeCells, cmd.ItemId, (int)cmd.Quantity, cmd.StackMax);
        if (plan is null)
        {
            log.NoFreeCell(cmd.PlayerName, cmd.Quantity, cmd.ItemName);
            return Refused(ShopCodes.BuyFailBagFull);
        }

        log.Buying(cmd.PlayerName, cmd.Quantity, cmd.ItemName, totalCost, cmd.NpcHandle);

        // Persist BEFORE charging: if the bag cannot be written nothing was delivered, so the buy is refused (and the
        // reserved zeny handed back) instead of charging for a record that would vanish on relog. The bought stacks
        // AND the token takes are one unit: if any write fails the ones already made are undone.
        ShopBuyWrite written;
        try
        {
            written = await inventory.WriteBuyAsync(bag, cmd.CharacterId, cmd.ItemId, (int)cmd.Quantity, plan,
                cmd.TemplateDurability, tokenTakes);
        }
        catch (Exception ex)
        {
            log.BuyNotSaved(ex, cmd.PlayerName, cmd.Quantity, cmd.ItemName);
            return Refused(ShopCodes.BuyFailGeneric);
        }

        // The host's sends that go out only now that every write succeeded (the token 2379s) come before the persist.
        if (onWritten is not null) await onWritten(written);

        if (cmd.Token is null)
        {
            reservedZeny = 0;   // the charge already happened at the affordability check
            // Write-through: persist the wallet immediately so a crash can't lose a purchase (the stacks are
            // already durable above).
            await persistWalletAsync();
        }
        return new ShopBuyResult(0, written);
    }

    /// <summary>
    /// Sells the requested stacks. The inventory port takes them atomically and owner-checked BEFORE the wallet is
    /// credited, so nothing is paid for a stack that a concurrent session already took; each sale is put on
    /// <paramref name="buyBack"/> as it is taken. When something sold, the buy-back uid list is read after all this
    /// request's sales were recorded and before the credit, then the wallet is credited and persisted. When nothing
    /// sold, the wallet and the list are untouched.
    /// </summary>
    /// <param name="cmd">The pre-validated sale.</param>
    /// <param name="wallet">The seller's wallet.</param>
    /// <param name="buyBack">The seller's buy-back list.</param>
    /// <param name="inventory">The inventory port.</param>
    /// <param name="log">The flow's operational log.</param>
    /// <param name="persistWalletAsync">Writes the wallet through; called after the credit.</param>
    public async Task<ShopSellResult> SellAsync(ShopSellCommand cmd, IWallet wallet, BuyBackList buyBack,
        IShopInventory inventory, IShopLog log, Func<Task> persistWalletAsync)
    {
        long proceeds = 0;
        var lines = new List<ShopSaleLine>();
        var soldTemplates = new List<uint>();

        await inventory.TakeForSaleAsync(cmd.CharacterId, cmd.Entries, snapshot => cmd.TermsOf(snapshot.ItemId) is not null, line =>
        {
            // Each item carries its OWN sell price (ItemInfo Price_Sell), it is NOT half the buy price.
            var terms = cmd.TermsOf(line.Row.ItemId)!;
            long paid = ShopPricing.SellProceeds(terms.UnitSellPrice, line.SellQty);
            proceeds += paid;
            lines.Add(line);
            if (!soldTemplates.Contains(line.Row.ItemId)) soldTemplates.Add(line.Row.ItemId);

            // Put the sale on the buy-back list that 1336 ReqCancelSellItem draws from; Paid is what this sale
            // credits, which is what the buy-back charges. Recorded only after the inventory write went through,
            // so a failed write cannot leave a buy-back entry for an item the player still has.
            buyBack.RecordSale(line.Row, line.SellQty, paid);

            log.Sold(cmd.PlayerName, line.Row.Id, terms.Name, line.SellQty, paid);
        });

        if (lines.Count == 0)
        {
            // Nothing resolved: the host answers the "nothing sold" 2376, never an empty success frame and never
            // silence, e.g. to the second click of a double-clicked sell whose uid the first one already sold.
            log.SoldNothing(cmd.PlayerName, cmd.Entries.Count);
            return new ShopSellResult(lines, soldTemplates, [], 0);
        }

        // blob+18 is the buy-back list itself: the client builds its buy-back window from it. The list is read from
        // the ledger after this request's sales were recorded.
        var soldList = buyBack.Uids();

        wallet.Credit(proceeds);
        // Write-through the new wallet total so the sale is durable on the next crash.
        await persistWalletAsync();

        return new ShopSellResult(lines, soldTemplates, soldList, proceeds);
    }

    /// <summary>
    /// Buys back a stack sold to the shop at what the sale paid. The entry leaves <paramref name="buyBack"/> atomically
    /// (two requests for one uid cannot both succeed) and goes back where it was whenever the buy-back does not
    /// complete; the price is taken from <paramref name="wallet"/> first and handed back on every refusal. On success
    /// the wallet is persisted before the result returns. A refusal is the 2377 code with nothing charged or given.
    /// </summary>
    /// <param name="cmd">The buy-back request.</param>
    /// <param name="wallet">The buyer's wallet.</param>
    /// <param name="buyBack">The buyer's buy-back list.</param>
    /// <param name="inventory">The inventory port.</param>
    /// <param name="log">The flow's operational log.</param>
    /// <param name="persistWalletAsync">Writes the wallet through; called after a successful buy-back.</param>
    public async Task<ShopBuyBackResult> BuyBackAsync(ShopBuyBackCommand cmd, IWallet wallet, BuyBackList buyBack,
        IShopInventory inventory, IShopLog log, Func<Task> persistWalletAsync)
    {
        if (!buyBack.TryTake(cmd.Uid, out var entry, out var index))
        {
            // Not on the buy-back list: already bought back (the usual refusal), or a sale this server has no
            // copy of, so there is no item to give back.
            log.BuyBackNotListed(cmd.PlayerName, cmd.Uid);
            return new ShopBuyBackResult(ShopCodes.CancelRefused, null);
        }

        // The price is taken here, atomically; the paths below hand it back if the item cannot be written.
        if (!wallet.TryDebit(entry.Paid))
        {
            buyBack.Restore(entry, index);
            log.BuyBackCannotAfford(cmd.PlayerName, cmd.Uid, entry.Paid, wallet.Gold);
            return new ShopBuyBackResult(ShopCodes.CancelRefused, null);
        }

        ItemStackSnapshot? placed;
        try
        {
            placed = await inventory.PlaceBackAsync(cmd.CharacterId, entry.Item);
        }
        catch (Exception ex)
        {
            wallet.Credit(entry.Paid);
            buyBack.Restore(entry, index);
            log.BuyBackNotWritten(ex, cmd.PlayerName, cmd.Uid);
            return new ShopBuyBackResult(ShopCodes.CancelRefused, null);
        }

        if (placed is null)
        {
            // Bag full: hand the price back, keep the sale on the buy-back list, deliver nothing.
            wallet.Credit(entry.Paid);
            buyBack.Restore(entry, index);
            log.BuyBackNoFreeCell(cmd.PlayerName, cmd.Uid);
            return new ShopBuyBackResult(ShopCodes.CancelBagFull, null);
        }

        // Write the wallet through, as the sale did, so a crash cannot keep the item and the zeny.
        await persistWalletAsync();

        log.BoughtBack(cmd.PlayerName, cmd.Uid, placed.ItemId, placed.Count, entry.Paid);
        return new ShopBuyBackResult(0, placed);
    }
}
