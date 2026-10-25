using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Models;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Self-contained mail business logic: storage (via <see cref="IMailRepository"/>), attachment transfer
/// (via <see cref="IInventoryRepository"/> / player wallet), and the online "new mail" notify (2449).
///
/// Not registered in DI (to avoid touching Program.cs / clashing with other work): a handler builds one
/// with <see cref="From"/>, passing the scoped services it already holds. All the singletons it needs
/// (<see cref="IMailRepository"/>, <see cref="PlayerStateManager"/>, <see cref="BroadcastService"/>) resolve
/// identically from any scope, and the mail store is a stateless singleton over the Mongo collections.
/// </summary>
public sealed class MailService
{
    /// <summary>InventoryItem.StorageType of a row in the character's bag, as opposed to worn gear or a bank.</summary>
    private const byte StorageBag = 0;

    /// <summary>Sender name stored on a mail whose sender has no name.</summary>
    private const string UnknownSenderName = "Unknown";

    /// <summary>Id given to a refunded row: the repository assigns a fresh uid.</summary>
    private const ulong NewRowId = 0;

    /// <summary>Slot given to a refunded row: no cell, so the repository picks the lowest free one.</summary>
    private const short UnplacedSlot = -1;

    private readonly IMailRepository _repo;
    private readonly ICharacterRepository _characters;
    private readonly PlayerStateManager _players;
    private readonly BroadcastService _broadcast;
    private readonly ILogger _log;

    public MailService(IMailRepository repo, ICharacterRepository characters, PlayerStateManager players, BroadcastService broadcast, ILogger log)
    {
        _repo = repo;
        _characters = characters;
        _players = players;
        _broadcast = broadcast;
        _log = log;
    }

    /// <summary>Builds a <see cref="MailService"/> from a handler's scope + singletons.</summary>
    public static MailService From(IServiceProvider services, PlayerStateManager players, BroadcastService broadcast, ILogger log)
    {
        return new MailService(services.GetRequiredService<IMailRepository>(), services.GetRequiredService<ICharacterRepository>(),
            players, broadcast, log);
    }

    public Task<IReadOnlyList<MailMessage>> GetInboxAsync(uint receiverCharId) => _repo.GetInboxAsync(receiverCharId);

    public Task<long> CountUnreadAsync(uint receiverCharId) => _repo.CountUnreadAsync(receiverCharId);

    public Task<MailMessage?> GetAsync(uint receiverCharId, ulong uid) => _repo.GetByUidAsync(receiverCharId, uid);

    // ---------------------------------------------------------------------------------------------
    // Send
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// What <see cref="SendMailAsync"/> did. <see cref="SendResult.MailboxFull"/>: the recipient already holds
    /// <see cref="MailPackets.MailboxPlayerSendLimit"/> live mails (2445 result
    /// <see cref="MailPackets.SendMailboxFull"/>); nothing was taken from the sender.
    /// </summary>
    public enum SendResult { Sent, RecipientNotFound, InvalidRequest, Error, MailboxFull }

    /// <summary>
    /// One gate per recipient character, held from the mailbox count to the insert of a player send, so
    /// two sends landing together cannot both pass the count at <see cref="MailPackets.MailboxPlayerSendLimit"/>
    /// minus one. Only player sends take it: system and auction mail are not held to the limit.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, SemaphoreSlim> s_recipientGates = new();

