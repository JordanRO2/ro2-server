using System.Reflection;
using RO2.BuildingBlocks;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Economy;

/// <summary>
/// Pins the settlement order of <see cref="AuctionApplicationService"/> over the in-memory auction repository and
/// recording ports: a failing wallet write must not skip the refund, award and mail steps that follow the durable
/// repository change.
/// </summary>
public sealed class AuctionApplicationServiceTests
{
    private const uint SellerId = 1, BidderId = 2, OldBidderId = 3;
    private const uint TemplateId = 5001;

    private sealed class Wallet(long gold) : IWallet
    {
        public long Gold { get; private set; } = gold;
        public long BankGold => 0;
        public bool TryDebit(long amount) { if (amount < 0 || Gold < amount) return false; Gold -= amount; return true; }
        public long DebitUpTo(long amount) => throw new NotSupportedException();
        public void Credit(long amount) => Gold += amount;
    }

    private sealed class Mail : IEconomyMailPort
    {
        public List<(uint Receiver, byte Kind, long Gold, bool Item)> Sent { get; } = [];
        public Task SendAuctionMailAsync(uint receiverCharId, string receiverName, byte kind, long auctionPrice, long gold,
            ItemStackSnapshot? item, uint kindItemId, DateTime sentAtUtc)
        {
            Sent.Add((receiverCharId, kind, gold, item is not null));
            return Task.CompletedTask;
        }
        public Task NotifyNewMailAsync(uint charId) => Task.CompletedTask;
    }

    private sealed class Notifier : IAuctionNotifier
    {
        public Task SendSystemMessageAsync(uint charId, uint type, ulong articleUid, uint itemId, long amount) => Task.CompletedTask;
    }

    private sealed class Log : IAuctionLog
    {
        public int WalletNotPersistedCount { get; private set; }
        public void BidPlaced(ulong articleUid, string bidderName, long bid, long previousPrice, uint previousBidder, bool bought) { }
        public void BidRefused(ulong articleUid, ulong bid, string bidderName, ushort code, string why) { }
        public void Bought(ulong articleUid, string buyerName, long price, string sellerName, long proceeds) { }
        public void BuyRefused(ulong articleUid, string buyerName, string why) { }
        public void BuyPriceMismatch(ulong articleUid, string buyerName, ulong offered, long price) { }
        public void Cancelled(ulong articleUid, string sellerName, uint itemId, int count) { }
        public void AcceptRefused(ulong articleUid, string sellerName, string why) { }
        public void Awarded(ulong articleUid, string bidderName, long bid, string sellerName, long proceeds) { }
        public void RefundMailFailed(ulong articleUid, long bid, uint bidderCharId, Exception ex) { }
        public void WalletNotPersisted(ulong articleUid, uint charId, long gold, Exception ex) => WalletNotPersistedCount++;
        public void ExpirySettleFailed(ulong articleUid, uint itemId, int count, uint sellerCharId, uint topBidderCharId, Exception ex) { }
    }

    private static (AuctionApplicationService Svc, FakeAuctionRepository Repo, Mail Mail, Log Log, AuctionArticle Article) Build(
        long topBid = 0, uint topBidder = 0)
    {
        var repo = new FakeAuctionRepository(new RepoScheduler());
        var article = repo.Seed(new AuctionArticle
        {
            Uid = 104_000_000_000_000_001UL, SellerCharId = SellerId, SellerName = "Seller", ItemId = TemplateId, ItemUid = 777,
            Count = 1, StartPrice = 100, BuyoutPrice = 5000, StartedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(24),
            State = AuctionArticleState.Active, TopBid = topBid, TopBidderCharId = topBidder, TopBidderName = topBidder == 0 ? "" : "Old",
        });
        var mail = new Mail();
        var log = new Log();
        return (new AuctionApplicationService(repo, mail, new Notifier(), 0.95, log), repo, mail, log, article);
    }

    private static Func<Task> Throwing => () => throw new InvalidOperationException("db down");

    [Fact]
    public async Task Bid_WhenTheWalletWriteThrows_StillRefundsTheDisplacedBidder()
    {
        var (svc, repo, mail, log, a) = Build(topBid: 200, topBidder: OldBidderId);
        var wallet = new Wallet(5000);

        var code = await svc.PlaceBidAsync(new AuctionBidCommand(BidderId, "Bidder", 20, a.Uid, a.ItemUid, a.ItemId, 1000), wallet, Throwing);

        Assert.Equal(AuctionCodes.ResultSuccess, code);
        Assert.Equal(4000, wallet.Gold);
        Assert.Equal(BidderId, repo.Articles.Single().TopBidderCharId);
        Assert.Contains(mail.Sent, m => m.Receiver == OldBidderId && m.Gold == 200 && !m.Item);   // the escrow comes back
        Assert.Equal(1, log.WalletNotPersistedCount);
    }

