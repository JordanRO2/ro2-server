using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Mails.Domain;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.MailCharacterization;

/// <summary>
/// B9 S1: the 1393 ReqMailSend decision chain as the host runs it today: parse, zeny cap, postage + zeny debit, ownership and
/// tradeability of the attachment, recipient resolution, mailbox-full, item take, store, notify. Every refusal is the one 2445
/// frame (<see cref="MailPackets.BuildMailSendFailed2445"/>) and hands the charge back.
/// </summary>
public class MailSendCharacterizationTests
{
    private const uint Recipient = 50;
    private static readonly ushort OpSend = ZoneS2COpcodes.OnMailSend;       // 2445

    private static MailSendHandler Handler(MailRig r) =>
        new(r.Scopes, NullLogger<MailSendHandler>.Instance, r.Players, r.Broadcast);

    private static void AssertRefused(List<(ushort Op, byte[] Body)> frames, ushort code)
    {
        var f = Assert.Single(frames);
        Assert.Equal(OpSend, f.Op);
        Assert.Equal(Hex(MailPackets.BuildMailSendFailed2445(MailRig.SelfHandle, code)), Hex(f.Body));
    }

    // ---------------------------------------------------------------- result codes and constants

    [Fact]
    public void The_result_codes_dispositions_and_limits_are_pinned()
    {
        Assert.Equal((ushort)16201, MailPackets.SendSystemError);
        Assert.Equal((ushort)16202, MailPackets.SendTargetNotFound);
        Assert.Equal((ushort)16203, MailPackets.ReadInvalidMail);
        Assert.Equal((ushort)16203, MailPackets.GetItemInvalidMail);
        Assert.Equal((ushort)16204, MailPackets.SendMailboxFull);
        Assert.Equal((ushort)16207, MailPackets.GetItemNoSpace);
        Assert.Equal((ushort)16201, MailPackets.GetItemSystemError);
        Assert.Equal((ushort)16208, MailPackets.SendItemEmpty);
        Assert.Equal((ushort)16209, MailPackets.SendNotEnoughZeny);
        Assert.Equal((ushort)0, MailPackets.ReadOk);
        Assert.Equal((byte)1, MailPackets.GetItemKeepMail);
        Assert.Equal((byte)1, MailPackets.GetMoneyKeepMail);
        Assert.Equal((byte)2, MailPackets.ClaimDeletesMail);
        Assert.Equal((byte)0, MailPackets.GetItemFailedDisposition);
        Assert.Equal(10L, MailPackets.PostageZeny);          // the whole "fee": a flat 10 zeny, no percentage, no per-item part
        Assert.Equal(31, MailPackets.MailboxPlayerSendLimit);
        Assert.Equal(30, MailPackets.MailLifetimeDays);
        Assert.Equal((byte)102, MailPackets.MailAttachmentContainer);
        Assert.Equal((byte)103, MailPackets.AuctionAttachmentContainer);
        Assert.Equal(146, MailPackets.ListRecordLength);
        Assert.Equal(126, MailPackets.ItemRecordLength);
    }

    // ---------------------------------------------------------------- parse and postage

    [Fact]
    public async Task An_unparseable_body_and_a_blank_recipient_both_answer_16201_and_charge_nothing()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        var h = Handler(r);