    /// <summary>
    /// Stores a mail from <paramref name="sender"/> to the named recipient, transferring any attached bag
    /// item off the sender first (so nothing is duplicated). Notifies the recipient with 2449 if online.
    /// A recipient whose box is full gets nothing and the sender loses nothing
    /// (<see cref="SendResult.MailboxFull"/>).
    /// </summary>
    public async Task<(SendResult result, MailMessage? mail)> SendMailAsync(PlayerState sender, MailPackets.MailSendRequest req, IInventoryRepository senderInventory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.RecipientName)) return (SendResult.InvalidRequest, null);

            var recipient = await ResolveCharacterByNameAsync(req.RecipientName);
            if (recipient == null) return (SendResult.RecipientNotFound, null);

            var gate = s_recipientGates.GetOrAdd(recipient.Id, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                // The mailbox-full refusal comes after the recipient resolves (16204 needs a mailbox to count)
                // and before anything is taken from the sender: a refused send moves nothing and costs nothing
                // (MailPackets.PostageZeny). The count is the whole box, every kind, read or not, but only mails
                // younger than the mail lifetime count (IMailRepository.CountLiveInboxAsync).
                long held = await _repo.CountLiveInboxAsync(recipient.Id,
                    DateTime.UtcNow.AddDays(-MailPackets.MailLifetimeDays));
                if (held >= MailPackets.MailboxPlayerSendLimit)
                {
                    _log.LogInformation("Mail {Sender} -> {Recipient}: mailbox holds {Held} (limit {Limit})",
                        sender.Name, recipient.Name, held, MailPackets.MailboxPlayerSendLimit);
                    return (SendResult.MailboxFull, null);
                }
                return await StoreAsync(sender, req, senderInventory, recipient);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendMailAsync failed for {Sender}", sender.Name);
            return (SendResult.Error, null);
        }
    }

    /// <summary>The store half of <see cref="SendMailAsync"/>, run under the recipient's gate once the box has room.</summary>
    private async Task<(SendResult result, MailMessage? mail)> StoreAsync(PlayerState sender, MailPackets.MailSendRequest req,
        IInventoryRepository senderInventory, Character recipient)
    {
        // Take the attachment off the sender BEFORE the mail exists, so the item can never be in two
        // places at once. If anything downstream fails we refund it.
        //
        // SP0-INV: the read-decide-write of the take runs under the sender's inventory gate, and the write
        // is atomic and owner-checked — a whole-stack attach is an owner-filtered delete, a partial one an
        // $inc guarded by the live count. So a stack a second concurrent session (a trade, a sell, another
        // mail attach) is splitting at the same moment cannot be handed to both: whichever write loses the
        // guard takes nothing. (The gate must NOT wrap the refund below: a refund re-adds an unplaced row,
        // which the repository's AddItemAsync takes the same gate for — a nested take would deadlock.)
        InventoryItem? taken = null;
        int takenCount = 0;
        if (req.ItemUid != 0 && senderInventory != null)
        {
            var take = await CharacterInventoryGate.RunAsync(sender.CharacterId, async () =>
            {
                var item = await senderInventory.GetByIdAsync(req.ItemUid);
                if (item == null || item.CharacterId != sender.CharacterId || item.StorageType != StorageBag)
                    return (Item: (InventoryItem?)null, Count: 0, Raced: false); // not an owned bag item: send without it
                int cnt = req.ItemCount == 0 ? item.Count : (int)Math.Min(req.ItemCount, (uint)item.Count);
                if (cnt < 1) cnt = 1;
                bool ok = cnt >= item.Count
                    ? await senderInventory.RemoveItemAsync(item.Id, sender.CharacterId)
                    : (await senderInventory.AdjustCountAsync(item.Id, sender.CharacterId, -cnt)).HasValue;
                // Raced: the stack moved/shrank under us between the read and the write.
                return ok ? (Item: (InventoryItem?)item, Count: cnt, Raced: false)
                          : (Item: (InventoryItem?)null, Count: 0, Raced: true);
            });
            // The requested attachment was taken by another session between the read and the write: send
            // nothing (and take nothing), rather than mail a copy of an item that is no longer the sender's.
            if (take.Raced) return (SendResult.InvalidRequest, null);
            taken = take.Item;
            takenCount = take.Count;
        }

        var mail = new MailMessage
        {
            ReceiverCharId = recipient.Id,
            ReceiverName = recipient.Name,
            SenderCharId = sender.CharacterId,
            SenderName = string.IsNullOrEmpty(sender.Name) ? UnknownSenderName : sender.Name,
            Title = req.Title ?? string.Empty,
            Body = req.Body ?? string.Empty, // 1393 request: UTF-16 body text at offset +113
            Gold = req.Gold,                 // 1393 request: attached zeny, u64 at offset +92; MailSendHandler
                                             // checks it against the Config_Def 92 cap and the wallet first
            IsRead = false,
            SentAt = DateTime.UtcNow,
        };

        if (taken != null)
        {
            mail.AttachedItemUid = taken.Id;
            mail.AttachedItemId = taken.ItemId;
            mail.AttachedItemCount = takenCount;
            mail.AttachedItemRefine = taken.Refine;
            mail.AttachedItemDurability = taken.Durability;
            mail.AttachedItemCardsJson = taken.CardsJson;
            mail.AttachedItemElement = taken.Element; // else the claim re-creates it neutral
            mail.AttachedItemDyeColor = taken.DyeColor;
            mail.AttachedItemSocketPunchersJson = taken.SocketPunchersJson;
            mail.AttachedItemDyeColor2 = taken.DyeColor2;
            mail.AttachedItemDyeColor3 = taken.DyeColor3;
            mail.AttachedItemFxEnchantIndex = taken.FxEnchantIndex;
            mail.AttachedItemEnchantReinforceId = taken.EnchantReinforceId;
            mail.AttachedItemEnchantSeconds = taken.EnchantSeconds;
            mail.AttachedItemBound = taken.Bound;
            mail.AttachedItemAbilityValue = taken.AbilityValue;
        }

        try
        {
            await _repo.AddAsync(mail);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Mail insert failed; refunding attachment to sender {Sender}", sender.Name);
            await RefundAsync(senderInventory, sender.CharacterId, mail);
            return (SendResult.Error, null);
        }

        await NotifyNewMailAsync(recipient.Id);
        _log.LogInformation("Mail {Uid:X} {Sender} -> {Recipient} (item {ItemId} x{Cnt})",
            mail.Uid, mail.SenderName, mail.ReceiverName, mail.AttachedItemId, mail.AttachedItemCount);
        return (SendResult.Sent, mail);
    }

    private async Task RefundAsync(IInventoryRepository? inv, uint charId, MailMessage mail)
    {
        if (inv == null || mail.AttachedItemId == 0) return;
        try
        {
            // A fresh uid and the lowest free slot.
            await inv.AddItemAsync(AttachmentAsBagRow(mail, charId, NewRowId, UnplacedSlot));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Attachment refund failed for char {Char}", charId);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Read / claim / delete
    // ---------------------------------------------------------------------------------------------

    /// <summary>Marks the mail read and returns it (null if the caller does not own a mail with that uid).</summary>
    public async Task<MailMessage?> ReadMailAsync(uint receiverCharId, ulong uid)
    {
        var mail = await _repo.GetByUidAsync(receiverCharId, uid);
        if (mail == null) return null;
        if (!mail.IsRead)
        {
            mail.IsRead = true;
            await _repo.ReplaceAsync(mail);
        }
        return mail;
    }

    /// <summary>
    /// What <see cref="ClaimItemAsync"/> did. <see cref="Placed"/> is the bag row the item is now in, as it
    /// now stands (null when nothing was claimed: no such mail, no item left on it, or nowhere to put it);
    /// <see cref="Joined"/> is true when that row is an existing stack the item was added to.
    /// </summary>
    public readonly record struct ItemClaim(MailMessage? Mail, InventoryItem? Placed, bool Joined);

    /// <summary>
    /// Moves the attached item into <paramref name="charId"/>'s bag (once) and marks it claimed: onto
    /// <paramref name="joinStack"/> when the caller found one (<see cref="FindJoinStack"/>), else as its own
    /// row in <paramref name="cell"/>, keeping the item's uid. An auction mail whose item this claim took is
    /// then removed from the store (<see cref="ClaimRemovesMail"/>).
    /// </summary>
    /// <remarks>
    /// A claimed item either keeps the uid it had in the mail, or joins a bag stack of the same template. In
    /// the join case the 2447 record carries the existing stack's uid with the mail count added, sits in the
    /// stack's own cell, stays within the ItemInfo Stack_Max, and every other record byte equals the mailed
    /// item's.
    /// The uid the item had can be in use by another row: a sender who mails part of a stack keeps that
    /// uid on the part left behind (<see cref="SendMailAsync"/>), and an auction's split-off part got
    /// <see cref="IInventoryRepository.GetNextIdAsync"/>, which reserves nothing. The row then takes a fresh
    /// uid, as every mail claim did before.
    /// </remarks>
    public async Task<ItemClaim> ClaimItemAsync(uint charId, ulong uid, IInventoryRepository inventory,
        InventoryItem? joinStack, short cell)
    {
        // E-H7, guard first: the claim is the repository's one atomic flip of ItemClaimed (TryClaimItemAsync),
        // taken BEFORE anything is placed, so of two concurrent 1396/1457 for one mail exactly one gets the mail
        // back and places the item; the other gets null and grants nothing. A placement that then fails is
        // undone (the placed item removed, the flag flipped back), so a failed claim leaves the mail claimable
        // and no item minted. The gate additionally serialises the claimer's bag mutation (SP0-INV); the
        // placement into a bag stack is an atomic $inc, not a stale read-modify-write. (AddItemAsync below is
        // only reached with cell >= 0, a placed row, so it never re-enters the gate.)
        return await CharacterInventoryGate.RunAsync(charId, async () =>
        {
            var mail = await _repo.GetByUidAsync(charId, uid);
            if (mail == null) return new ItemClaim(null, null, false);
            if (mail.AttachedItemId == 0 || mail.ItemClaimed || inventory == null) return new ItemClaim(mail, null, false);
            if (joinStack == null && cell < 0) return new ItemClaim(mail, null, false);   // nowhere to put it; the caller answers 16207

            var claimed = await _repo.TryClaimItemAsync(charId, uid);
            if (claimed == null) return new ItemClaim(mail, null, false);   // lost the race: already claimed

            InventoryItem? placed = null;
            InventoryItem? addedRow = null;
            int? countBefore = null;
            try
            {
                if (joinStack != null)
                {
                    countBefore = (await inventory.GetByIdAsync(joinStack.Id))?.Count;
                    var merged = countBefore is null ? null : await inventory.AdjustCountAsync(joinStack.Id, charId, AttachedCount(claimed));
                    if (merged is null)
                    {
                        // the stack vanished; the mail stays claimable
                        await UnclaimItemAsync(charId, uid);
                        return new ItemClaim(mail, null, false);
                    }
                    joinStack.Count = merged.Value;
                    placed = joinStack;
                }
                else
                {
                    ulong keepUid = claimed.AttachedItemUid;
                    if (keepUid != 0 && await inventory.GetByIdAsync(keepUid) != null)
                        keepUid = 0;   // taken: AddItemAsync assigns a fresh uid for Id 0
                    placed = addedRow = AttachmentAsBagRow(claimed, charId, keepUid, cell);
                    try
                    {
                        await inventory.AddItemAsync(placed);
                    }
                    catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && placed.Id != 0)
                    {
                        // Another row took the uid between the check and the insert.
                        placed.Id = 0;
                        await inventory.AddItemAsync(placed);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Mail {Uid:X} item placement failed for char {Char}; undoing the claim", uid, charId);
                await UndoPlacementAsync(inventory, charId, joinStack, countBefore, addedRow, AttachedCount(claimed));
                await UnclaimItemAsync(charId, uid);
                throw;
            }

            // The flag was flipped BEFORE the placement and any removal: if the delete below fails, what is
            // left is a spent mail, never a mail whose item can be claimed a second time.
            await RemoveIfSpentAuctionMailAsync(claimed);
            return new ItemClaim(claimed, placed, joinStack != null);
        });
    }

    /// <summary>
    /// Takes back what a failed claim put in the bag: the row it created, or the count it added to the stack (only
    /// when the stack now stands at exactly its pre-claim count plus the mail's, i.e. the add did land).
    /// </summary>
    private async Task UndoPlacementAsync(IInventoryRepository inventory, uint charId, InventoryItem? joinStack, int? countBefore,
        InventoryItem? addedRow, int count)
    {
        try
        {
            if (addedRow is { Id: not 0 }) await inventory.RemoveItemAsync(addedRow.Id, charId);
            else if (joinStack != null && countBefore is { } before
                     && (await inventory.GetByIdAsync(joinStack.Id))?.Count == before + count)
                await inventory.AdjustCountAsync(joinStack.Id, charId, -count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Undoing the claimed placement of mail item failed for char {Char}", charId);
        }
    }

    /// <summary>
    /// Flips <c>ItemClaimed</c> back so a claim whose placement failed can be retried. The repository exposes no
    /// atomic un-claim, so this is a re-read and write-back; the flag is set, so no second claim can win in
    /// between, and only a concurrent mark-read could interleave.
    /// </summary>
    private async Task UnclaimItemAsync(uint charId, ulong uid)
    {
        try
        {
            var m = await _repo.GetByUidAsync(charId, uid);
            if (m is { ItemClaimed: true })
            {
                m.ItemClaimed = false;
                await _repo.ReplaceAsync(m);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Un-claiming mail {Uid:X} of char {Char} failed; the item stays marked claimed", uid, charId);
        }
    }

    /// <summary>The stack count a mail's item carries (a mail stored with 0 means one).</summary>
    internal static int AttachedCount(MailMessage mail) => mail.AttachedItemCount > 0 ? mail.AttachedItemCount : 1;

    /// <summary>
    /// The mailed item as a bag row (StorageType 0) with uid <paramref name="id"/> in flat cell
    /// <paramref name="slot"/>, carrying the snapshot the mail took of it. The single mapping from the
    /// mail's AttachedItem* fields to an item, used by the claim and by the 2446 attachment record
    /// (<see cref="MailPackets.BuildAttachmentRecord126"/>), so the item a mail shows is the item it gives.
    /// </summary>
    internal static InventoryItem AttachmentAsBagRow(MailMessage mail, uint charId, ulong id, short slot) => new()
    {
        Id = id,
        CharacterId = charId,
        ItemId = mail.AttachedItemId,
        Count = AttachedCount(mail),
        Slot = slot,
        StorageType = StorageBag,
        Refine = mail.AttachedItemRefine,
        Durability = mail.AttachedItemDurability,
        CardsJson = mail.AttachedItemCardsJson,
        Element = mail.AttachedItemElement,
        DyeColor = mail.AttachedItemDyeColor,
        SocketPunchersJson = mail.AttachedItemSocketPunchersJson,
        DyeColor2 = mail.AttachedItemDyeColor2,
        DyeColor3 = mail.AttachedItemDyeColor3,
        FxEnchantIndex = mail.AttachedItemFxEnchantIndex,
        EnchantReinforceId = mail.AttachedItemEnchantReinforceId,
        EnchantSeconds = mail.AttachedItemEnchantSeconds,
        Bound = mail.AttachedItemBound,
        AbilityValue = mail.AttachedItemAbilityValue,
    };

    /// <summary>
    /// The bag stack a claim of <paramref name="mail"/>'s item joins, or null when it needs a cell of its
    /// own: a placed bag row (StorageType 0) of the same template whose count plus the mail's stays within
    /// <paramref name="stackMax"/> (ItemInfo Stack_Max; 1 never stacks), lowest cell first.
    /// </summary>
    /// <remarks>
    /// Follows the join rule of <see cref="ClaimItemAsync"/>: a stack never grows above Stack_Max, and a mail
    /// that does not fit takes a cell of its own, keeping its own uid. The joined stack must also carry the
    /// mailed item's own state (refine, element, dye, rune holes and their punchers): merging two different
    /// instances would lose one's state, so only a stack equal to the mailed item in every field but uid,
    /// count and cell qualifies.
    /// CardBookTakeOff1PcsHandler keeps card grades (Refine) apart for the same reason.
    /// </remarks>
    public static InventoryItem? FindJoinStack(IEnumerable<InventoryItem> bag, MailMessage mail, int stackMax)
    {
        if (mail.AttachedItemId == 0 || stackMax <= 1) return null;
        int count = AttachedCount(mail);
        return bag
            .Where(i => i.StorageType == StorageBag && i.Slot >= 0 && i.ItemId == mail.AttachedItemId
                        && i.Count > 0 && i.Count + count <= stackMax
                        && i.Refine == mail.AttachedItemRefine
                        && i.Element == mail.AttachedItemElement
                        && i.DyeColor == mail.AttachedItemDyeColor
                        && i.DyeColor2 == mail.AttachedItemDyeColor2 && i.DyeColor3 == mail.AttachedItemDyeColor3
                        && i.FxEnchantIndex == mail.AttachedItemFxEnchantIndex
                        && i.EnchantReinforceId == mail.AttachedItemEnchantReinforceId
                        && i.EnchantSeconds == mail.AttachedItemEnchantSeconds
                        && WorldEntryService.RecordAbilityValue(i)
                           == WorldEntryService.RecordAbilityValue(mail.AttachedItemId, mail.AttachedItemAbilityValue)
                        && SameJson(i.CardsJson, mail.AttachedItemCardsJson)
                        && SameJson(i.SocketPunchersJson, mail.AttachedItemSocketPunchersJson))
            .OrderBy(i => i.Slot)
            .FirstOrDefault();
    }

    private static bool SameJson(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? string.IsNullOrWhiteSpace(b) : a == b;

    // ---------------------------------------------------------------------------------------------
    // Claimed-this-entry: what a re-read of a claimed mail shows
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Mails whose item each player claimed since the client's last 1304 AnsMapLoading. Keyed weakly by the
    /// <see cref="PlayerState"/>, so the entries go with it: every 1417 world entry makes a new PlayerState.
    /// Emptied on every other 1304 by <see cref="ForgetClaimsOnMapLoad"/>.
    /// </summary>
    private static readonly ConditionalWeakTable<PlayerState, HashSet<ulong>> s_claimedThisEntry = new();

    /// <summary>
    /// Records that <paramref name="player"/> just claimed the item of mail <paramref name="mailUid"/>, so a
    /// re-read before the next map load still shows it (<see cref="ClaimedThisEntry"/>).
    /// </summary>
    public static void NoteClaimedThisEntry(PlayerState player, ulong mailUid)
    {
        var claimed = s_claimedThisEntry.GetOrCreateValue(player);
        lock (claimed) claimed.Add(mailUid);
    }

    /// <summary>
    /// Whether <paramref name="player"/> claimed the item of mail <paramref name="mailUid"/> since the last
    /// map load, i.e. whether its 2446 still shows the item (<see cref="MailPackets.BuildAttachmentRecord126"/>).
    /// </summary>
    /// <remarks>
    /// After a claim, a re-read of the mail (2446) before the next 1304 AnsMapLoading shows the attachment
    /// record as it was before the claim with only the uid zeroed; once a 1304 has arrived it shows an
    /// all-zero record. So the rule is "a 1304 since the claim", whatever map it loads. A 1417 is a new
    /// PlayerState here, which the weak table does not know.
    /// Every other map load on the connection also ends in the client's 1304, and the 1304 handler
    /// (AnsMapLoadingHandler) calls <see cref="ForgetClaimsOnMapLoad"/> there. The map id cannot stand in
    /// for that call: a reload of the same map (WarpService.WarpAsync to the current map, i.e. a same-map
    /// teleport or a respawn at a save point on that map) and a round trip A -> B -> A both leave
    /// <see cref="PlayerState.MapId"/> as it was at the claim.
    /// </remarks>
    public static bool ClaimedThisEntry(PlayerState player, ulong mailUid)
    {
        if (!s_claimedThisEntry.TryGetValue(player, out var claimed)) return false;
        lock (claimed) return claimed.Contains(mailUid);
    }

    /// <summary>
    /// Forgets every claim noted for <paramref name="player"/>. Called by AnsMapLoadingHandler on every C2S
    /// 1304 AnsMapLoading, once its PlayerState resolves: after a 1304 a re-read of a claimed mail carries an
    /// all-zero record (see <see cref="ClaimedThisEntry"/>). A world-entry 1304 comes before any claim of
    /// that PlayerState, so calling it on every 1304 is exact.
    /// </summary>
    public static void ForgetClaimsOnMapLoad(PlayerState player)
    {
        if (!s_claimedThisEntry.TryGetValue(player, out var claimed)) return;
        lock (claimed) claimed.Clear();
    }

    /// <summary>
    /// Marks the attached zeny claimed (once). Returns the mail and the amount to grant (0 if already
    /// claimed / none). The caller adds it to the live wallet (which auto-persists). An auction proceeds
    /// mail whose zeny this claim took is then removed from the store (<see cref="ClaimRemovesMail"/>).
    /// </summary>
    public async Task<(MailMessage? mail, long granted)> ClaimGoldAsync(uint charId, ulong uid)
    {
        // E-H7: one atomic flip of GoldClaimed (TryClaimGoldAsync) decides the winner, so no gate is needed:
        // of two concurrent 1457 for one mail exactly one gets the mail back and the amount to grant.
        var claimed = await _repo.TryClaimGoldAsync(charId, uid);
        if (claimed == null) return (await _repo.GetByUidAsync(charId, uid), 0);
        await RemoveIfSpentAuctionMailAsync(claimed);
        return (claimed, claimed.Gold);
    }

    /// <summary>
    /// Whether a claim that just took an attachment of <paramref name="mail"/> leaves it removed: an
    /// auction mail (<see cref="AuctionMailKind.IsAuction"/>) with nothing left to claim. This is the
    /// case the claim answers with disposition 2, which makes the client delete the mail (2447 blob+144,
    /// 2617 blob+26). Player mail is answered with 1 and stays in the mailbox; auction mail is removed from
    /// the store, as <see cref="ClaimItemAsync"/> and <see cref="ClaimGoldAsync"/> do. Only call it on the
    /// mail a claim returned after that claim granted something: a "receive all" 1457 on an auction ITEM
    /// mail grants no zeny and must not delete the unclaimed item (that is what the unclaimed-attachment
    /// test guards).
    /// </summary>
    public static bool ClaimRemovesMail(MailMessage mail) =>
        AuctionMailKind.IsAuction(mail.Kind) && !mail.HasUnclaimedAttachment;

    private async Task RemoveIfSpentAuctionMailAsync(MailMessage mail)
    {
        if (!ClaimRemovesMail(mail)) return;
        try
        {
            await _repo.DeleteIfSpentAsync(mail.ReceiverCharId, mail.Uid);
        }
        catch (Exception ex)
        {
            // The mail is already marked claimed, so what stays is an empty row the player can delete.
            _log.LogWarning(ex, "Removing claimed auction mail {Uid:X} of char {Char} failed", mail.Uid, mail.ReceiverCharId);
        }
    }

    public Task<bool> DeleteMailAsync(uint charId, ulong uid) => _repo.DeleteAsync(charId, uid);

    // ---------------------------------------------------------------------------------------------
    // Notify
    // ---------------------------------------------------------------------------------------------

    /// <summary>Sends 2449 OnMailNew to the receiver if they are online, carrying their unread count.</summary>
    public async Task NotifyNewMailAsync(uint receiverCharId)
    {
        try
        {
            var online = _players.GetPlayerByCharacterId(receiverCharId);
            if (online == null) return;
            var session = _broadcast.GetSession(online.SessionId);
            if (session == null) return;

            long unread = await _repo.CountUnreadAsync(receiverCharId);
            if (unread > ushort.MaxValue) unread = ushort.MaxValue; // the 2449 count field is a u16
            var body = MailPackets.BuildMailNew2449(online.EntityId, (ushort)unread);
            await session.SendRmiAsync(RO2.Network.Protocol.ZoneS2COpcodes.OnMailNew, body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OnMailNew notify failed for char {Char}", receiverCharId);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<Character?> ResolveCharacterByNameAsync(string name)
    {
        // Exact match first (fast, index-friendly), then a case-insensitive fallback.
        var exact = await _characters.FindByExactNameAsync(name);
        if (exact != null) return exact;

        return await _characters.FindByNameIgnoreCaseAsync(name);
    }
}
