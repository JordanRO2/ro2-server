using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Mails.Application;
using RO2.Mails.Domain;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.MailCharacterization;

/// <summary>
/// B9 S1: the 1397 read, 1398 delete, 1457 zeny claim, 1396 item claim and 1394 list as the host answers them today, including the
/// quirks the carve must not "fix": 2448 goes out even when nothing was deleted, a zeny claim of a missing mail still answers a
/// success 2617, an unknown uid on a read answers 16203 while an unparseable body answers nothing at all.
/// </summary>
public class MailReadClaimDeleteCharacterizationTests
{
    private const ulong ItemUid = 9001;
    private static readonly byte[] Empty = [];

    private static MailReadHandler Read(MailRig r) => new(r.Scopes, NullLogger<MailReadHandler>.Instance, r.Players, r.Broadcast);
    private static MailDeleteHandler Delete(MailRig r) => new(r.Scopes, NullLogger<MailDeleteHandler>.Instance, r.Players, r.Broadcast);
    private static MailGetMoneyHandler Money(MailRig r) => new(r.Scopes, NullLogger<MailGetMoneyHandler>.Instance, r.Players, r.Broadcast);
    private static MailGetItemHandler Item(MailRig r) => new(r.Scopes, NullLogger<MailGetItemHandler>.Instance, r.Players, r.Broadcast);
    private static MailListHandler List(MailRig r) => new(r.Scopes, NullLogger<MailListHandler>.Instance, r.Players, r.Broadcast);

    // ---------------------------------------------------------------- unparseable bodies answer nothing

    [Fact]
    public async Task An_unparseable_read_delete_zeny_or_item_request_gets_no_reply_at_all()
    {
        await using var r = await MailRig.StartAsync();
        r.Mail(gold: 5, item: 501, itemUid: ItemUid);
        foreach (var bad in new[] { Empty, new byte[] { 1, 2, 3 }, new byte[] { 1, 4, 4, 0, 0, 0 } })   // empty, junk, a blob shorter than 16
        {
            Assert.Empty(await r.CallAsync(Read(r), bad));
            Assert.Empty(await r.CallAsync(Delete(r), bad));
            Assert.Empty(await r.CallAsync(Money(r), bad));
            Assert.Empty(await r.CallAsync(Item(r), bad));
        }
        Assert.Single(r.Fx.Mail.Mails);
    }

    // ---------------------------------------------------------------- 1397 read

    [Fact]
    public async Task Reading_an_unread_mail_marks_it_read_and_the_first_reply_already_shows_the_read_flag()
    {
        await using var r = await MailRig.StartAsync();
        var m = r.Mail();
        Assert.False(r.Stored(m.Uid).IsRead);

        var f = Assert.Single(await r.CallAsync(Read(r), MailRig.Ref(m.Uid)));

        Assert.Equal(ZoneS2COpcodes.OnMailRead, f.Op);
        Assert.Equal(Hex(MailPackets.BuildMailRead2446(MailRig.SelfHandle, m.Uid, isRead: true, m.SentAt, 0, new byte[MailPackets.ItemRecordLength], "B")),
            Hex(f.Body));
        Assert.True(r.Stored(m.Uid).IsRead);
        Assert.Equal(0, await r.Fx.MailRepo.CountUnreadAsync(MailRig.Self));
    }

    [Fact]
    public async Task A_read_shows_the_body_the_auction_price_and_the_attachment_record_and_an_expired_mail_is_still_readable()
    {
        await using var r = await MailRig.StartAsync();
        var m = r.Fx.Mail.Seed(new MailMessage
        {
            ReceiverCharId = MailRig.Self, Body = "hello", AuctionPrice = 1234, Kind = AuctionMailKind.Purchased,
            AttachedItemId = 501, AttachedItemUid = ItemUid, AttachedItemCount = 2, SentAt = DateTime.UtcNow.AddDays(-90),
        });

        var f = Assert.Single(await r.CallAsync(Read(r), MailRig.Ref(m.Uid)));

        var rec = MailPackets.BuildAttachmentRecord126(m, claimedThisEntry: false);
        Assert.Equal(Hex(MailPackets.BuildMailRead2446(MailRig.SelfHandle, m.Uid, true, m.SentAt, 1234, rec, "hello")), Hex(f.Body));
        Assert.Equal(MailPackets.AuctionAttachmentContainer, rec[15]);               // an auction mail's item sits in container 103
    }

