using Npgsql;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="MailRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers id sequencing, field round-trips, inbox ordering and counts, whole-row replace and delete paths including concurrent ones. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresMailTests
{
    private static readonly DateTime Base = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    
    private static long _receiverSeq = Random.Shared.Next(0, 500_000_000);

    private readonly PostgresFixture _fx;
    public PostgresMailTests(PostgresFixture fx) => _fx = fx;

    private static uint NewReceiver() => 3_000_000_000u + (uint)Interlocked.Increment(ref _receiverSeq);

    private static MailMessage Draft(uint receiver, DateTime? sentAt = null) => new()
    {
        ReceiverCharId = receiver,
        ReceiverName = "Rcv",
        SenderCharId = 7,
        SenderName = "Snd",
        Title = "Hello",
        Body = "Body",
        SentAt = sentAt ?? Base,
    };

    [Fact]
    public async Task Add_assigns_increasing_ids_and_retail_uid_shape()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();

        var a = await repo.AddAsync(Draft(rcv), CancellationToken.None);
        var b = await repo.AddAsync(Draft(rcv), CancellationToken.None);

        Assert.True(a.Id > 0);
        Assert.True(b.Id > a.Id);
        Assert.Equal((0x0E4B4B8BUL << 32) | (uint)a.Id, a.Uid);
        Assert.Equal((0x0E4B4B8BUL << 32) | (uint)b.Id, b.Uid);
        Assert.NotEqual(a.Uid, b.Uid);
    }

    [Fact]
    public async Task Add_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var draft = new MailMessage
        {
            ReceiverCharId = rcv,
            ReceiverName = "Receiver",
            SenderCharId = 4_000_000_001u,
            SenderName = "Sender",
            Title = "Title é中",
            Body = "Line1\nLine2",
            AttachedItemUid = 0x0123_4567_89AB_CDEFUL,
            AttachedItemId = 3_000_000_123u,
            AttachedItemCount = 42,
            AttachedItemRefine = 9,
            AttachedItemDurability = 250,
            AttachedItemCardsJson = "[1,2,3]",
            AttachedItemElement = 3,
            AttachedItemDyeColor = 0xFFAABBCCu,
            AttachedItemSocketPunchersJson = "{\"a\":1}",
            AttachedItemDyeColor2 = 0xFF000001u,
            AttachedItemDyeColor3 = 0xFF000002u,
            AttachedItemFxEnchantIndex = 4_000_000_000u,
            AttachedItemEnchantReinforceId = 65_535,
            AttachedItemEnchantSeconds = 60_000,
            AttachedItemBound = true,
            AttachedItemAbilityValue = 0,
            ItemClaimed = true,
            Gold = 9_000_000_000L,
            GoldClaimed = true,
            IsRead = true,
            SentAt = Base.AddMilliseconds(123).AddTicks(4567), 
            Kind = 11,
            KindItemId = 3_100_000_000u,
            AuctionPrice = 5_000_000_500L,
        };

        var added = await repo.AddAsync(draft, CancellationToken.None);
        var got = await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None);

        Assert.NotNull(got);
        Assert.Equal(added.Id, got!.Id);
        Assert.Equal(added.Uid, got.Uid);
        Assert.Equal(rcv, got.ReceiverCharId);
        Assert.Equal("Receiver", got.ReceiverName);
        Assert.Equal(4_000_000_001u, got.SenderCharId);
        Assert.Equal("Sender", got.SenderName);
        Assert.Equal("Title é中", got.Title);
        Assert.Equal("Line1\nLine2", got.Body);
        Assert.Equal(0x0123_4567_89AB_CDEFUL, got.AttachedItemUid);
        Assert.Equal(3_000_000_123u, got.AttachedItemId);
        Assert.Equal(42, got.AttachedItemCount);
        Assert.Equal((byte)9, got.AttachedItemRefine);
        Assert.Equal((short)250, got.AttachedItemDurability);
        Assert.Equal("[1,2,3]", got.AttachedItemCardsJson);
        Assert.Equal((byte)3, got.AttachedItemElement);
        Assert.Equal(0xFFAABBCCu, got.AttachedItemDyeColor);
        Assert.Equal("{\"a\":1}", got.AttachedItemSocketPunchersJson);
        Assert.Equal(0xFF000001u, got.AttachedItemDyeColor2);
        Assert.Equal(0xFF000002u, got.AttachedItemDyeColor3);
        Assert.Equal(4_000_000_000u, got.AttachedItemFxEnchantIndex);
        Assert.Equal((ushort)65_535, got.AttachedItemEnchantReinforceId);
        Assert.Equal((ushort)60_000, got.AttachedItemEnchantSeconds);
        Assert.True(got.AttachedItemBound);
        Assert.Equal(0u, got.AttachedItemAbilityValue); 
        Assert.True(got.ItemClaimed);
        Assert.Equal(9_000_000_000L, got.Gold);
        Assert.True(got.GoldClaimed);
        Assert.True(got.IsRead);
        Assert.Equal(Base.AddMilliseconds(123), got.SentAt);
        Assert.Equal(DateTimeKind.Utc, got.SentAt.Kind);
        Assert.Equal((byte)11, got.Kind);
        Assert.Equal(3_100_000_000u, got.KindItemId);
        Assert.Equal(5_000_000_500L, got.AuctionPrice);
    }

    [Fact]
    public async Task Add_defaults_round_trip_with_nulls_and_negative_durability()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();

        var added = await repo.AddAsync(Draft(rcv), CancellationToken.None);
        var got = await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None);

        Assert.NotNull(got);
        Assert.Null(got!.AttachedItemCardsJson);
        Assert.Null(got.AttachedItemSocketPunchersJson);
        Assert.Null(got.AttachedItemAbilityValue);
        Assert.Equal((short)-1, got.AttachedItemDurability);
        Assert.Equal(0u, got.AttachedItemId);
        Assert.Equal(0L, got.Gold);
        Assert.False(got.IsRead);
        Assert.False(got.ItemClaimed);
        Assert.False(got.GoldClaimed);
        Assert.Equal((byte)0, got.Kind);
    }

    [Fact]
    public async Task Add_concurrent_ids_and_uids_are_unique()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();

        var added = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => repo.AddAsync(Draft(rcv), CancellationToken.None)));

        Assert.Equal(20, added.Select(m => m.Id).Distinct().Count());
        Assert.Equal(20, added.Select(m => m.Uid).Distinct().Count());
        Assert.Equal(20, (await repo.GetInboxAsync(rcv, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Inbox_is_newest_first_and_only_the_receivers()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var other = NewReceiver();
        var oldest = await repo.AddAsync(Draft(rcv, Base), CancellationToken.None);
        var newest = await repo.AddAsync(Draft(rcv, Base.AddHours(2)), CancellationToken.None);
        var middle = await repo.AddAsync(Draft(rcv, Base.AddHours(1)), CancellationToken.None);
        await repo.AddAsync(Draft(other, Base.AddHours(3)), CancellationToken.None);

        var inbox = await repo.GetInboxAsync(rcv, CancellationToken.None);

        Assert.Equal(new[] { newest.Uid, middle.Uid, oldest.Uid }, inbox.Select(m => m.Uid).ToArray());
        Assert.All(inbox, m => Assert.Equal(rcv, m.ReceiverCharId));
    }

    [Fact]
    public async Task Inbox_empty_for_receiver_with_no_mail()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        Assert.Empty(await repo.GetInboxAsync(NewReceiver(), CancellationToken.None));
    }

    [Fact]
    public async Task Inbox_is_capped_at_200_newest()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        for (var i = 0; i < 205; i++)
            await repo.AddAsync(Draft(rcv, Base.AddMinutes(i)), CancellationToken.None);

        var inbox = await repo.GetInboxAsync(rcv, CancellationToken.None);

        Assert.Equal(200, inbox.Count);
        Assert.Equal(Base.AddMinutes(204), inbox[0].SentAt);
        Assert.Equal(Base.AddMinutes(5), inbox[199].SentAt); 
    }

    [Fact]
    public async Task GetByUid_scopes_to_receiver_and_misses_are_null()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var added = await repo.AddAsync(Draft(rcv), CancellationToken.None);

        Assert.NotNull(await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None));
        Assert.Null(await repo.GetByUidAsync(NewReceiver(), added.Uid, CancellationToken.None)); 
        Assert.Null(await repo.GetByUidAsync(rcv, added.Uid + 1, CancellationToken.None));       
    }

    [Fact]
    public async Task CountUnread_counts_only_the_receivers_unread()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        Assert.Equal(0L, await repo.CountUnreadAsync(rcv, CancellationToken.None));

        var a = await repo.AddAsync(Draft(rcv), CancellationToken.None);
        await repo.AddAsync(Draft(rcv), CancellationToken.None);
        await repo.AddAsync(Draft(rcv), CancellationToken.None);
        await repo.AddAsync(Draft(NewReceiver()), CancellationToken.None); 
        Assert.Equal(3L, await repo.CountUnreadAsync(rcv, CancellationToken.None));

        a.IsRead = true;
        await repo.ReplaceAsync(a, CancellationToken.None);
        Assert.Equal(2L, await repo.CountUnreadAsync(rcv, CancellationToken.None));
    }

    [Fact]
    public async Task CountLiveInbox_is_inclusive_of_notBefore_and_counts_read_and_claimed()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        await repo.AddAsync(Draft(rcv, Base.AddDays(-31)), CancellationToken.None);  
        await repo.AddAsync(Draft(rcv, Base.AddMilliseconds(-1)), CancellationToken.None); 
        await repo.AddAsync(Draft(rcv, Base), CancellationToken.None);               
        var fresh = Draft(rcv, Base.AddDays(1));
        fresh.IsRead = true;
        fresh.ItemClaimed = true;
        fresh.GoldClaimed = true;
        await repo.AddAsync(fresh, CancellationToken.None);
        await repo.AddAsync(Draft(NewReceiver(), Base.AddDays(1)), CancellationToken.None); 

        Assert.Equal(2L, await repo.CountLiveInboxAsync(rcv, Base, CancellationToken.None));
        Assert.Equal(1L, await repo.CountLiveInboxAsync(rcv, Base.AddMilliseconds(1), CancellationToken.None));
        Assert.Equal(4L, await repo.CountLiveInboxAsync(rcv, Base.AddDays(-365), CancellationToken.None));
        Assert.Equal(0L, await repo.CountLiveInboxAsync(rcv, Base.AddDays(2), CancellationToken.None));
        Assert.Equal(0L, await repo.CountLiveInboxAsync(NewReceiver(), Base.AddDays(-365), CancellationToken.None));
    }

    [Fact]
    public async Task Expired_mail_is_not_deleted_by_the_server()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var old = await repo.AddAsync(Draft(rcv, Base.AddDays(-400)), CancellationToken.None);

        Assert.Equal(0L, await repo.CountLiveInboxAsync(rcv, Base.AddDays(-30), CancellationToken.None));
        Assert.Single(await repo.GetInboxAsync(rcv, CancellationToken.None));
        Assert.NotNull(await repo.GetByUidAsync(rcv, old.Uid, CancellationToken.None));
    }

    [Fact]
    public async Task CountLiveInbox_accepts_non_utc_notBefore()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        await repo.AddAsync(Draft(rcv, Base), CancellationToken.None);

        
        Assert.Equal(1L, await repo.CountLiveInboxAsync(rcv, Base.ToLocalTime(), CancellationToken.None));
    }

    [Fact]
    public async Task Replace_overwrites_the_whole_row_by_id()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var draft = Draft(rcv);
        draft.AttachedItemId = 501;
        draft.AttachedItemCount = 5;
        draft.AttachedItemCardsJson = "[9]";
        draft.AttachedItemAbilityValue = 7;
        draft.Gold = 100;
        var added = await repo.AddAsync(draft, CancellationToken.None);

        var edit = (await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None))!;
        edit.IsRead = true;
        edit.ItemClaimed = true;
        edit.GoldClaimed = true;
        edit.Title = "Edited";
        edit.AttachedItemCardsJson = null;
        edit.AttachedItemAbilityValue = null;
        edit.Gold = 0;
        await repo.ReplaceAsync(edit, CancellationToken.None);

        var got = (await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None))!;
        Assert.Equal(added.Id, got.Id);
        Assert.Equal(added.Uid, got.Uid);
        Assert.Equal("Edited", got.Title);
        Assert.True(got.IsRead);
        Assert.True(got.ItemClaimed);
        Assert.True(got.GoldClaimed);
        Assert.Null(got.AttachedItemCardsJson);
        Assert.Null(got.AttachedItemAbilityValue);
        Assert.Equal(0L, got.Gold);
        Assert.Equal(501u, got.AttachedItemId);
        Assert.Single(await repo.GetInboxAsync(rcv, CancellationToken.None)); 
    }

    [Fact]
    public async Task Replace_of_unknown_id_is_a_no_op()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var ghost = Draft(rcv);
        ghost.Id = 9_000_000_000_000UL + (ulong)Random.Shared.Next(1, 1_000_000); 
        ghost.Uid = ghost.Id;

        await repo.ReplaceAsync(ghost, CancellationToken.None); 

        Assert.Empty(await repo.GetInboxAsync(rcv, CancellationToken.None));
        Assert.Null(await repo.GetByUidAsync(rcv, ghost.Uid, CancellationToken.None));
    }

    [Fact]
    public async Task Replace_onto_another_mails_uid_violates_the_unique_uid()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var a = await repo.AddAsync(Draft(rcv), CancellationToken.None);
        var b = await repo.AddAsync(Draft(rcv), CancellationToken.None);

        b.Uid = a.Uid;
        await Assert.ThrowsAsync<PostgresException>(() => repo.ReplaceAsync(b, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_once_then_reports_false()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var added = await repo.AddAsync(Draft(rcv), CancellationToken.None);

        Assert.True(await repo.DeleteAsync(rcv, added.Uid, CancellationToken.None));
        Assert.False(await repo.DeleteAsync(rcv, added.Uid, CancellationToken.None));
        Assert.Null(await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_of_another_receivers_mail_is_false_and_keeps_it()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var added = await repo.AddAsync(Draft(rcv), CancellationToken.None);

        Assert.False(await repo.DeleteAsync(NewReceiver(), added.Uid, CancellationToken.None));
        Assert.NotNull(await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_race_exactly_one_caller_wins()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var added = await repo.AddAsync(Draft(rcv), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => repo.DeleteAsync(rcv, added.Uid, CancellationToken.None)));

        Assert.Equal(1, results.Count(r => r));
    }

    
    private async Task<(bool Deleted, bool StillThere)> SpentCase(Action<MailMessage> shape, bool wrongReceiver = false)
    {
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var draft = Draft(rcv);
        shape(draft);
        var added = await repo.AddAsync(draft, CancellationToken.None);
        var caller = wrongReceiver ? NewReceiver() : rcv;
        var deleted = await repo.DeleteIfSpentAsync(caller, added.Uid, CancellationToken.None);
        var still = await repo.GetByUidAsync(rcv, added.Uid, CancellationToken.None) is not null;
        return (deleted, still);
    }

    [Fact]
    public async Task DeleteIfSpent_deletes_a_mail_with_no_attachments()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(_ => { });
        Assert.True(deleted);
        Assert.False(still);
    }

    [Fact]
    public async Task DeleteIfSpent_keeps_a_mail_with_an_unclaimed_item()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(m => { m.AttachedItemId = 501; m.ItemClaimed = false; });
        Assert.False(deleted);
        Assert.True(still);
    }

    [Fact]
    public async Task DeleteIfSpent_deletes_once_the_item_is_claimed()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(m => { m.AttachedItemId = 501; m.ItemClaimed = true; });
        Assert.True(deleted);
        Assert.False(still);
    }

    [Fact]
    public async Task DeleteIfSpent_keeps_a_mail_with_unclaimed_zeny()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(m => { m.Gold = 5000; m.GoldClaimed = false; });
        Assert.False(deleted);
        Assert.True(still);
    }

    [Fact]
    public async Task DeleteIfSpent_deletes_once_the_zeny_is_claimed()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(m => { m.Gold = 5000; m.GoldClaimed = true; });
        Assert.True(deleted);
        Assert.False(still);
    }

    [Fact]
    public async Task DeleteIfSpent_treats_non_positive_gold_as_nothing_pending()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(m => { m.Gold = -10; m.GoldClaimed = false; });
        Assert.True(deleted);
        Assert.False(still);
    }

    [Fact]
    public async Task DeleteIfSpent_needs_both_attachments_spent()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(m =>
        {
            m.AttachedItemId = 501; m.ItemClaimed = true; 
            m.Gold = 5000; m.GoldClaimed = false;          
        });
        Assert.False(deleted);
        Assert.True(still);

        (deleted, still) = await SpentCase(m =>
        {
            m.AttachedItemId = 501; m.ItemClaimed = false; 
            m.Gold = 5000; m.GoldClaimed = true;           
        });
        Assert.False(deleted);
        Assert.True(still);

        (deleted, still) = await SpentCase(m =>
        {
            m.AttachedItemId = 501; m.ItemClaimed = true;
            m.Gold = 5000; m.GoldClaimed = true;
        });
        Assert.True(deleted);
        Assert.False(still);
    }

    [Fact]
    public async Task DeleteIfSpent_of_another_receivers_spent_mail_is_false()
    {
        if (!_fx.Available) return;
        var (deleted, still) = await SpentCase(_ => { }, wrongReceiver: true);
        Assert.False(deleted);
        Assert.True(still);
    }

    [Fact]
    public async Task DeleteIfSpent_unknown_uid_is_false()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        Assert.False(await repo.DeleteIfSpentAsync(NewReceiver(), 0xDEAD_BEEFUL, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteIfSpent_sees_a_claim_written_between_reads()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var draft = Draft(rcv);
        draft.AttachedItemId = 501;
        var added = await repo.AddAsync(draft, CancellationToken.None);

        Assert.False(await repo.DeleteIfSpentAsync(rcv, added.Uid, CancellationToken.None)); 

        added.ItemClaimed = true;
        await repo.ReplaceAsync(added, CancellationToken.None);

        Assert.True(await repo.DeleteIfSpentAsync(rcv, added.Uid, CancellationToken.None));  
    }

    [Fact]
    public async Task DeleteIfSpent_race_exactly_one_caller_wins()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewReceiver();
        var added = await repo.AddAsync(Draft(rcv), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => repo.DeleteIfSpentAsync(rcv, added.Uid, CancellationToken.None)));

        Assert.Equal(1, results.Count(r => r));
    }
}