        AssertRefused(await r.CallAsync(h, new byte[] { 1, 2, 3 }), MailPackets.SendSystemError);   // not even an envelope
        AssertRefused(await r.CallAsync(h, MailRig.Ref(1)), MailPackets.SendSystemError);           // a 16-byte blob: under the 113-byte fixed part
        AssertRefused(await r.CallAsync(h, MailRig.Send("   ")), MailPackets.SendSystemError);      // whitespace recipient fails the parse
        Assert.Equal(100, r.Player.Gold);
        Assert.Empty(r.Fx.Mail.Mails);
    }

    [Theory]
    [InlineData(9L, 0L, false)]     // postage alone is 10: 9 is short
    [InlineData(10L, 0L, true)]     // exactly 10: sent, wallet 0
    [InlineData(109L, 100L, false)] // zeny + postage = 110
    [InlineData(110L, 100L, true)]
    public async Task Postage_is_ten_zeny_plus_the_attached_zeny_taken_up_front(long wallet, long gold, bool sent)
    {
        await using var r = await MailRig.StartAsync(gold: wallet);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");

        var frames = await r.CallAsync(Handler(r), MailRig.Send("Rcvr", gold: gold));

        if (!sent)
        {
            AssertRefused(frames, MailPackets.SendNotEnoughZeny);
            Assert.Equal(wallet, r.Player.Gold);
            Assert.Empty(r.Fx.Mail.Mails);
            return;
        }
        var f = Assert.Single(frames);
        Assert.Equal(OpSend, f.Op);
        Assert.Equal(Hex(MailPackets.BuildMailSend2445(MailRig.SelfHandle, wallet - gold - 10, 0, 0, 0, 0)), Hex(f.Body));
        Assert.Equal(wallet - gold - 10, r.Player.Gold);
        Assert.Equal(wallet - gold - 10, r.Fx.WalletOf(MailRig.Self).Gold);          // persisted via UpdateGoldAsync
        Assert.Equal(gold, Assert.Single(r.Fx.Mail.Mails).Gold);
    }

    [Fact]
    public async Task The_zeny_cap_is_Config_Def_92_and_a_wire_value_above_long_max_clamps_to_long_max()
    {
        var store = await MailRig.StoreAsync(config: (92, "1000"));
        await using var r = await MailRig.StartAsync(gold: 5000, store: store);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var h = Handler(r);

        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr", gold: 1001)), MailPackets.SendSystemError);   // over the cap: 16201, not 16209
        Assert.Equal(5000, r.Player.Gold);

        Assert.Equal(OpSend, Assert.Single(await r.CallAsync(h, MailRig.Send("Rcvr", gold: 1000))).Op);          // the cap itself passes
        Assert.Equal(5000 - 1000 - 10, r.Player.Gold);

        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr", rawGold: ulong.MaxValue)), MailPackets.SendSystemError);   // clamped, then over the cap
    }

    [Fact]
    public async Task Without_a_cap_row_the_cap_is_unlimited_so_a_huge_wire_value_fails_as_not_enough_zeny()
    {
        await using var r = await MailRig.StartAsync(gold: 5000);                    // no GameDataStore registered
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");

        // ulong.MaxValue clamps to long.MaxValue in the parse; long.MaxValue + postage would overflow, which the handler answers as 16209.
        AssertRefused(await r.CallAsync(Handler(r), MailRig.Send("Rcvr", rawGold: ulong.MaxValue)), MailPackets.SendNotEnoughZeny);
        Assert.Equal(5000, r.Player.Gold);
    }

    // ---------------------------------------------------------------- recipient

    [Fact]
    public async Task An_unknown_recipient_is_16202_and_the_charge_is_handed_back_a_case_difference_still_resolves_and_a_trailing_space_does_not()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var h = Handler(r);

        AssertRefused(await r.CallAsync(h, MailRig.Send("Nobody", gold: 20)), MailPackets.SendTargetNotFound);
        Assert.Equal(100, r.Player.Gold);                                            // postage + zeny credited back
        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr ")), MailPackets.SendTargetNotFound);   // typed exactly: no trim
        Assert.Empty(r.Fx.Mail.Mails);

        Assert.Equal(OpSend, Assert.Single(await r.CallAsync(h, MailRig.Send("rCVR"))).Op);   // FindByNameIgnoreCase fallback
        var mail = Assert.Single(r.Fx.Mail.Mails);
        Assert.Equal(Recipient, mail.ReceiverCharId);
        Assert.Equal("Rcvr", mail.ReceiverName);                                     // the stored name is the character's, not as typed
        Assert.Equal(90, r.Player.Gold);
    }

    // ---------------------------------------------------------------- mailbox full

    [Theory]
    [InlineData(30, 0, true)]      // one below the limit: sent
    [InlineData(31, 0, false)]     // at the limit: refused
    [InlineData(40, 0, false)]
    [InlineData(0, 31, true)]      // 31 mails all older than 30 days: none are live, so sent
    [InlineData(20, 11, true)]     // 20 live + 11 expired: 20 < 31
    public async Task The_box_is_full_at_31_live_mails_and_expired_mail_does_not_count(int live, int expired, bool sent)
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        for (int i = 0; i < live; i++) r.Mail(Recipient, read: i % 2 == 0, sentAt: DateTime.UtcNow.AddDays(-29).AddHours(-23));   // read or not, all count
        for (int i = 0; i < expired; i++) r.Mail(Recipient, sentAt: DateTime.UtcNow.AddDays(-30).AddMinutes(-5));
        var before = r.Fx.Mail.Mails.Count;

        var frames = await r.CallAsync(Handler(r), MailRig.Send("Rcvr"));

        if (sent) { Assert.Equal(OpSend, Assert.Single(frames).Op); Assert.Equal(before + 1, r.Fx.Mail.Mails.Count); Assert.Equal(90, r.Player.Gold); }
        else { AssertRefused(frames, MailPackets.SendMailboxFull); Assert.Equal(before, r.Fx.Mail.Mails.Count); Assert.Equal(100, r.Player.Gold); }
    }

    [Fact]
    public async Task A_full_box_refuses_before_anything_is_taken_from_the_sender()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        for (int i = 0; i < 31; i++) r.Mail(Recipient);
        var item = r.Fx.GiveItem(MailRig.Self, 501, count: 5);

        AssertRefused(await r.CallAsync(Handler(r), MailRig.Send("Rcvr", itemUid: item.Id, itemCount: 2)), MailPackets.SendMailboxFull);
        Assert.Equal(5, Assert.Single(r.Fx.Inventory.Rows).Count);
    }

    // ---------------------------------------------------------------- attachment

    [Fact]
    public async Task An_attachment_the_sender_does_not_own_or_that_is_not_in_the_bag_is_16208_and_refunds()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var theirs = r.Fx.GiveItem(Recipient, 501, count: 1);
        var worn = r.Fx.GiveItem(MailRig.Self, 502, storageType: 1);
        var h = Handler(r);

        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr", gold: 30, itemUid: 987654321)), MailPackets.SendItemEmpty);   // unknown uid
        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr", itemUid: theirs.Id)), MailPackets.SendItemEmpty);               // someone else's
        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr", itemUid: worn.Id)), MailPackets.SendItemEmpty);                 // worn gear, not StorageType 0
        Assert.Equal(100, r.Player.Gold);
        Assert.Empty(r.Fx.Mail.Mails);
    }

    [Fact]
    public async Task An_untradeable_or_bound_item_is_16208_while_an_unknown_template_passes()
    {
        var store = await MailRig.StoreAsync(items:
        [
            new ItemData { ID = 601, Is_Trade = 0 },
            new ItemData { ID = 602, Is_Trade = 1, Bind_Type = 2 },
            new ItemData { ID = 603, Is_Trade = 1 },
        ]);
        await using var r = await MailRig.StartAsync(gold: 100, store: store);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var untradeable = r.Fx.GiveItem(MailRig.Self, 601);
        var bound = r.Fx.GiveItem(MailRig.Self, 602);
        var fine = r.Fx.GiveItem(MailRig.Self, 603);
        var h = Handler(r);

        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr", itemUid: untradeable.Id)), MailPackets.SendItemEmpty);
        AssertRefused(await r.CallAsync(h, MailRig.Send("Rcvr", itemUid: bound.Id)), MailPackets.SendItemEmpty);
        Assert.Equal(3, r.Fx.Inventory.Rows.Count);
        Assert.Equal(100, r.Player.Gold);

        Assert.Equal(OpSend, Assert.Single(await r.CallAsync(h, MailRig.Send("Rcvr", itemUid: fine.Id))).Op);
    }

    [Theory]
    [InlineData(5, 2, 2, 3)]    // part of a stack: the stack keeps 3
    [InlineData(5, 0, 5, 0)]    // count byte 0 means the whole stack
    [InlineData(5, 5, 5, 0)]
    [InlineData(5, 9, 5, 0)]    // more than the stack is clamped to the stack
    [InlineData(5, 1, 1, 4)]
    public async Task The_mailed_count_is_min_of_the_request_and_the_stack_and_the_frame_reports_what_is_left(
        int stack, int requested, int mailed, int left)
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var item = r.Fx.GiveItem(MailRig.Self, 501, count: stack, slot: 0);

        var f = Assert.Single(await r.CallAsync(Handler(r), MailRig.Send("Rcvr", itemUid: item.Id, itemCount: (byte)requested)));

        // slot 0 of the main bag is client (container 1, cell 50) per ItemWorkbench.ClientBagSlot
        Assert.Equal(Hex(MailPackets.BuildMailSend2445(MailRig.SelfHandle, 90, item.Id, 50, 1, (byte)left)), Hex(f.Body));
        var mail = Assert.Single(r.Fx.Mail.Mails);
        Assert.Equal((501u, mailed, item.Id), (mail.AttachedItemId, mail.AttachedItemCount, mail.AttachedItemUid));
        if (left == 0) Assert.Empty(r.Fx.Inventory.Rows); else Assert.Equal(left, Assert.Single(r.Fx.Inventory.Rows).Count);
    }

    [Fact]
    public async Task A_sent_mail_stores_the_snapshot_the_sender_and_unread_with_empty_strings_not_null()
    {
        await using var r = await MailRig.StartAsync(gold: 100, name: "");           // a sender with no name
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var item = r.Fx.GiveItem(MailRig.Self, 501, count: 1, slot: 0);
        var before = DateTime.UtcNow;

        await r.CallAsync(Handler(r), MailRig.Send("Rcvr", title: "Hi", gold: 7, itemUid: item.Id, itemCount: 1, body: "yo"));

        var mail = Assert.Single(r.Fx.Mail.Mails);
        Assert.Equal((Recipient, "Rcvr", MailRig.Self), (mail.ReceiverCharId, mail.ReceiverName, mail.SenderCharId));
        Assert.Equal("Unknown", mail.SenderName);                                    // empty sender name -> "Unknown"
        Assert.Equal(("Hi", "yo", 7L), (mail.Title, mail.Body, mail.Gold));
        Assert.False(mail.IsRead);
        Assert.Equal((0, false, false), ((int)mail.Kind, mail.ItemClaimed, mail.GoldClaimed));
        Assert.InRange(mail.SentAt, before, DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, mail.SentAt.Kind);
        Assert.Equal(0x0E4B4B8BUL, mail.Uid >> 32);                                  // the fake store keeps the real uid prefix

        var blank = await MailRig.StartAsync(gold: 100);
        await using (blank)
        {
            blank.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
            await blank.CallAsync(Handler(blank), MailRig.Send("Rcvr", title: "", body: ""));
            var m = Assert.Single(blank.Fx.Mail.Mails);
            Assert.Equal((string.Empty, string.Empty), (m.Title, m.Body));
            Assert.Equal("Sender", m.SenderName);
        }
    }

    // ---------------------------------------------------------------- quirks: blanket catch, notify

    [Fact]
    public async Task A_store_failure_inside_SendMailAsync_is_swallowed_into_16201_and_the_charge_is_refunded()
    {
        await using var r = await MailRig.StartAsync(gold: 100,
            characters: fx => ThrowingProxy<RO2.Characters.Domain.ICharacterRepository>.Wrap(fx.CharacterRepo, nameof(RO2.Characters.Domain.ICharacterRepository.FindByExactNameAsync)));

        AssertRefused(await r.CallAsync(Handler(r), MailRig.Send("Rcvr", gold: 20)), MailPackets.SendSystemError);
        Assert.Equal(100, r.Player.Gold);
    }

    [Fact]
    public async Task SendMailAsync_returns_each_SendResult_without_throwing()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var svc = new MailService(r.Fx.MailRepo, r.Fx.CharacterRepo, r.Players, r.Broadcast, NullLogger.Instance);
        var inv = r.Fx.InventoryRepo;

        Assert.Equal(MailService.SendResult.InvalidRequest, (await svc.SendMailAsync(r.Player, new MailPackets.MailSendRequest(0, " ", "", 0, 0, 0, ""), inv)).result);
        Assert.Equal(MailService.SendResult.RecipientNotFound, (await svc.SendMailAsync(r.Player, new MailPackets.MailSendRequest(0, "Nope", "", 0, 0, 0, ""), inv)).result);
        var (ok, mail) = await svc.SendMailAsync(r.Player, new MailPackets.MailSendRequest(0, "Rcvr", "t", 0, 0, 0, "b"), inv);
        Assert.Equal(MailService.SendResult.Sent, ok);
        Assert.Equal("t", mail!.Title);

        // an item uid that is not an owned bag item is sent WITHOUT it (the handler's own pre-check is what answers 16208)
        var (ok2, mail2) = await svc.SendMailAsync(r.Player, new MailPackets.MailSendRequest(0, "Rcvr", "t", 424242, 1, 0, ""), inv);
        Assert.Equal(MailService.SendResult.Sent, ok2);
        Assert.Equal(0u, mail2!.AttachedItemId);

        for (int i = 0; i < 29; i++) r.Mail(Recipient);                              // 2 sent + 29 = 31 live
        Assert.Equal(MailService.SendResult.MailboxFull, (await svc.SendMailAsync(r.Player, new MailPackets.MailSendRequest(0, "Rcvr", "", 0, 0, 0, ""), inv)).result);

        var boom = new MailService(ThrowingProxy<IMailRepository>.Wrap(r.Fx.MailRepo, nameof(IMailRepository.CountLiveInboxAsync)),
            r.Fx.CharacterRepo, r.Players, r.Broadcast, NullLogger.Instance);
        Assert.Equal(MailService.SendResult.Error, (await boom.SendMailAsync(r.Player, new MailPackets.MailSendRequest(0, "Rcvr", "", 0, 0, 0, ""), inv)).result);
    }

    [Fact]
    public async Task A_failed_insert_gives_the_taken_item_back_as_a_fresh_unplaced_row_and_answers_Error()
    {
        await using var r = await MailRig.StartAsync(gold: 100);
        r.Fx.AddOfflineCharacter(Recipient, 0, name: "Rcvr");
        var item = r.Fx.GiveItem(MailRig.Self, 501, count: 3, slot: 0);
        var svc = new MailService(ThrowingProxy<IMailRepository>.Wrap(r.Fx.MailRepo, nameof(IMailRepository.AddAsync)),
            r.Fx.CharacterRepo, r.Players, r.Broadcast, NullLogger.Instance);

        var (result, mail) = await svc.SendMailAsync(r.Player, new MailPackets.MailSendRequest(0, "Rcvr", "t", item.Id, 3, 0, ""), r.Fx.InventoryRepo);

        Assert.Equal(MailService.SendResult.Error, result);
        Assert.Null(mail);
        var back = Assert.Single(r.Fx.Inventory.Rows);                               // the whole stack was taken, then refunded
        Assert.Equal((501u, 3, 0), (back.ItemId, back.Count, (int)back.StorageType));
        Assert.Empty(r.Fx.Mail.Mails);
    }

    [Fact]
    public async Task An_online_recipient_gets_a_2449_with_their_unread_count_and_an_offline_one_gets_nothing()
    {
        await using var r = await MailRig.StartAsync(gold: 100, sessions: 2);
        var online = r.AddOnline(Recipient, "Rcvr", 0x0A0B0C11);
        r.Fx.AddOfflineCharacter(51, 0, name: "Away");
        r.Mail(Recipient);                                                           // one unread already waiting
        var h = Handler(r);

        await r.CallAsync(h, MailRig.Send("Rcvr"));
        var seen = await r.DrainAsync(1);
        var f = Assert.Single(seen);
        Assert.Equal(ZoneS2COpcodes.OnMailNew, f.Op);
        Assert.Equal(Hex(MailPackets.BuildMailNew2449(online.EntityId, 2)), Hex(f.Body));    // unread = the old one + the new one

        await r.CallAsync(h, MailRig.Send("Away"));
        Assert.Empty(await r.DrainAsync(1));
    }

    [Fact]
    public async Task The_unread_count_in_a_2449_saturates_at_a_u16()
    {
        await using var r = await MailRig.StartAsync(sessions: 2);
        var online = r.AddOnline(Recipient, "Rcvr", 0x0A0B0C11);
        var svc = new MailService(r.Fx.MailRepo, r.Fx.CharacterRepo, r.Players, r.Broadcast, NullLogger.Instance);
        for (int i = 0; i < 70_000; i++) r.Fx.Mail.Seed(new RO2.Mails.Domain.MailMessage { ReceiverCharId = Recipient });

        await svc.NotifyNewMailAsync(Recipient);

        var f = Assert.Single(await r.DrainAsync(1));
        Assert.Equal(Hex(MailPackets.BuildMailNew2449(online.EntityId, ushort.MaxValue)), Hex(f.Body));
    }
}