    [Fact]
    public async Task InstantBid_WhenTheWalletWriteThrows_StillMailsTheItemAndTheProceeds()
    {
        var (svc, repo, mail, _, a) = Build();
        var wallet = new Wallet(9000);

        var code = await svc.PlaceBidAsync(new AuctionBidCommand(BidderId, "Bidder", 20, a.Uid, a.ItemUid, a.ItemId, 5000), wallet, Throwing);

        Assert.Equal(AuctionCodes.ResultSuccess, code);
        Assert.Equal(AuctionArticleState.Sold, repo.Articles.Single().State);
        Assert.Contains(mail.Sent, m => m.Receiver == BidderId && m.Item);
        Assert.Contains(mail.Sent, m => m.Receiver == SellerId && m.Gold > 0);
    }

    [Fact]
    public async Task Buy_WhenTheWalletWriteThrows_StillMailsTheItemToTheBuyerAndPaysTheSeller()
    {
        var (svc, repo, mail, log, a) = Build();
        var wallet = new Wallet(9000);

        var (result, sold) = await svc.BuyAsync(new AuctionBuyCommand(BidderId, "Buyer", a.Uid, 5000), wallet, Throwing);

        Assert.Equal(BuyResult.Bought, result);
        Assert.NotNull(sold);
        Assert.Equal(4000, wallet.Gold);
        Assert.Equal(AuctionArticleState.Sold, repo.Articles.Single().State);
        Assert.Contains(mail.Sent, m => m.Receiver == BidderId && m.Item);
        Assert.Contains(mail.Sent, m => m.Receiver == SellerId && m.Gold > 0);
        Assert.Equal(1, log.WalletNotPersistedCount);
    }

    private sealed class EscrowThatCannotRestore : IInventoryEscrow
    {
        public Task<(ItemStackSnapshot? Taken, int CountLeft)> TakeAsync(uint characterId, ulong itemUid, int count)
            => Task.FromResult<(ItemStackSnapshot?, int)>((new ItemStackSnapshot { Id = itemUid, ItemId = TemplateId, Count = count }, 0));
        public Task RestoreAsync(uint characterId, ItemStackSnapshot taken, int countLeft, int count)
            => throw new InvalidOperationException("slot taken");
        public Task<ulong> NextItemUidAsync() => Task.FromResult(1UL);
    }

    [Fact]
    public async Task List_WhenTheStoreAndTheRestoreBothThrow_StillHandsTheFeeBack()
    {
        var (svc, repo, _, _, _) = Build();
        var failing = DispatchProxy.Create<IAuctionRepository, AuctionClusterATests.ThrowOnInsert>();
        ((AuctionClusterATests.ThrowOnInsert)(object)failing).Inner = repo;
        var svcFailing = new AuctionApplicationService(failing, new Mail(), new Notifier(), 0.95, new Log());
        var wallet = new Wallet(1000);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svcFailing.ListAsync(
            new AuctionListCommand(SellerId, "Seller", 10, 55, 1, 100, 1000, 24, Fee: 40), wallet, new EscrowThatCannotRestore()));

        Assert.Equal(1000, wallet.Gold);
    }

    [Fact]
    public async Task Restore_OfAWholeStackWhoseSlotWasTakenMeanwhile_KeepsTheIdInAFreeSlot()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(SellerId, gold: 1000);
        await fx.InventoryRepo.EnsureIndexesAsync();   // the unique placement index is what rejects the reused slot
        var item = fx.GiveItem(SellerId, TemplateId, 3);
        var escrow = new InventoryEscrowAdapter(fx.InventoryRepo);

        var (taken, left) = await escrow.TakeAsync(SellerId, item.Id, 3);
        Assert.NotNull(taken);
        var intruder = fx.GiveItem(SellerId, 9999, 1, slot: item.Slot);   // loot lands in the freed slot
        await escrow.RestoreAsync(SellerId, taken, left, 3);

        var back = fx.Inventory.Rows.Single(r => r.Id == item.Id);
        Assert.Equal(3, back.Count);
        Assert.NotEqual(item.Slot, back.Slot);
        Assert.Equal(intruder.Slot, fx.Inventory.Rows.Single(r => r.Id == intruder.Id).Slot);
    }
}
