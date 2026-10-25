using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Application;
using RO2.Economy.Application;
using RO2.Economy.Domain;
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

    private static readonly TradeApplicationService Trading = new();

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
    /// single item. The flow itself (zeny escrow, the two-sided item exchange under both inventory gates, the
    /// compensation, the zeny hand-over) is <see cref="TradeApplicationService"/>; this method supplies the wallets and
    /// ports and builds and sends every frame from the result.
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

        var itemData = services.GetRequiredService<GameDataStore>().Items;
        var result = await Trading.ExecuteSwapAsync(
            new TradeSwapCommand(trade.Id, confirmer.Name, partner.Name, myGold, partnerGold,
                SideOf(trade, mySession, confirmer.CharacterId, myUids),
                SideOf(trade, partnerSession, partner.CharacterId, partnerUids)),
            confirmer, partner,
            new TradeInventoryAdapter(inventory, itemData, Logger), new TradeLogAdapter(Logger),
            () => characters.UpdateGoldAsync(confirmer.CharacterId, confirmer.Gold),
            () => characters.UpdateGoldAsync(partner.CharacterId, partner.Gold));

        // A trade that could not be carried out closes both windows: validation or affordability failed, or a write
        // threw (the zeny is back with its owners; items already moved stay moved).
        if (result.Kind == TradeResultKind.Cancelled)
        {
            await SendCancelledAsync(mySess, trade.HandleFor(mySession));
            await SendCancelledAsync(partnerSess, trade.HandleFor(partnerSession));
            return;
        }

        // NO ROOM: refuse the whole trade and move nothing. When a recipient's bag cannot take the goods, after the
        // usual 2388 [confirmer, 1] (already sent above), each trader gets 2388 [its OWN handle, 0] and
        // then 2438 OnCommonError [20020 no free bag cell, mode 0], and no 2389 follows.
        // Both traders get it, paired by the confirmer's handle. The result-0 2388 is what closes the window (the
        // client shuts it on a byte != 1), and the session was already dropped above, so nothing is left to cancel.
        if (result.Kind == TradeResultKind.NoRoom)
        {
            await SendNoBagRoomAsync(mySess, trade.HandleFor(mySession));
            await SendNoBagRoomAsync(partnerSess, trade.HandleFor(partnerSession));
            return;
        }

        // ---- NOTIFY ---- each side receives 2389 carrying its OWN handle, its post-trade wallet and its
        // WHOLE post-trade inventory — not the partner's items and gold, which is what this used to send.
        //  - blob+6 and blob+14 are equal and nonzero, even for a trade that moved 0 gold, so neither is the
        //    traded amount. It is the wallet balance.
        //  - the record list is every item the character owns, worn gear (bag 0) through the bank (bag 5),
        //    ordered by (bag, slot). Offered uids are ABSENT from the giver's list. On the receiver's side a
        //    received item keeps its offered uid and count, EXCEPT a stack of a template the receiver already
        //    holds in the bag with room: that one is merged into the held stack (see TradePlanner), which is
        //    why its uid is missing from the list.
        // The client takes the 8-byte money field and hands every record to its bag-slot updater, so a
        // received-only list leaves the rest of the bag to whatever the client does with a partial one.
        // Held-item Khara rows move for every template that changed hands, on BOTH traders (one lost it, the
        // other gained it), with their 2577s ahead of each trader's 2389 (the sequence is 2388 -> 2577 x n -> ...,
        // and the next item frame is the 2389 carrying that item). A trader whose session is gone is caught up
        // at its next evaluation, which recounts the bag.
        if (mySess is not null)
            foreach (var templateId in result.MovedTemplates)
                await KharaService.OnHeldItemChangedAsync(mySess, confirmer, templateId, services, Logger);
        if (partnerSess is not null)
            foreach (var templateId in result.MovedTemplates)
                await KharaService.OnHeldItemChangedAsync(partnerSess, partner, templateId, services, Logger);

        var myList = await FullInventoryRecordsAsync(inventory, confirmer.CharacterId);
        var partnerList = await FullInventoryRecordsAsync(inventory, partner.CharacterId);

        // No 2435 follows: the 2389 already carries the post-trade wallet.
        await SendResult(mySess, trade.HandleFor(mySession), (ulong)result.GoldA, myList);
        await SendResult(partnerSess, trade.HandleFor(partnerSession), (ulong)result.GoldB, partnerList);

        // 2588 AnsRegistCardBook for each card a trader RECEIVED and has not registered (an MVP card always),
        // after that trader's 2389: it sits right behind a trade's 2389, and the card in it is one the partner
        // offered (its 2384), never one the receiver already held. A trader whose session is gone gets
        // its new cards registered at its next 1442 instead (CardRegistryStore.Merge).
        if (mySess is not null)
            await CardRegistrationNotifier.NotifyArrivedAsync(mySess, confirmer.CharacterId,
                result.ReceivedByA, services, Logger);
        if (partnerSess is not null)
            await CardRegistrationNotifier.NotifyArrivedAsync(partnerSess, partner.CharacterId,
                result.ReceivedByB, services, Logger);
    }

    /// <summary>One trader's offer for the economy application: its character and every uid in its window, with the
    /// quantity and the snapshot the window recorded for it.</summary>
    private static TradeSwapSide SideOf(TradeSession trade, uint session, uint characterId, ulong[] uids)
        => new(characterId, uids.Select(uid =>
            new TradeOfferedItem(uid, TradeOfferQuantities.Get(trade, uid), trade.SnapshotOf(session, uid))).ToList());

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