    [Fact]
    public async Task Reading_an_unknown_uid_or_someone_elses_mail_answers_16203_while_marking_nothing()
    {
        await using var r = await MailRig.StartAsync();
        r.Fx.AddOfflineCharacter(60, 0, name: "Other");
        var theirs = r.Mail(receiver: 60);
        var expected = Hex(MailPackets.BuildMailReadFailed2446(MailRig.SelfHandle, MailPackets.ReadInvalidMail));

        var unknown = Assert.Single(await r.CallAsync(Read(r), MailRig.Ref(0xDEADBEEF)));
        var foreign = Assert.Single(await r.CallAsync(Read(r), MailRig.Ref(theirs.Uid)));

        Assert.Equal((ZoneS2COpcodes.OnMailRead, expected), (unknown.Op, Hex(unknown.Body)));
        Assert.Equal((ZoneS2COpcodes.OnMailRead, expected), (foreign.Op, Hex(foreign.Body)));
        Assert.False(r.Stored(theirs.Uid).IsRead);
    }

    [Fact]
    public async Task ReadMailAsync_writes_only_on_the_unread_to_read_transition()
    {
        await using var r = await MailRig.StartAsync();
        var svc = new MailService(r.Fx.MailRepo, r.Fx.CharacterRepo, r.Players, r.Broadcast, NullLogger.Instance);
        var unread = r.Mail();
        var already = r.Mail(read: true);

        var a = await svc.ReadMailAsync(MailRig.Self, unread.Uid);
        var b = await svc.ReadMailAsync(MailRig.Self, already.Uid);

        Assert.True(a!.IsRead);
        Assert.True(b!.IsRead);
        Assert.Null(await svc.ReadMailAsync(MailRig.Self, 12345));
        Assert.True(r.Stored(unread.Uid).IsRead);
    }

    // ---------------------------------------------------------------- 1398 delete

    [Fact]
    public async Task Delete_sends_2448_whether_or_not_anything_was_deleted_and_never_looks_at_unclaimed_attachments()
    {
        await using var r = await MailRig.StartAsync();
        r.Fx.AddOfflineCharacter(60, 0, name: "Other");
        var plain = r.Mail();
        var loaded = r.Mail(gold: 500, item: 501, itemUid: ItemUid);                 // delete does NOT refuse a mail with unclaimed zeny + item
        var theirs = r.Mail(receiver: 60);

        var del = Assert.Single(await r.CallAsync(Delete(r), MailRig.Ref(plain.Uid)));
        Assert.Equal((ZoneS2COpcodes.OnMailDelete, Hex(MailPackets.BuildMailDelete2448(MailRig.SelfHandle, plain.Uid))), (del.Op, Hex(del.Body)));
        Assert.DoesNotContain(r.Fx.Mail.Mails, x => x.Uid == plain.Uid);

        var again = Assert.Single(await r.CallAsync(Delete(r), MailRig.Ref(plain.Uid)));          // already gone: the same success frame
        Assert.Equal(Hex(MailPackets.BuildMailDelete2448(MailRig.SelfHandle, plain.Uid)), Hex(again.Body));

        var unknown = Assert.Single(await r.CallAsync(Delete(r), MailRig.Ref(777)));
        Assert.Equal(Hex(MailPackets.BuildMailDelete2448(MailRig.SelfHandle, 777)), Hex(unknown.Body));

        var foreign = Assert.Single(await r.CallAsync(Delete(r), MailRig.Ref(theirs.Uid)));       // someone else's: not deleted, still "ok"
        Assert.Equal(Hex(MailPackets.BuildMailDelete2448(MailRig.SelfHandle, theirs.Uid)), Hex(foreign.Body));
        Assert.Contains(r.Fx.Mail.Mails, x => x.Uid == theirs.Uid);

        Assert.Single(await r.CallAsync(Delete(r), MailRig.Ref(loaded.Uid)));
        Assert.DoesNotContain(r.Fx.Mail.Mails, x => x.Uid == loaded.Uid);                         // the item and zeny are lost with it
    }

    // ---------------------------------------------------------------- 1457 zeny claim

