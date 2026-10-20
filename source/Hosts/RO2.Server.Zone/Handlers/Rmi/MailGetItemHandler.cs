using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1396 ReqMailGetItem - Move the attached item into the reader's inventory (2447).</summary>
public class MailGetItemHandler : ZoneRmiHandlerBase<MailGetItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMailGetItem;
    public override string Name => "MailGetItem";

    /// <summary>2447 result 0 (success), and the "no failure code" value while a claim is being checked.</summary>
    private const ushort ResultOk = 0;

    /// <summary>Target cell meaning "no free bag cell found / none needed" (the item joins a stack, or the claim is refused).</summary>
    private const short NoCell = -1;

    /// <summary>A mail whose AttachedItemId is 0 carries no attachment.</summary>
    private const uint NoAttachedItem = 0;

    /// <summary>Stack_Max assumed for an attached item that has no ItemInfo row: it never stacks.</summary>
    private const int DefaultStackMax = 1;

    public MailGetItemHandler(IServiceScopeFactory s, ILogger<MailGetItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!MailPackets.TryParseMailRef(payload.Span, out var owner, out var mailUid))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqMailGetItem", player.Name);
            return;
        }

        var mail = MailService.From(services, PlayerManager, Broadcast, Logger);
        var inv = services.GetService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // Decide where the item goes BEFORE ClaimItemAsync: it marks the mail claimed as soon as it has
        // placed the item, so a claim that cannot be placed must never reach it. Failures answer in the
        // only failure shape (result code, echoed mail uid, zero record, disposition 0). A result 0 with a zero
        // record would send the client down its success branch for a claim that moved nothing.
        //
        // Where it goes (see MailService.ClaimItemAsync): onto a bag stack of the same item
        // the mail's count still fits in (ItemInfo Stack_Max), else into the first usable free cell under
        // the item's own uid. Only when neither exists is the answer 16207 "no space": a full bag with a
        // joinable stack still takes the item, while a full bag of full stacks has nothing to join.
        var existing = await mail.GetAsync(player.CharacterId, mailUid);
        ushort failure = ResultOk;
        short cell = NoCell;
        InventoryItem? joinStack = null;
        if (existing == null || existing.AttachedItemId == NoAttachedItem || existing.ItemClaimed)
        {
            failure = MailPackets.GetItemInvalidMail;   // nothing left to claim (every re-claim)
        }
        else if (inv == null)
        {
            failure = MailPackets.GetItemSystemError;
        }
        else
        {
            var bag = await inv.GetByCharacterAsync(player.CharacterId);
            int stackMax = store.Items.TryGetValue(existing.AttachedItemId, out var template) ? template.Stack_Max : DefaultStackMax;
            joinStack = MailService.FindJoinStack(bag, existing, stackMax);
            if (joinStack == null)
            {
                // The lowest cell the player can actually use (BagCapacity: the main bag, then each equipped
                // bag's Bag_Size cells), or -1 when every one is taken. Left to itself AddItemAsync takes the
                // lowest free flat slot with no upper bound, which can be a cell of a bag the player does not
                // have (WriteItemInstance clamps past bag 4 onto an occupied cell).
                var freeCells = BagCapacity.FreeCells(bag, store.Items);
                cell = freeCells.Count > 0 ? freeCells[0] : NoCell;
                if (cell < 0)
                    failure = MailPackets.GetItemNoSpace;
            }
        }

        if (failure != ResultOk)
        {
            Logger.LogInformation("{Name} claims mail item {Uid:X}: refused with {Result}", player.Name, mailUid, failure);
            await session.SendRmiAsync(ZoneS2COpcodes.OnMailGetItem,
                MailPackets.BuildMailGetItemFailed2447(player.EntityId, failure, mailUid));
            return;
        }

        MailService.ItemClaim claim;
        try
        {
            claim = await mail.ClaimItemAsync(player.CharacterId, mailUid, inv!, joinStack, cell);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ClaimItemAsync has undone its flag flip and placement: the mail is still claimable, nothing was granted.
            Logger.LogError(ex, "{Name} claims mail item {Uid:X}: placement failed", player.Name, mailUid);
            await session.SendRmiAsync(ZoneS2COpcodes.OnMailGetItem,
                MailPackets.BuildMailGetItemFailed2447(player.EntityId, MailPackets.GetItemSystemError, mailUid));
            return;
        }
        if (claim.Placed == null)
        {
            // Claimed in between (a double click sends two 1396s).
            Logger.LogInformation("{Name} claims mail item {Uid:X}: already claimed", player.Name, mailUid);
            await session.SendRmiAsync(ZoneS2COpcodes.OnMailGetItem,
                MailPackets.BuildMailGetItemFailed2447(player.EntityId, MailPackets.GetItemInvalidMail, mailUid));
            return;
        }

        // Until the next map load a re-read of this mail still shows the item, uid zeroed (MailReadHandler).
        MailService.NoteClaimedThisEntry(player, mailUid);

        // 2447 OnMailGetItem — compact-BA (msgTag 0xE358), blob 145 / body 148. The client adds
        // blob+18 to the bag at (container rec+15, slot rec+16) whenever its uid is non-zero, and a stack
        // count of 0 there clears the cell instead. The item is sent as it now sits in the bag: its own
        // uid in its new cell, or the stack it joined with that stack's uid, cell and new count,
        // which is exactly claim.Placed. The record goes
        // through ItemWorkbench with the store this handler holds so the claimed item keeps its opened rune
        // holes (the claim restores CardsJson and Element from the mail): a non-empty hole block goes at record+0x21.
        // Disposition: 1 keeps the mail, 2 makes the client delete it. The answer is 2 on every successful
        // claim of an auction mail (kinds 11, 2 and 7) and 1 on player and
        // system mail (kinds 0 and 22). ClaimItemAsync has just removed exactly those
        // spent auction mails from the store, so asking the same predicate of the mail it returned makes the
        // client drop the mail when, and only when, the store did.
        var m = claim.Mail;
        var disposition = m != null && MailService.ClaimRemovesMail(m)
            ? MailPackets.ClaimDeletesMail
            : MailPackets.GetItemKeepMail;
        byte[] item126 = ItemWorkbench.RecordWithRuneholes(claim.Placed, store);

        Logger.LogInformation("{Name} claims mail item {Uid:X} (itemId={ItemId}, bag uid={BagUid}, slot={Slot}, count={Count}, joined={Joined}, disposition={Disposition})",
            player.Name, mailUid, m?.AttachedItemId ?? NoAttachedItem, claim.Placed.Id, claim.Placed.Slot, claim.Placed.Count, claim.Joined, disposition);

        // The item is in the bag now, so its held count changed: a Complete_Type 4 Khara row naming it reports
        // the new count (2577), and any completion that causes (2575 / 2576, chained accepts) goes out too, all
        // BEFORE the claim's 2447, e.g.
        // 2577 598 -> 2575 598 / 593 / 588 -> 2447.
        await KharaService.OnHeldItemChangedAsync(session, player, claim.Placed.ItemId, services, Logger, ct);

        await session.SendRmiAsync(ZoneS2COpcodes.OnMailGetItem,
            MailPackets.BuildMailGetItem2447(player.EntityId, ResultOk, mailUid, item126, disposition));
    }
}
