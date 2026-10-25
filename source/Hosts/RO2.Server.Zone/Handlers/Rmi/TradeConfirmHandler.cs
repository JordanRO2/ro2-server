using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1357 ReqPCTradeConfirm - Confirm trade. When BOTH sides have locked AND confirmed, this is the
/// point at which the two offers are atomically exchanged: every offered item is re-validated to still
/// belong to its offerer, then each item is MOVED (owner reassigned, never copied) to the other player
/// and both wallets are adjusted, then 2389 OnConfirmTradePCNotify is sent to both.
/// </summary>
public class TradeConfirmHandler : ZoneRmiHandlerBase<TradeConfirmHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTradeConfirm;
    public override string Name => "TradeConfirm";
    public TradeConfirmHandler(IServiceScopeFactory s, ILogger<TradeConfirmHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // 1357 is a confirm TOGGLE, not a one-way confirm: the body is innerSize 9,
        // msgTag 58046, the sender's own handle @blob+4 and a u8 argument @blob+8, which is the
        // compact-BA body offset 10 once the [marker][len] prefix is counted. The argument is 1 to confirm and 0
        // to take the confirm back, and the 2388 result byte mirrors it.
        if (payload.Length < 11) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var confirmFlag = reader.ReadByte(); // 1 = confirm, 0 = take the confirm back

        Logger.LogInformation("{Name} {Action} trade", player.Name, confirmFlag != 0 ? "confirms" : "un-confirms");

        var trade = TradeSessionService.Instance.Get(player.SessionId);

        // NO live trade -> answer NOTHING. The client sends a flag-0 1357 as a tail-off after a trade
        // request it declined, and it is left unanswered: the decliner rejects with 1352 flag 0, receives the
        // reject 2383, then sends 1357 flag 0 - and no 2388 follows it. It is the live-ness that decides,
        // not the flag: a flag-0 1357 sent INSIDE a live trade is answered with a
        // result-0 2388 on both sides. Answering the dead one makes the client see a
        // result byte != 1 and pop strid 324 "Trade was cancelled." at somebody who had just declined.
        if (trade == null)
        {
            Logger.LogDebug("{Name} sent a trade confirm with no live session; nothing to answer", player.Name);
            return;
        }

        // The swap is already running (the partner's confirm completed the round). A 1357 sent at that point
        // is left unanswered: a flag-0 1357 that follows its sender's own completing confirm gets no 2388,
        // only the 2389 that was coming.
        if (trade.Completing)
        {
            Logger.LogDebug("{Name} sent a trade confirm while the swap is running; nothing to answer", player.Name);
            return;
        }

        // A zero flag CANCELS the trade: each client gets 2388 carrying ITS OWN handle and result 0,
        // which the client answers with strid 324 "Trade was cancelled." and closes the window.
        // Every result-0 2388 carries the receiving client's own handle, and the partner gets its own-handle 2388
        // too (never the canceller's handle). Sending the canceller's handle to both, as this used to, is a frame
        // that is never valid. The session is dropped: nobody can see or finish it any more.
        if (confirmFlag == 0)
        {
            TradeSessionService.Instance.Cancel(player.SessionId);
            foreach (var sid in new[] { trade.SessionA, trade.SessionB })
                await SendCancelledAsync(Broadcast.GetSession(sid), trade.HandleFor(sid));
            return;
        }

        // 2388 OnConfirmTradePC goes to BOTH sides and names the CONFIRMER, so each client can light
        // up the right half of the window: the handle at blob+8 is either the
        // recipient's own or the partner's, and both 2388s of a trade arrive on both clients.
        // The old body was 8 bytes, which left the client reading its result byte (blob+12)
        // past the end of the ByteArray -> !=1 -> strid 324 "Trade was cancelled." + window closed on
        // every confirm. The correct body is 15 bytes and echoes the request flag.
        var ack = TradePackets.BuildOnConfirmTradePC(trade.HandleFor(player.SessionId), confirmFlag);
        foreach (var sid in new[] { trade.SessionA, trade.SessionB })
        {
            var s = Broadcast.GetSession(sid);
            if (s != null)
                await s.SendRmiAsync(ZoneS2COpcodes.OnConfirmTradePC, ack);
        }

        // Latch: only the confirm that finds BOTH sides locked+confirmed proceeds to the swap, exactly
        // once. Every earlier confirm just returns here.
        if (!trade.ConfirmAndTryComplete(player.SessionId))
            return;

        await ExecuteSwapAsync(trade, player, services);
    }

    /// <summary>
    /// Atomically exchange both offers. Runs on the single confirm that latched Completing, so no other
    /// trade action can mutate the offers concurrently. Validates ALL ownership and affordability BEFORE
    /// moving anything (all-or-nothing at the validation boundary); a failure aborts without moving a
    /// single item.
    /// </summary>
    private async Task ExecuteSwapAsync(TradeSession trade, PlayerState confirmer, IServiceProvider services)
    {
        var mySession = confirmer.SessionId;
        var partnerSession = trade.Other(mySession);
        var partner = PlayerManager.GetPlayer(partnerSession);

        var mySess = Broadcast.GetSession(mySession);
        var partnerSess = Broadcast.GetSession(partnerSession);

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var characters = services.GetRequiredService<ICharacterRepository>();

        var myOffer = trade.OfferFor(mySession);
        var partnerOffer = trade.OfferFor(partnerSession);
        long myGold = Math.Max(0, myOffer.Gold);
        long partnerGold = Math.Max(0, partnerOffer.Gold);
        var myUids = trade.SnapshotItems(mySession);
        var partnerUids = trade.SnapshotItems(partnerSession);

        // The session is done being negotiated no matter what happens next; drop it so neither side can
        // re-trigger the swap.
        TradeSessionService.Instance.Cancel(mySession);

        // FAILURE ANSWER. A trade that cannot be carried out is closed with 2388 [own handle][0] to each
        // trader (see SendNoBagRoomAsync). It used to be
        // 2389 with result 1, which is never sent: 2389 always carries result 0. No 2438
        // follows, because the only trade 2438 (20020) names a full bag and nothing
        // names these two conditions. Picking another message id here would be a
        // guess; "Trade was cancelled." (strid 324) is what the client
        // already says for this frame.
        if (partner == null)
        {
            Logger.LogWarning("Trade {A}: partner session {B} is gone; trade closed", confirmer.Name, partnerSession);
            await SendCancelledAsync(mySess, trade.HandleFor(mySession));
            return;
        }

        // SP0-INV: validate -> plan -> escrow -> apply -> notify all run while holding BOTH traders'
        // inventory gates (taken in ascending characterId order so two trades touching the same pair can
        // never deadlock; the gate is non-reentrant, so a trader with itself takes it once). No concurrent
        // session of either trader can mutate an offered stack between the ownership check and the move,
        // which is what let a stack be offered here and sold/mailed elsewhere in the same breath.
        await WithBothGatesAsync(confirmer.CharacterId, partner.CharacterId, async () =>
        {
        // ---- VALIDATE (no mutation yet) ----
        // Stack_Max and Is_Trade come from ItemInfo: the first sizes the merge plan below (the same per-template cap
        // ItemSwapHandler's merge uses), the second re-runs the tradeable gate on the live row.
        var itemData = services.GetRequiredService<GameDataStore>().Items;
        var myItems = await ResolveOwnedAsync(inventory, trade, mySession, myUids, confirmer.CharacterId, itemData, Logger);
        var partnerItems = await ResolveOwnedAsync(inventory, trade, partnerSession, partnerUids, partner.CharacterId, itemData, Logger);

        if (myItems is null || partnerItems is null
            || confirmer.Gold < myGold || partner.Gold < partnerGold)
        {
            Logger.LogWarning("Trade {A}<->{B} aborted: ownership/affordability check failed",
                confirmer.Name, partner.Name);
            await SendCancelledAsync(mySess, trade.HandleFor(mySession));
            await SendCancelledAsync(partnerSess, trade.HandleFor(partnerSession));
            return;
        }

        // ---- PLAN (still no mutation) ----
        // Both placements are computed from the CURRENT inventories, before any move, so the two
        // directions cannot collide.
        var myInv = await inventory.GetByCharacterAsync(confirmer.CharacterId);
        var partnerInv = await inventory.GetByCharacterAsync(partner.CharacterId);

        // Each side's OWN offered uids are passed as "leaving" so the other direction never merges into a
        // stack that is itself being handed over (or, for a partial offer, shrunk) in this same swap.
        var toPartner = PlanPlacement(myItems, partnerInv, partnerUids, itemData);   // my items -> partner
        var toMe = PlanPlacement(partnerItems, myInv, myUids, itemData);             // partner items -> me

        // NO ROOM: refuse the whole trade and move nothing. When a recipient's bag cannot take the goods, after the
        // usual 2388 [confirmer, 1] (already sent above), each trader gets 2388 [its OWN handle, 0] and
        // then 2438 OnCommonError [20020 no free bag cell, mode 0], and no 2389 follows.
        // Both traders get it, paired by the confirmer's handle. The result-0 2388 is what closes the window (the
        // client shuts it on a byte != 1), and the session was already dropped above, so nothing is left to cancel.
        if (toPartner is null || toMe is null)
        {
            Logger.LogInformation("Trade {A}<->{B} refused: no free bag cell for {Who}", confirmer.Name, partner.Name,
                toPartner is null && toMe is null ? "either side" : toPartner is null ? partner.Name : confirmer.Name);
            await SendNoBagRoomAsync(mySess, trade.HandleFor(mySession));
            await SendNoBagRoomAsync(partnerSess, trade.HandleFor(partnerSession));
            return;
        }

        // ---- ESCROW THE ZENY (atomic, before anything moves) ----
        // Each side's offer is taken from its wallet here, with no clamp: a wallet that spent the funds since the
        // validation above refuses the trade instead of paying out of nothing. Both debits succeed or neither does.
        if (!confirmer.TryDebit(myGold))
        {
            Logger.LogWarning("Trade {A}<->{B} aborted: {Payer} can no longer pay {Gold}z", confirmer.Name, partner.Name, confirmer.Name, myGold);
            await SendCancelledAsync(mySess, trade.HandleFor(mySession));
            await SendCancelledAsync(partnerSess, trade.HandleFor(partnerSession));
            return;
        }
        if (!partner.TryDebit(partnerGold))
        {
            confirmer.Credit(myGold);
            Logger.LogWarning("Trade {A}<->{B} aborted: {Payer} can no longer pay {Gold}z", confirmer.Name, partner.Name, partner.Name, partnerGold);
            await SendCancelledAsync(mySess, trade.HandleFor(mySession));
            await SendCancelledAsync(partnerSess, trade.HandleFor(partnerSession));
            return;
        }

        // ---- APPLY (move, not copy) ----
        // ALL item moves first, each one atomic and owner-checked; the zeny changes hands only after every item has
        // (it is already escrowed above, so nothing is spendable meanwhile). A throw, or a move the giver's row no
        // longer allows, ends the swap here (E-H3): the escrow goes back to its owners and both windows are closed.
        // Items are not rolled back, so a failure after the first move leaves a bounded loss for one side, never a
        // copy; that case is logged for reconciliation with the trade id, the uids not yet moved and the zeny.
        var progress = new SwapProgress();
        try
        {
            await ApplyPlacementAsync(inventory, toPartner, partner.CharacterId, confirmer.CharacterId, progress);
            await ApplyPlacementAsync(inventory, toMe, confirmer.CharacterId, partner.CharacterId, progress);
        }
        catch (Exception ex)
        {
            confirmer.Credit(myGold);
            partner.Credit(partnerGold);
            if (progress.Writes == 0)
                Logger.LogWarning(ex, "Trade {TradeId} {A}<->{B} aborted before any item moved; zeny returned",
                    trade.Id, confirmer.Name, partner.Name);
            else
                Logger.LogError(ex,
                    "Trade {TradeId} {A}<->{B} failed MID-SWAP after {Writes} writes: not yet moved uids [{Remaining}], "
                    + "{A} offered {AGold}z and {B} offered {BGold}z (both returned to their owners); items already moved stay moved",
                    trade.Id, confirmer.Name, partner.Name, progress.Writes,
                    string.Join(",", myUids.Concat(partnerUids).Where(u => !progress.Settled.Contains(u)).Select(u => u.ToString("X"))),
                    confirmer.Name, myGold, partner.Name, partnerGold);
            await SendCancelledAsync(mySess, trade.HandleFor(mySession));
            await SendCancelledAsync(partnerSess, trade.HandleFor(partnerSession));
            return;
        }

        // Wallets: each side loses what it offered and gains what the other offered. The live wallet is the truth
        // (its setter queues the write-behind flush), so a failed row write is logged and the trade still completes.
        confirmer.Credit(partnerGold);
        partner.Credit(myGold);
        foreach (var payer in new[] { confirmer, partner })
        {
            try { await characters.UpdateGoldAsync(payer.CharacterId, payer.Gold); }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Trade {TradeId}: items moved and zeny credited in memory, but the wallet row of {Name} "
                    + "was not written ({Gold}z); the write-behind flush persists it", trade.Id, payer.Name, payer.Gold);
            }
        }

        Logger.LogInformation(
            "Trade complete: {A} gave {AItems} items + {AGold}z, {B} gave {BItems} items + {BGold}z",
            confirmer.Name, myItems.Count, myGold, partner.Name, partnerItems.Count, partnerGold);

        // ---- NOTIFY ---- each side receives 2389 carrying its OWN handle, its post-trade wallet and its
        // WHOLE post-trade inventory — not the partner's items and gold, which is what this used to send.
        //  - blob+6 and blob+14 are equal and nonzero, even for a trade that moved 0 gold, so neither is the
        //    traded amount. It is the wallet balance.
        //  - the record list is every item the character owns, worn gear (bag 0) through the bank (bag 5),
        //    ordered by (bag, slot). Offered uids are ABSENT from the giver's list. On the receiver's side a
        //    received item keeps its offered uid and count, EXCEPT a stack of a template the receiver already
        //    holds in the bag with room: that one is merged into the held stack (see PlanPlacement), which is
        //    why its uid is missing from the list.
        // The client takes the 8-byte money field and hands every record to its bag-slot updater, so a
        // received-only list leaves the rest of the bag to whatever the client does with a partial one.
        // Held-item Khara rows move for every template that changed hands, on BOTH traders (one lost it, the
        // other gained it), with their 2577s ahead of each trader's 2389 (the sequence is 2388 -> 2577 x n -> ...,
        // and the next item frame is the 2389 carrying that item). A trader whose session is gone is caught up
        // at its next evaluation, which recounts the bag.
        var movedTemplates = myItems.Select(m => m.Item.ItemId)
            .Concat(partnerItems.Select(m => m.Item.ItemId)).Distinct().ToList();
        if (mySess is not null)
            foreach (var templateId in movedTemplates)
                await KharaService.OnHeldItemChangedAsync(mySess, confirmer, templateId, services, Logger);
        if (partnerSess is not null)
            foreach (var templateId in movedTemplates)
                await KharaService.OnHeldItemChangedAsync(partnerSess, partner, templateId, services, Logger);

        var myList = await FullInventoryRecordsAsync(inventory, confirmer.CharacterId);
        var partnerList = await FullInventoryRecordsAsync(inventory, partner.CharacterId);

        // No 2435 follows: the 2389 already carries the post-trade wallet.
        await SendResult(mySess, trade.HandleFor(mySession), (ulong)confirmer.Gold, myList);
        await SendResult(partnerSess, trade.HandleFor(partnerSession), (ulong)partner.Gold, partnerList);

        // 2588 AnsRegistCardBook for each card a trader RECEIVED and has not registered (an MVP card always),
        // after that trader's 2389: it sits right behind a trade's 2389, and the card in it is one the partner
        // offered (its 2384), never one the receiver already held. A trader whose session is gone gets
        // its new cards registered at its next 1442 instead (CardRegistryStore.Merge).
        if (mySess is not null)
            await CardRegistrationNotifier.NotifyArrivedAsync(mySess, confirmer.CharacterId,
                partnerItems.Select(m => m.Item.ItemId), services, Logger);
        if (partnerSess is not null)
            await CardRegistrationNotifier.NotifyArrivedAsync(partnerSess, partner.CharacterId,
                myItems.Select(m => m.Item.ItemId), services, Logger);
        });
    }

    /// <summary>
    /// Run <paramref name="body"/> while holding both traders' inventory gates, lowest characterId first so
    /// two trades over the same pair acquire them in the same order and cannot deadlock. A trader whose two
    /// sessions somehow trade with each other (equal ids) takes the single gate once — it is not re-entrant.
    /// </summary>
    private static Task WithBothGatesAsync(uint c1, uint c2, Func<Task> body)
    {
        uint a = Math.Min(c1, c2), b = Math.Max(c1, c2);
        if (a == b)
            return CharacterInventoryGate.RunAsync(a, async () => { await body(); return 0; });
        return CharacterInventoryGate.RunAsync(a, () =>
            CharacterInventoryGate.RunAsync(b, async () => { await body(); return 0; }));
    }

    /// <summary>
    /// Resolve each offered uid to a live inventory item that STILL belongs to <paramref name="ownerCharId"/>
    /// and sits in the bag (StorageType 0), paired with the quantity the window showed for it (the 1353
    /// count, or the whole stack when none was recorded). Returns null if ANY item fails the check — that
    /// aborts the whole trade, so a stale/foreign/moved item can never be transferred. A stack that now
    /// holds FEWER units than were shown also aborts: the partner agreed to the shown quantity, and
    /// silently handing over less is the swindle the lock/confirm round exists to prevent.
    /// </summary>
    /// <remarks>
    /// E-H4. A uid names a row, and a freed uid is reused, so the row under an offered uid can have become a different
    /// template or a re-refined / re-socketed / re-dyed instance since the partner looked at it. The row is therefore
    /// also compared, field by field, to the <see cref="TradeOfferSnapshot"/> taken when it was accepted into the
    /// window, and the tradeable gate is run again on the live row (it may have become bound). Any difference aborts
    /// the whole trade.
    /// </remarks>
    private static async Task<List<(InventoryItem Item, int Qty)>?> ResolveOwnedAsync(
        IInventoryRepository inventory, TradeSession trade, uint ownerSession, ulong[] uids, uint ownerCharId,
        IReadOnlyDictionary<uint, ItemData> itemData, ILogger logger)
    {
        var result = new List<(InventoryItem Item, int Qty)>(uids.Length);
        foreach (var uid in uids)
        {
            var item = await inventory.GetByIdAsync(uid);
            if (item is null || item.CharacterId != ownerCharId || item.StorageType != 0)
                return null;
            int stack = Math.Max(1, item.Count);
            int qty = TradeOfferQuantities.Get(trade, uid) ?? stack;
            if (qty < 1 || qty > stack)
                return null;
            if (trade.SnapshotOf(ownerSession, uid) is { } offered && offered != TradeOfferSnapshot.Of(item, qty))
            {
                logger.LogWarning("Trade {TradeId}: offered uid {Uid} is no longer what was offered ({Offered} -> {Now})",
                    trade.Id, uid, offered, TradeOfferSnapshot.Of(item, qty));
                return null;
            }
            if (itemData.TryGetValue(item.ItemId, out var template) && TradeGate.RefusedAsNotTradeable(template, item))
            {
                logger.LogWarning("Trade {TradeId}: offered uid {Uid} (item {ItemId}) is untradeable or bound now",
                    trade.Id, uid, item.ItemId);
                return null;
            }
            result.Add((item, qty));
        }
        return result;
    }

    /// <summary>
    /// Where each offered item would go in the recipient's bag, computed without writing anything: the
    /// stack merges first, then a free cell for whatever does not merge. Returns null when some item
    /// needs a cell and none is left, which refuses the whole trade.
    /// <see cref="ApplyPlacementAsync"/> then executes exactly this plan, so the room check and the move
    /// cannot disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CELLS. New cells come from <see cref="BagCapacity.FreeCells"/>: the main bag (flat 0..19), then
    /// each equipped bag up to its ItemInfo <c>Bag_Size</c>, lowest first. The old search walked up from
    /// flat 0 with no upper bound, so a full bag grew past its real cells into bags the player does not
    /// have.
    /// </para>
    /// <para>
    /// A cell held by one of the recipient's OWN offered items is not counted as free, although that
    /// item leaves in the same swap. This matches the move below, where the two directions are applied
    /// one after the other and each takes cells from its recipient's pre-trade bag.
    /// </para>
    /// <para>
    /// A whole stack is a MOVE (ReplaceOne on the same _id): the item keeps its uid, so it can never be
    /// duplicated or lost. A partial offer splits the stack the way ItemSwapHandler's split does: the giver
    /// keeps the uid with the remainder, the receiver gets a fresh uid carrying the offered count.
    /// </para>
    /// <para>
    /// STACK MERGE. A stackable item (ItemInfo <c>Stack_Max</c> &gt; 1) is first poured into the
    /// recipient's bag stacks of the same template, each up to <c>Stack_Max</c>, and only what does not
    /// fit takes a cell of its own. An item received into a bag that already holds a stack of its template
    /// with room merges into that stack and so does not keep its offered uid. A template the receiver holds
    /// only as WORN copies does not merge (worn copies are not bag stacks). How an overflow past Stack_Max is
    /// placed is not specified by the protocol; it goes to a new cell rather than being dropped, so the
    /// receiver always gets the full count.
    /// </para>
    /// <para>
    /// <paramref name="leavingUids"/> are the recipient's own offered uids. They are never merge targets:
    /// the other direction of this swap moves or shrinks them through a separately loaded instance, and
    /// writing a merged count into one would either hand the merged units back or be overwritten.
    /// </para>
    /// </remarks>
    private static TradePlacement? PlanPlacement(
        List<(InventoryItem Item, int Qty)> items, IReadOnlyList<InventoryItem> recipientInventory,
        IReadOnlyCollection<ulong> leavingUids, IReadOnlyDictionary<uint, ItemData> itemData)
    {
        var plan = new TradePlacement();
        var freeCells = BagCapacity.FreeCells(recipientInventory, itemData);
        int nextFree = 0;

        // Bag stacks the incoming items may merge into, in cell order, with their PLANNED counts (the
        // instances are not touched until ApplyPlacementAsync). Cells planned below join the list, so a
        // second offered stack of the same template fills the first before opening another cell.
        var stacks = recipientInventory
            .Where(i => i.StorageType == 0 && i.Slot >= 0 && !leavingUids.Contains(i.Id))
            .OrderBy(i => i.Slot)
            .Select(i => new MergeTarget(i.ItemId, i.Count) { Held = i })
            .ToList();

        foreach (var (it, qty) in items)
        {
            int max = itemData.TryGetValue(it.ItemId, out var data) ? data.Stack_Max : 0;
            int remaining = qty;
            if (max > 1)
            {
                foreach (var target in stacks)
                {
                    if (remaining == 0) break;
                    if (target.ItemId != it.ItemId || target.Count >= max) continue;
                    int add = Math.Min(max - target.Count, remaining);
                    target.Count += add;
                    remaining -= add;
                    if (target.Held != null) plan.HeldCounts[target.Held] = target.Count;
                    else target.Created!.CellCount = target.Count;
                }
            }

            var move = new PlannedMove(it, qty);
            if (remaining > 0)
            {
                if (nextFree >= freeCells.Count)
                    return null; // this item needs a cell and the bag has none left
                move.Cell = freeCells[nextFree++];
                move.CellCount = remaining;
                stacks.Add(new MergeTarget(it.ItemId, remaining) { Created = move });
            }
            plan.Moves.Add(move);
        }
        return plan;
    }

    /// <summary>
    /// Writes a <see cref="PlanPlacement"/> result: each offered item moved, split into its planned cell, or (fully
    /// merged) removed or shrunk, then the merged counts of the recipient's held stacks. Items belong to
    /// <paramref name="fromCharId"/> and move to <paramref name="toCharId"/>. A write the giver's row refuses throws,
    /// so the caller can end the swap; every successful write is counted in <paramref name="progress"/>.
    /// </summary>
    /// <remarks>
    /// SP0-INV. Every write is atomic and owner-checked, so it can neither revert a concurrent change nor
    /// touch a row that is no longer the expected owner's (both gates are held, so under normal operation no
    /// such race exists; this is the cross-process backstop and the proof the move cannot dupe or lose):
    /// <list type="bullet">
    /// <item>a whole stack changing owner (E-H3) is ONE guarded write, <see cref="IInventoryRepository.TryReassignAsync"/>:
    /// the row keeps its uid and is the giver's or the receiver's, never both and never neither;</item>
    /// <item>a giver's partial offer shrinks by a guarded <c>$inc</c>, a fully-merged whole stack is an owner-filtered
    /// delete;</item>
    /// <item>every giver-side write of the batch comes BEFORE any receiver-side increment (a held stack growing by
    /// <see cref="IInventoryRepository.AdjustCountAsync"/>), so a failure part-way loses units, it never copies them.</item>
    /// </list>
    /// RESIDUAL (bounded, loss-not-dupe). Two paths are still two writes: the partial-split (shrink the giver's stack,
    /// then insert the split-off row) and a whole stack whose planned count differs from its row (a partial merge into
    /// the receiver's stacks, or later offers merged into its cell): remove, then insert under the same uid. A throw
    /// between the two writes destroys that item's units; it cannot create any.
    /// </remarks>
    private static async Task ApplyPlacementAsync(IInventoryRepository inventory, TradePlacement plan,
        uint toCharId, uint fromCharId, SwapProgress progress)
    {
        foreach (var move in plan.Moves)
        {
            var it = move.Item;
            bool wholeStack = move.Qty >= it.Count;
            if (move.Cell < 0)
            {
                // Everything merged. The offered uid disappears when the whole stack went;
                // on a partial offer the giver just keeps the rest under its uid.
                bool taken = wholeStack
                    ? await inventory.RemoveItemAsync(it.Id, fromCharId)
                    : await inventory.AdjustCountAsync(it.Id, fromCharId, -move.Qty) is not null;
                if (!taken) throw GiverNoLongerHolds(it);
                progress.Settle(it.Id);
                continue;
            }

            if (!wholeStack)
            {
                // Shrink the giver's stack by the offered amount (guarded $inc), then create the split-off
                // part as a new row for the receiver. The new row takes the giver's per-instance state through
                // the one copier of it: listing Refine/Durability/CardsJson here dropped a bestowed element
                // (record+68). It takes a fresh uid (Id 0), so the two directions can never collide on one uid.
                if (await inventory.AdjustCountAsync(it.Id, fromCharId, -move.Qty) is null)
                    throw GiverNoLongerHolds(it);   // the giver's stack is gone (cannot happen under the gates)
                progress.Settle(it.Id);
                await inventory.AddItemAsync(new InventoryItem
                {
                    CharacterId = toCharId,
                    ItemId = it.ItemId,
                    Count = move.CellCount,
                    Slot = move.Cell,
                    StorageType = 0,
                }.CopyInstanceStateFrom(it));
                progress.Wrote();
            }
            else if (move.CellCount == it.Count)
            {
                // Owner change, the common case: the whole row moves to its planned cell in one guarded write, so it
                // is never in two bags and never in none (E-H3).
                if (!await inventory.TryReassignAsync(it.Id, fromCharId, toCharId, 0, move.Cell))
                    throw GiverNoLongerHolds(it);
                progress.Settle(it.Id);
            }
            else
            {
                // Owner change of a row whose planned count differs from its stored one: take it off the giver FIRST
                // (owner-filtered), then insert it for the receiver under the SAME uid with the planned count. See the
                // RESIDUAL note above.
                if (!await inventory.RemoveItemAsync(it.Id, fromCharId))
                    throw GiverNoLongerHolds(it);
                progress.Settle(it.Id);
                await inventory.AddItemAsync(new InventoryItem
                {
                    Id = it.Id,
                    CharacterId = toCharId,
                    ItemId = it.ItemId,
                    Count = move.CellCount,
                    Slot = move.Cell,
                    StorageType = 0,
                }.CopyInstanceStateFrom(it));
                progress.Wrote();
            }
        }

        foreach (var (held, count) in plan.HeldCounts)
        {
            // The recipient's own held stack grows by the merged-in units: an atomic $inc, not a stale
            // whole-row write that could clobber a concurrent change to this stack. It comes after every giver-side
            // write above, so units are never in both places.
            var merged = await inventory.AdjustCountAsync(held.Id, toCharId, count - held.Count);
            held.Count = merged ?? count;
            progress.Wrote();
        }
    }

    private static InvalidOperationException GiverNoLongerHolds(InventoryItem item) =>
        new($"Trade move of uid {item.Id:X} refused: the giver's row is gone or no longer theirs");

    /// <summary>How far a swap's item phase got: how many writes landed and which offered uids were taken off
    /// their giver (the rest are what a mid-swap failure still has to reconcile).</summary>
    private sealed class SwapProgress
    {
        public int Writes { get; private set; }
        public HashSet<ulong> Settled { get; } = [];

        public void Wrote() => Writes++;

        public void Settle(ulong uid)
        {
            Settled.Add(uid);
            Writes++;
        }
    }

    /// <summary>One side's planned placement: merged counts for held stacks, and one move per offered item.</summary>
    private sealed class TradePlacement
    {
        public Dictionary<InventoryItem, int> HeldCounts { get; } = new(ReferenceEqualityComparer.Instance);
        public List<PlannedMove> Moves { get; } = new();
    }

    /// <summary>An offered item and its destination. <see cref="Cell"/> -1 means it merged completely.
    /// <see cref="CellCount"/> is the count that ends in the cell, including later offers merged into it.</summary>
    private sealed class PlannedMove(InventoryItem item, int qty)
    {
        public InventoryItem Item { get; } = item;
        public int Qty { get; } = qty;
        public short Cell { get; set; } = -1;
        public int CellCount { get; set; }
    }

    /// <summary>A stack an incoming item may merge into: a held one, or a cell planned for an earlier offer.</summary>
    private sealed class MergeTarget(uint itemId, int count)
    {
        public uint ItemId { get; } = itemId;
        public int Count { get; set; } = count;
        public InventoryItem? Held { get; init; }
        public PlannedMove? Created { get; init; }
    }

    /// <summary>
    /// Every item <paramref name="characterId"/> owns, as 126-byte records ordered by (bag, slot) — the
    /// record's own bytes +15/+16 — which is the order the 2389 lists use.
    /// </summary>
    private static async Task<List<byte[]>> FullInventoryRecordsAsync(IInventoryRepository inventory, uint characterId)
    {
        var all = await inventory.GetByCharacterAsync(characterId);
        return all.Select(WorldEntryService.BuildItemRecord126)
                  .OrderBy(r => r[15]).ThenBy(r => r[16])
                  .ToList();
    }

    /// <summary>The completed trade to one trader: 2389 result 0 with its own handle, wallet and whole inventory.</summary>
    private static async Task SendResult(
        ProudSession? sess, uint pcHandle, ulong money, IReadOnlyList<byte[]> items)
    {
        if (sess == null) return;
        await sess.SendRmiAsync(ZoneS2COpcodes.OnConfirmTradePCNotify,
            TradeNotifyNotifyPackets.BuildOnConfirmTradePCNotify(0, pcHandle, money, items));
    }

    /// <summary>
    /// Close one trader's window: 2388 OnConfirmTradePC with the RECEIVER's own handle and result 0.
    /// The client shows strid 324 "Trade was cancelled." and toggles the window off for any
    /// result byte != 1.
    /// </summary>
    private static async Task SendCancelledAsync(ProudSession? sess, uint ownHandle)
    {
        if (sess == null) return;
        await sess.SendRmiAsync(ZoneS2COpcodes.OnConfirmTradePC, TradePackets.BuildOnConfirmTradePC(ownHandle, 0));
    }

    /// <summary>
    /// The full-bag refusal to one trader: 2388 OnConfirmTradePC carrying the RECEIVER's own handle
    /// and result 0, which closes its trade window, then 2438 OnCommonError with system message 20020
    /// (no free bag cell) in mode 0, the mode every trade refusal uses.
    /// </summary>
    private static async Task SendNoBagRoomAsync(ProudSession? sess, uint ownHandle)
    {
        if (sess == null) return;
        await SendCancelledAsync(sess, ownHandle);
        await sess.SendRmiAsync(ZoneS2COpcodes.OnCommonError,
            CommonErrorPackets.BuildCommonError2438(CommonErrorPackets.NoFreeBagCell, CommonErrorPackets.ModeTrade));
    }
}