    private static async Task<(ulong Wallet, byte Disposition, ushort Result, ulong Echo)> MoneyReplyAsync(MailRig r, ulong uid)
    {
        var f = Assert.Single(await r.CallAsync(Money(r), MailRig.Ref(uid)));
        Assert.Equal(ZoneS2COpcodes.OnMailGetMoney, f.Op);
        var b = MailRig.Blob(f.Body);
        Assert.Equal(27, b.Length);
        return (BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(18)), b[26], BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(10)));
    }

    [Fact]
    public async Task A_zeny_claim_credits_once_keeps_a_player_mail_and_a_second_claim_grants_nothing()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        var m = r.Mail(gold: 500);

        var first = await MoneyReplyAsync(r, m.Uid);
        Assert.Equal((600UL, MailPackets.GetMoneyKeepMail, (ushort)0, m.Uid), first);
        Assert.Equal(600, r.Player.Gold);
        Assert.Equal(600, r.Fx.WalletOf(MailRig.Self).Gold);                          // persisted
        Assert.True(r.Stored(m.Uid).GoldClaimed);

        var second = await MoneyReplyAsync(r, m.Uid);
        Assert.Equal((600UL, MailPackets.GetMoneyKeepMail, (ushort)0, m.Uid), second);   // still a success frame, no credit
        Assert.Equal(600, r.Player.Gold);
    }

    [Fact]
    public async Task A_zeny_claim_of_a_missing_foreign_or_empty_mail_still_answers_a_success_2617_with_the_wallet_unchanged()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(60, 0, name: "Other");
        var theirs = r.Mail(receiver: 60, gold: 500);
        var empty = r.Mail();
        var negative = r.Mail(gold: -50);

        Assert.Equal((100UL, (byte)1, (ushort)0, 4242UL), await MoneyReplyAsync(r, 4242));
        Assert.Equal((100UL, (byte)1, (ushort)0, theirs.Uid), await MoneyReplyAsync(r, theirs.Uid));
        Assert.Equal((100UL, (byte)1, (ushort)0, empty.Uid), await MoneyReplyAsync(r, empty.Uid));
        Assert.Equal((100UL, (byte)1, (ushort)0, negative.Uid), await MoneyReplyAsync(r, negative.Uid));
        Assert.Equal(100, r.Player.Gold);
        Assert.False(r.Stored(theirs.Uid).GoldClaimed);
    }

    [Theory]
    [InlineData(AuctionMailKind.Proceeds, true)]       // auction mails 2 / 7 / 10 / 11 and the bid kinds 1 / 4 / 6 / 8 are deleted ...
    [InlineData(AuctionMailKind.Expired, true)]
    [InlineData(AuctionMailKind.Purchased, true)]
    [InlineData(AuctionBidMailKind.OutbidRefund, true)]
    [InlineData((byte)0, false)]                       // ... a player mail stays ...
    [InlineData((byte)3, false)]                       // ... and so do kinds outside the auction set
    [InlineData((byte)22, false)]
    public async Task A_spent_auction_mail_is_deleted_from_the_store_and_answered_with_disposition_2(byte kind, bool deletes)
    {
        await using var r = await MailRig.StartAsync();
        var m = r.Mail(gold: 500, kind: kind);

        var (_, disposition, _, _) = await MoneyReplyAsync(r, m.Uid);

        Assert.Equal(deletes ? MailPackets.ClaimDeletesMail : MailPackets.GetMoneyKeepMail, disposition);
        Assert.Equal(!deletes, r.Fx.Mail.Mails.Any(x => x.Uid == m.Uid));
    }

    [Fact]
    public async Task An_auction_mail_with_an_unclaimed_item_survives_its_zeny_claim_with_disposition_1()
    {
        await using var r = await MailRig.StartAsync();
        var m = r.Mail(gold: 500, item: 501, itemUid: ItemUid, kind: AuctionMailKind.Purchased);

        var (wallet, disposition, _, _) = await MoneyReplyAsync(r, m.Uid);

        Assert.Equal((500UL, MailPackets.GetMoneyKeepMail), (wallet, disposition));
        Assert.True(r.Stored(m.Uid).GoldClaimed);
        Assert.False(r.Stored(m.Uid).ItemClaimed);
    }

    // ---------------------------------------------------------------- 1396 item claim

    private static (ushort Result, ulong MailUid, ulong RecordUid, byte Disposition, int BlobLength) ItemReply(List<(ushort Op, byte[] Body)> frames)
    {
        var f = Assert.Single(frames);
        Assert.Equal(ZoneS2COpcodes.OnMailGetItem, f.Op);
        var b = MailRig.Blob(f.Body);
        return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)), BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(10)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(18)), b[144], b.Length);
    }

    [Fact]
    public async Task An_item_claim_with_nothing_to_claim_is_16203_with_disposition_0_and_an_empty_record()
    {
        var store = await MailRig.StoreAsync();
        await using var r2 = await MailRig.StartAsync(store: store);
        r2.Fx.AddOfflineCharacter(60, 0, name: "Other");
        var noItem = r2.Mail(gold: 5);
        var claimed = r2.Fx.Mail.Seed(new MailMessage { ReceiverCharId = MailRig.Self, AttachedItemId = 501, ItemClaimed = true });
        var theirs = r2.Mail(receiver: 60, item: 501, itemUid: ItemUid);

        foreach (var uid in new[] { 31337UL, noItem.Uid, claimed.Uid, theirs.Uid })
        {
            var f = Assert.Single(await r2.CallAsync(Item(r2), MailRig.Ref(uid)));
            Assert.Equal(Hex(MailPackets.BuildMailGetItemFailed2447(MailRig.SelfHandle, MailPackets.GetItemInvalidMail, uid)), Hex(f.Body));
            Assert.Equal((MailPackets.GetItemInvalidMail, uid, 0UL, MailPackets.GetItemFailedDisposition, 145), ItemReply([f]));
        }
        Assert.False(r2.Stored(theirs.Uid).ItemClaimed);
    }

    [Fact]
    public async Task An_item_claim_places_the_item_under_its_own_uid_flips_the_flag_and_keeps_a_player_mail()
    {
        var store = await MailRig.StoreAsync();
        await using var r = await MailRig.StartAsync(store: store);
        var m = r.Mail(item: 501, itemUid: ItemUid, count: 3);

        var reply = ItemReply(await r.CallAsync(Item(r), MailRig.Ref(m.Uid)));

        Assert.Equal((MailPackets.ReadOk, m.Uid, ItemUid, MailPackets.GetItemKeepMail, 145), reply);
        var row = Assert.Single(r.Fx.Inventory.Rows);
        Assert.Equal((ItemUid, 501u, 3, (short)0, (byte)0), (row.Id, row.ItemId, row.Count, row.Slot, row.StorageType));   // lowest free cell, its own uid
        Assert.True(r.Stored(m.Uid).ItemClaimed);

        // a double click: the second 1396 answers 16203 and grants nothing
        var again = ItemReply(await r.CallAsync(Item(r), MailRig.Ref(m.Uid)));
        Assert.Equal((MailPackets.GetItemInvalidMail, m.Uid, 0UL, MailPackets.GetItemFailedDisposition, 145), again);
        Assert.Single(r.Fx.Inventory.Rows);
    }

    [Fact]
    public async Task A_claimed_auction_item_mail_is_removed_with_disposition_2_unless_zeny_is_still_unclaimed()
    {
        var store = await MailRig.StoreAsync();
        await using var r = await MailRig.StartAsync(store: store);
        var itemOnly = r.Mail(item: 501, itemUid: ItemUid, kind: AuctionMailKind.Purchased);
        var both = r.Mail(item: 502, itemUid: ItemUid + 1, gold: 800, kind: AuctionMailKind.Purchased);

        Assert.Equal((MailPackets.ReadOk, itemOnly.Uid, ItemUid, MailPackets.ClaimDeletesMail, 145), ItemReply(await r.CallAsync(Item(r), MailRig.Ref(itemOnly.Uid))));
        Assert.DoesNotContain(r.Fx.Mail.Mails, x => x.Uid == itemOnly.Uid);

        Assert.Equal((MailPackets.ReadOk, both.Uid, ItemUid + 1, MailPackets.GetItemKeepMail, 145), ItemReply(await r.CallAsync(Item(r), MailRig.Ref(both.Uid))));
        Assert.Contains(r.Fx.Mail.Mails, x => x.Uid == both.Uid);                      // the zeny is still waiting
        Assert.Equal(2, r.Fx.Inventory.Rows.Count);
    }

    [Fact]
    public async Task A_full_bag_answers_16207_and_leaves_the_flag_alone_but_a_stack_it_can_join_still_takes_the_item()
    {
        var store = await MailRig.StoreAsync([new ItemData { ID = 501, Stack_Max = 99 }]);
        await using var r = await MailRig.StartAsync(store: store);
        for (int i = 0; i < 20; i++) r.Fx.GiveItem(MailRig.Self, (uint)7000 + (uint)i, slot: (short)i);   // 20 = BagLayout.MainBagCells, none of template 501
        var m = r.Mail(item: 501, itemUid: ItemUid, count: 4);

        var refused = ItemReply(await r.CallAsync(Item(r), MailRig.Ref(m.Uid)));
        Assert.Equal((MailPackets.GetItemNoSpace, m.Uid, 0UL, MailPackets.GetItemFailedDisposition, 145), refused);
        Assert.False(r.Stored(m.Uid).ItemClaimed);
        Assert.Equal(20, r.Fx.Inventory.Rows.Count);

        // swap one bag row for a joinable 501 stack: a full bag still takes the item
        var victim = r.Fx.Inventory.Rows.First(x => x.Slot == 5);
        await r.Fx.InventoryRepo.RemoveItemAsync(victim.Id, MailRig.Self);
        var stack = r.Fx.GiveItem(MailRig.Self, 501, count: 10, slot: 5);
        var joined = ItemReply(await r.CallAsync(Item(r), MailRig.Ref(m.Uid)));
        Assert.Equal((MailPackets.ReadOk, m.Uid, stack.Id, MailPackets.GetItemKeepMail, 145), joined);   // the record carries the stack's uid
        Assert.Equal(14, r.Fx.Inventory.Rows.Single(x => x.Id == stack.Id).Count);
    }

    [Fact]
    public async Task Without_an_inventory_repository_the_claim_answers_16201()
    {
        var store = await MailRig.StoreAsync();
        await using var r = await MailRig.StartAsync(store: store, withInventory: false);
        var m = r.Mail(item: 501, itemUid: ItemUid);

        Assert.Equal((MailPackets.GetItemSystemError, m.Uid, 0UL, MailPackets.GetItemFailedDisposition, 145), ItemReply(await r.CallAsync(Item(r), MailRig.Ref(m.Uid))));
        Assert.False(r.Stored(m.Uid).ItemClaimed);
    }

    // ---------------------------------------------------------------- pure claim predicates

    [Theory]
    // kind, item, itemClaimed, gold, goldClaimed -> ClaimRemovesMail
    [InlineData((byte)10, 0u, false, 0L, false, true)]     // auction, nothing attached
    [InlineData((byte)10, 501u, false, 0L, false, false)]  // auction, item waiting
    [InlineData((byte)10, 501u, true, 0L, false, true)]    // auction, item claimed
    [InlineData((byte)10, 0u, false, 5L, false, false)]    // auction, zeny waiting
    [InlineData((byte)10, 0u, false, 5L, true, true)]
    [InlineData((byte)10, 501u, true, 5L, false, false)]
    [InlineData((byte)10, 501u, true, 5L, true, true)]
    [InlineData((byte)10, 0u, false, -5L, false, true)]    // non-positive zeny is not waiting
    [InlineData((byte)0, 0u, false, 0L, false, false)]     // player mail never
    [InlineData((byte)0, 501u, true, 5L, true, false)]
    [InlineData((byte)22, 0u, false, 0L, false, false)]    // system mail never
    public void ClaimRemovesMail_needs_an_auction_kind_and_nothing_left_to_claim(byte kind, uint item, bool itemClaimed, long gold, bool goldClaimed, bool expected)
    {
        var m = new MailMessage { Kind = kind, AttachedItemId = item, ItemClaimed = itemClaimed, Gold = gold, GoldClaimed = goldClaimed };
        Assert.Equal(expected, MailService.ClaimRemovesMail(m));
    }

    [Fact]
    public void The_auction_kind_set_is_exactly_1_2_4_6_7_8_10_11()
    {
        var yes = Enumerable.Range(0, 256).Select(i => (byte)i).Where(AuctionMailKind.IsAuction).ToArray();
        Assert.Equal(new byte[] { 1, 2, 4, 6, 7, 8, 10, 11 }, yes);
    }

    [Theory]
    [InlineData(0u, false, 0L, false, false)]
    [InlineData(501u, false, 0L, false, true)]
    [InlineData(501u, true, 0L, false, false)]
    [InlineData(0u, false, 5L, false, true)]
    [InlineData(0u, false, 5L, true, false)]
    [InlineData(0u, false, -5L, false, false)]
    [InlineData(501u, true, 5L, false, true)]
    [InlineData(0u, true, 0L, true, false)]
    public void HasUnclaimedAttachment_is_an_unclaimed_item_or_unclaimed_positive_zeny(uint item, bool itemClaimed, long gold, bool goldClaimed, bool expected)
        => Assert.Equal(expected, new MailMessage { AttachedItemId = item, ItemClaimed = itemClaimed, Gold = gold, GoldClaimed = goldClaimed }.HasUnclaimedAttachment);

    [Fact]
    public void A_stored_count_of_zero_means_one_item()
    {
        Assert.Equal(1, ClaimMailItemUseCase.AttachedCount(new MailMessage { AttachedItemCount = 0 }));
        Assert.Equal(1, ClaimMailItemUseCase.AttachedCount(new MailMessage { AttachedItemCount = -3 }));
        Assert.Equal(7, ClaimMailItemUseCase.AttachedCount(new MailMessage { AttachedItemCount = 7 }));
    }

    // ---------------------------------------------------------------- 1394 list

    [Fact]
    public async Task The_list_projection_hides_claimed_attachments_and_shows_the_template_id_only_for_kinds_1_to_11()
    {
        const uint handle = 0x0001C013;
        await using var r = await MailRig.StartAsync(npcs: MailRig.NpcWithMailbox(handle));
        var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        MailMessage Seed(Action<MailMessage> shape, int hour)
        {
            var m = new MailMessage { ReceiverCharId = MailRig.Self, SenderName = "Snd", Title = "Ttl", SentAt = t.AddHours(hour) };
            shape(m);
            return r.Fx.Mail.Seed(m);
        }
        var m0 = Seed(m => { m.Gold = 500; m.AttachedItemId = 501; m.AttachedItemUid = 9001; m.KindItemId = 777; }, 1);          // kind 0: template hidden
        var m1 = Seed(m => { m.Kind = 1; m.KindItemId = 111; m.IsRead = true; }, 2);                                              // kind 1: shown
        var m11 = Seed(m => { m.Kind = 11; m.KindItemId = 888; m.Gold = 300; m.GoldClaimed = true;
            m.AttachedItemId = 502; m.AttachedItemUid = 9002; m.ItemClaimed = true; }, 3);                                         // both claimed: zeny and uid hidden
        var m12 = Seed(m => { m.Kind = 12; m.KindItemId = 999; }, 4);                                                              // kind 12: hidden
        var m22 = Seed(m => { m.Kind = 22; m.KindItemId = 555; }, 5);

        var f = Assert.Single(await r.CallAsync(List(r), MailRig.List(handle)));

        var rows = new List<byte[]>
        {
            MailPackets.BuildListRecord146(m22.Uid, 22, false, m22.SentAt, 0, 0, 0, "Snd", "Ttl"),                  // newest first
            MailPackets.BuildListRecord146(m12.Uid, 12, false, m12.SentAt, 0, 0, 0, "Snd", "Ttl"),
            MailPackets.BuildListRecord146(m11.Uid, 11, false, m11.SentAt, 0, 0, 888, "Snd", "Ttl"),
            MailPackets.BuildListRecord146(m1.Uid, 1, true, m1.SentAt, 0, 0, 111, "Snd", "Ttl"),
            MailPackets.BuildListRecord146(m0.Uid, 0, false, m0.SentAt, 500, 9001, 0, "Snd", "Ttl"),
        };
        Assert.Equal((ZoneS2COpcodes.OnMailList, Hex(MailPackets.BuildMailList2444(MailRig.SelfHandle, rows))), (f.Op, Hex(f.Body)));
    }

    [Fact]
    public async Task A_refused_mailbox_gate_answers_an_empty_list_whatever_the_box_holds()
    {
        const uint handle = 0x0001C013;
        await using var r = await MailRig.StartAsync(npcs: MailRig.NpcWithMailbox(handle, mapId: 1200));   // the NPC is on another map
        r.Mail();
        var empty = Hex(MailPackets.BuildMailList2444(MailRig.SelfHandle, new List<byte[]>()));

        foreach (var req in new[] { MailRig.List(handle), MailRig.List(0), MailRig.List(0x0001C014), MailRig.List(0x0001C0FF), Empty })
        {
            var f = Assert.Single(await r.CallAsync(List(r), req));
            Assert.Equal((ZoneS2COpcodes.OnMailList, empty), (f.Op, Hex(f.Body)));
        }
    }

    [Fact]
    public async Task The_close_request_sends_nothing()
    {
        await using var r = await MailRig.StartAsync();
        var close = new MailCloseHandler(r.Scopes, NullLogger<MailCloseHandler>.Instance, r.Players, r.Broadcast);
        Assert.Empty(await r.CallAsync(close, MailRig.Ref(1)));
    }
}
