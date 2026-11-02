using RO2.Mails.Application;
using RO2.Mails.Domain;
using RO2.Tests.Economy;
using Xunit;

namespace RO2.Tests.MailCharacterization;

/// <summary>The 1457 / 1397 / 1398 / 1394 store-side use cases over the in-memory mail store.</summary>
public class MailStoreUseCaseTests
{
    private const uint Me = 1;
    private const uint Other = 2;
    private const byte AuctionKind = 10;   // an auction proceeds mail (zeny)

    private static readonly Func<byte, bool> IsAuction = k => k == AuctionKind;

    private static FakeMailRepository Repo() => new(new RepoScheduler());

    private static MailMessage Seed(FakeMailRepository repo, uint receiver = Me, long gold = 0, bool read = false, byte kind = 0, uint item = 0)
        => repo.Seed(new MailMessage { ReceiverCharId = receiver, Gold = gold, IsRead = read, Kind = kind, AttachedItemId = item, SentAt = DateTime.UtcNow });

    [Fact]
    public async Task Gold_claim_grants_once_and_a_missing_foreign_or_empty_mail_grants_nothing()
    {
        var repo = Repo();
        var uc = new ClaimMailGoldUseCase(repo);
        var mail = Seed(repo, gold: 500);
        var foreign = Seed(repo, receiver: Other, gold: 500);
        var empty = Seed(repo);

        var first = await uc.ClaimAsync(Me, mail.Uid, IsAuction);
        var again = await uc.ClaimAsync(Me, mail.Uid, IsAuction);
        Assert.Equal(500, first.Granted);
        Assert.True(first.Mail!.GoldClaimed);
        Assert.Equal(0, again.Granted);
        Assert.NotNull(again.Mail);

        var foreignClaim = await uc.ClaimAsync(Me, foreign.Uid, IsAuction);
        Assert.Equal((0L, (MailMessage?)null), (foreignClaim.Granted, foreignClaim.Mail));
        var emptyClaim = await uc.ClaimAsync(Me, empty.Uid, IsAuction);
        Assert.Equal(0, emptyClaim.Granted);
        Assert.NotNull(emptyClaim.Mail);
        Assert.Null((await uc.ClaimAsync(Me, 12345, IsAuction)).Mail);
        Assert.False(repo.Mails.Single(m => m.Uid == foreign.Uid).GoldClaimed);
    }

    [Fact]
    public async Task Gold_claim_removes_a_spent_auction_mail_but_keeps_a_player_mail_or_one_with_an_item_left()
    {
        var repo = Repo();
        var uc = new ClaimMailGoldUseCase(repo);
        var auction = Seed(repo, gold: 10, kind: AuctionKind);
        var player = Seed(repo, gold: 10);
        var auctionWithItem = Seed(repo, gold: 10, kind: AuctionKind, item: 501);

        await uc.ClaimAsync(Me, auction.Uid, IsAuction);
        await uc.ClaimAsync(Me, player.Uid, IsAuction);
        await uc.ClaimAsync(Me, auctionWithItem.Uid, IsAuction);

        Assert.DoesNotContain(repo.Mails, m => m.Uid == auction.Uid);
        Assert.Contains(repo.Mails, m => m.Uid == player.Uid);
        Assert.Contains(repo.Mails, m => m.Uid == auctionWithItem.Uid);
    }

    [Fact]
    public async Task Gold_claim_survives_a_failing_removal_and_reports_it()
    {
        var repo = new ThrowingDeleteIfSpent(new RepoScheduler());
        var uc = new ClaimMailGoldUseCase(repo);
        var mail = repo.Seed(new MailMessage { ReceiverCharId = Me, Gold = 10, Kind = AuctionKind, SentAt = DateTime.UtcNow });
        MailMessage? reported = null;

        var claim = await uc.ClaimAsync(Me, mail.Uid, IsAuction, (m, _) => reported = m);

        Assert.Equal(10, claim.Granted);
        Assert.Equal(mail.Uid, reported!.Uid);
    }

    [Fact]
    public async Task Read_marks_read_and_writes_only_on_the_transition_and_hides_foreign_mail()
    {
        var repo = Repo();
        var uc = new ReadMailUseCase(repo);
        var unread = Seed(repo);
        var already = Seed(repo, read: true);
        var theirs = Seed(repo, receiver: Other);
        var expired = repo.Seed(new MailMessage { ReceiverCharId = Me, SentAt = DateTime.UtcNow.AddDays(-90) });

        Assert.True((await uc.ReadAsync(Me, unread.Uid))!.IsRead);
        Assert.True((await uc.ReadAsync(Me, already.Uid))!.IsRead);
        Assert.NotNull(await uc.ReadAsync(Me, expired.Uid));
        Assert.Null(await uc.ReadAsync(Me, theirs.Uid));
        Assert.Null(await uc.ReadAsync(Me, 12345));
        Assert.True(repo.Mails.Single(m => m.Uid == unread.Uid).IsRead);
        Assert.False(repo.Mails.Single(m => m.Uid == theirs.Uid).IsRead);
    }

    [Fact]
    public async Task Delete_is_one_repository_call_that_ignores_unclaimed_attachments_and_reports_nothing_there()
    {
        var repo = Repo();
        var uc = new DeleteMailUseCase(repo);
        var loaded = Seed(repo, gold: 500, item: 501);
        var theirs = Seed(repo, receiver: Other);

        Assert.True(await uc.DeleteAsync(Me, loaded.Uid));
        Assert.False(await uc.DeleteAsync(Me, loaded.Uid));
        Assert.False(await uc.DeleteAsync(Me, theirs.Uid));
        Assert.False(await uc.DeleteAsync(Me, 12345));
        Assert.Contains(repo.Mails, m => m.Uid == theirs.Uid);
    }

    [Fact]
    public async Task List_projects_the_readers_inbox_newest_first()
    {
        var repo = Repo();
        var uc = new ListInboxUseCase(repo);
        var old = repo.Seed(new MailMessage { ReceiverCharId = Me, Gold = 5, GoldClaimed = true, SentAt = DateTime.UtcNow.AddDays(-2) });
        var recent = repo.Seed(new MailMessage { ReceiverCharId = Me, Gold = 7, SentAt = DateTime.UtcNow });
        Seed(repo, receiver: Other, gold: 9);

        var rows = await uc.ListAsync(Me);

        Assert.Equal([recent.Uid, old.Uid], rows.Select(r => r.Uid));
        Assert.Equal([7L, 0L], rows.Select(r => r.Gold));   // the claimed zeny is hidden
        Assert.Empty(await uc.ListAsync(99));
    }

    private sealed class ThrowingDeleteIfSpent(RepoScheduler s) : IMailRepository
    {
        private readonly FakeMailRepository _inner = new(s);
        public MailMessage Seed(MailMessage m) => _inner.Seed(m);
        public Task<MailMessage> AddAsync(MailMessage draft, CancellationToken ct = default) => _inner.AddAsync(draft, ct);
        public Task<IReadOnlyList<MailMessage>> GetInboxAsync(uint r, CancellationToken ct = default) => _inner.GetInboxAsync(r, ct);
        public Task<MailMessage?> GetByUidAsync(uint r, ulong uid, CancellationToken ct = default) => _inner.GetByUidAsync(r, uid, ct);
        public Task<long> CountUnreadAsync(uint r, CancellationToken ct = default) => _inner.CountUnreadAsync(r, ct);
        public Task<long> CountLiveInboxAsync(uint r, DateTime nb, CancellationToken ct = default) => _inner.CountLiveInboxAsync(r, nb, ct);
        public Task ReplaceAsync(MailMessage m, CancellationToken ct = default) => _inner.ReplaceAsync(m, ct);
        public Task<bool> DeleteAsync(uint r, ulong uid, CancellationToken ct = default) => _inner.DeleteAsync(r, uid, ct);
        public Task<MailMessage?> TryClaimGoldAsync(uint r, ulong uid, CancellationToken ct = default) => _inner.TryClaimGoldAsync(r, uid, ct);
        public Task<bool> DeleteIfSpentAsync(uint r, ulong uid, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }
}
