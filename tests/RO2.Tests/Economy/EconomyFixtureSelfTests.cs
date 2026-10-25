using RO2.Economy.Domain;
using RO2.Domain.Entities;
using Xunit;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Tests.Economy;

/// <summary>
/// The fixture proves itself before the clusters rely on it: each invariant passes a correct operation and fails a
/// deliberately buggy one, the scheduler really interleaves at repo calls, and the atomic fakes behave atomically.
/// </summary>
public class EconomyFixtureSelfTests
{
    // ---------- invariant A: zeny conservation ----------

    private static async Task Transfer(EconomyFixture fx, uint from, uint to, long amount, bool buggyDoubleCredit = false)
    {
        await fx.CharacterRepo.GetByIdAsync(from);
        Assert.True(fx.Players[from].TryDebit(amount));
        fx.Players[to].Credit(amount);
        if (buggyDoubleCredit) fx.Players[to].Credit(amount);
    }

    [Fact]
    public async Task A_ConservingTransfer_Passes()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(1, gold: 1000);
        fx.AddPlayer(2, gold: 50);

        var (before, after) = await fx.AssertInvariantsAsync(() => Transfer(fx, 1, 2, 300));

        Assert.Equal(1050, before.TotalZeny);
        Assert.Equal(1050, after.TotalZeny);
        Assert.Equal((700L, 350L), (fx.Players[1].Gold, fx.Players[2].Gold));
    }

    [Fact]
    public async Task A_DoubleCredit_Fails()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(1, gold: 1000);
        fx.AddPlayer(2, gold: 50);

        var ex = await Assert.ThrowsAsync<EconomyInvariantViolation>(
            () => fx.AssertInvariantsAsync(() => Transfer(fx, 1, 2, 300, buggyDoubleCredit: true)));
        Assert.Contains("A (zeny conservation)", ex.Message);
        Assert.Contains("moved by 300", ex.Message);
    }

    [Fact]
    public async Task A_DeclaredSink_PassesOnlyWithTheDeclaredDelta()
    {
        // A 50-zeny fee really leaves the economy: the test must say so, otherwise it is a leak.
        var fx = new EconomyFixture();
        var p = fx.AddPlayer(1, gold: 1000);

        await Assert.ThrowsAsync<EconomyInvariantViolation>(
            () => fx.AssertInvariantsAsync(() => { Assert.True(p.TryDebit(50)); return Task.CompletedTask; }));
        // (the failed run did debit); a second fixture proves the declared form
        var fx2 = new EconomyFixture();
        var p2 = fx2.AddPlayer(1, gold: 1000);
        await fx2.AssertInvariantsAsync(() => { Assert.True(p2.TryDebit(50)); return Task.CompletedTask; }, zenyDelta: -50);
    }

    [Fact]
    public async Task A_CountsMailZenyAndAuctionEscrow_AndOfflineWallets()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(1, gold: 100, bankGold: 10);
        fx.AddOfflineCharacter(2, gold: 200);
        fx.GiveMail(2, gold: 30);
        fx.Auction.Seed(new AuctionArticle { SellerCharId = 2, ItemId = 5, Count = 1, ItemUid = 77, TopBid = 40, TopBidderCharId = 1,
            ExpiresAt = DateTime.UtcNow.AddHours(1) });

        Assert.Equal(100 + 10 + 200 + 30 + 40, fx.Snapshot().TotalZeny);

        // Claiming the mail zeny into the offline wallet is a pure move; claiming twice (the dupe) is not.
        await fx.AssertInvariantsAsync(async () =>
        {
            var mail = await fx.MailRepo.TryClaimGoldAsync(2, fx.Mail.Mails.Single().Uid);
            Assert.NotNull(mail);
            await fx.CharacterRepo.UpdateGoldAsync(2, 200 + mail.Gold);
        });
        await Assert.ThrowsAsync<EconomyInvariantViolation>(() => fx.AssertInvariantsAsync(
            () => fx.CharacterRepo.UpdateGoldAsync(2, 200 + 30 + 30)));
    }

    // ---------- invariant D: no negative gold ----------

    [Fact]
    public async Task D_PlayerStateRefusesNegativeGold_AndTheFixtureSeesTheFault()
    {
        var fx = new EconomyFixture();
        var p = fx.AddPlayer(1, gold: 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => p.Gold = -1);
        Assert.False(p.TryDebit(101));
        Assert.Equal(100, p.Gold);

        // A buggy operation that drives the wallet negative surfaces through the run result.
        var result = await fx.RunInterleavedAsync([() => { p.Gold -= 500; return Task.CompletedTask; }]);
        Assert.True(result.HitNegativeWallet);
        Assert.Throws<EconomyInvariantViolation>(() => EconomyInvariants.AssertNoNegativeWalletFault(result));
    }

    [Fact]
    public async Task D_StoredNegativeBalances_Fail()
    {
        var fx = new EconomyFixture();
        fx.AddOfflineCharacter(1, gold: 10);
        EconomyInvariants.AssertNoNegativeGold(fx.Snapshot());

        await fx.CharacterRepo.UpdateGoldAsync(1, -5);
        var ex = Assert.Throws<EconomyInvariantViolation>(() => EconomyInvariants.AssertNoNegativeGold(fx.Snapshot()));
        Assert.Contains("char1 gold -5", ex.Message);

        var fx2 = new EconomyFixture();
        fx2.AddOfflineCharacter(1);
        fx2.GiveMail(1, gold: -1);
        Assert.Throws<EconomyInvariantViolation>(() => EconomyInvariants.AssertNoNegativeGold(fx2.Snapshot()));
    }

    // ---------- invariant B: items ----------

    [Fact]
    public async Task B_MovingAnItemBetweenStores_Passes()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(1);
        var item = fx.GiveItem(1, itemId: 500, count: 3);

        // inventory -> mail attachment under the same uid
        await fx.AssertInvariantsAsync(async () =>
        {
            Assert.True(await fx.InventoryRepo.RemoveItemAsync(item.Id, 1));
            await fx.MailRepo.AddAsync(new MailMessage { ReceiverCharId = 2, AttachedItemId = 500, AttachedItemUid = item.Id, AttachedItemCount = 3 });
        });
    }

    [Fact]
    public async Task B_DuplicatedUid_Fails()
    {
        var fx = new EconomyFixture();
        fx.AddPlayer(1);
        var item = fx.GiveItem(1, 500);

        // Mail the item but forget to take it out of the bag: one uid, two places.
        var ex = await Assert.ThrowsAsync<EconomyInvariantViolation>(() => fx.AssertInvariantsAsync(
            () => fx.MailRepo.AddAsync(new MailMessage { ReceiverCharId = 2, AttachedItemId = 500, AttachedItemUid = item.Id, AttachedItemCount = 1 })));
        Assert.Contains("is in 2 places", ex.Message);
    }

    [Fact]
    public async Task B_LostItem_Fails_UnlessDeclaredConsumed()
    {
        var fx = new EconomyFixture();
        var item = fx.GiveItem(1, 500, 2);
        var ex = await Assert.ThrowsAsync<EconomyInvariantViolation>(
            () => fx.AssertInvariantsAsync(() => fx.InventoryRepo.RemoveItemAsync(item.Id)));
        Assert.Contains("lost", ex.Message);

        var fx2 = new EconomyFixture();
        var item2 = fx2.GiveItem(1, 500, 2);
        await fx2.AssertInvariantsAsync(() => fx2.InventoryRepo.RemoveItemAsync(item2.Id), items: new ItemExpectation
        {
            ConsumedUids = new HashSet<ulong> { item2.Id },
            QuantityDelta = new Dictionary<uint, long> { [500] = -2 },
        });
    }

    [Fact]
    public async Task B_MintedQuantity_Fails_UnlessDeclared()
    {
        var fx = new EconomyFixture();
        var item = fx.GiveItem(1, 500, 2);
        var ex = await Assert.ThrowsAsync<EconomyInvariantViolation>(
            () => fx.AssertInvariantsAsync(() => fx.InventoryRepo.AdjustCountAsync(item.Id, 1, +5)));
        Assert.Contains("template 500 total moved by 5", ex.Message);

        var fx2 = new EconomyFixture();
        var item2 = fx2.GiveItem(1, 500, 2);
        await fx2.AssertInvariantsAsync(() => fx2.InventoryRepo.AdjustCountAsync(item2.Id, 1, +5), items: new ItemExpectation
        {
            QuantityDelta = new Dictionary<uint, long> { [500] = 5 },
        });
    }

    // ---------- invariant C: placement ----------

    [Fact]
    public async Task C_SharedSlot_Fails_AndTheUniqueIndexRefusesIt()
    {
        var fx = new EconomyFixture();
        fx.GiveItem(1, 500, slot: 4);
        fx.Inventory.Seed(new InventoryItem { CharacterId = 1, ItemId = 501, Slot = 4 }); // a legacy collision
        Assert.Throws<EconomyInvariantViolation>(() => EconomyInvariants.AssertUniquePlacement(fx.Snapshot()));
        Assert.False(await fx.InventoryRepo.EnsureIndexesAsync()); // cannot build over a collision

        var fx2 = new EconomyFixture();
        fx2.GiveItem(1, 500, slot: 4);
        Assert.True(await fx2.InventoryRepo.EnsureIndexesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fx2.InventoryRepo.AddItemAsync(new InventoryItem { CharacterId = 1, ItemId = 501, Slot = 4 }));
        // An unassigned slot gets the lowest free one.
        var added = new InventoryItem { CharacterId = 1, ItemId = 502, Slot = -1 };
        await fx2.InventoryRepo.AddItemAsync(added);
        Assert.Equal(0, added.Slot);
        EconomyInvariants.AssertUniquePlacement(fx2.Snapshot());
    }

    // ---------- the fakes honour owner filters and atomicity ----------

    [Fact]
    public async Task InventoryFake_OwnerFilteredAtomicMethods()
    {
        var fx = new EconomyFixture();
        var item = fx.GiveItem(1, 500, 5);

        Assert.Null(await fx.InventoryRepo.AdjustCountAsync(item.Id, 2, -1));    // not the owner
        Assert.Null(await fx.InventoryRepo.AdjustCountAsync(item.Id, 1, -6));    // would go below 0
        Assert.Equal(2, await fx.InventoryRepo.AdjustCountAsync(item.Id, 1, -3));
        Assert.False(await fx.InventoryRepo.RemoveItemAsync(item.Id, 2));
        Assert.False(await fx.InventoryRepo.TryReassignAsync(item.Id, 2, 3, 0, 0)); // stale sender
        Assert.True(await fx.InventoryRepo.TryReassignAsync(item.Id, 1, 3, 0, 9));
        Assert.False(await fx.InventoryRepo.TryReassignAsync(item.Id, 1, 3, 0, 9)); // already moved
        Assert.Equal((3u, (short)9), fx.Inventory.Rows.Select(r => (r.CharacterId, r.Slot)).Single());

        // Rows are copies: mutating a read changes nothing until it is written back.
        var read = await fx.InventoryRepo.GetByIdAsync(item.Id);
        read!.Count = 999;
        Assert.Equal(2, fx.Inventory.Rows.Single().Count);
    }

    [Fact]
    public async Task MailFake_ClaimIsExactlyOnce_AndDeleteIfSpentKeepsUnclaimed()
    {
        var fx = new EconomyFixture();
        var mail = fx.GiveMail(1, gold: 70, attachedItemId: 500, attachedItemUid: 9001, attachedItemCount: 2);

        Assert.Null(await fx.MailRepo.TryClaimGoldAsync(2, mail.Uid));            // not the receiver
        Assert.False(await fx.MailRepo.DeleteIfSpentAsync(1, mail.Uid));          // both attachments waiting
        Assert.NotNull(await fx.MailRepo.TryClaimGoldAsync(1, mail.Uid));
        Assert.Null(await fx.MailRepo.TryClaimGoldAsync(1, mail.Uid));
        Assert.False(await fx.MailRepo.DeleteIfSpentAsync(1, mail.Uid));          // item still waiting
        Assert.NotNull(await fx.MailRepo.TryClaimItemAsync(1, mail.Uid));
        Assert.Null(await fx.MailRepo.TryClaimItemAsync(1, mail.Uid));
        Assert.True(await fx.MailRepo.DeleteIfSpentAsync(1, mail.Uid));
        Assert.Empty(fx.Mail.Mails);
    }

    [Fact]
    public async Task AuctionFake_BidConditionAndSingleClose()
    {
        var fx = new EconomyFixture();
        var now = DateTime.UtcNow;
        var read = fx.Auction.Seed(new AuctionArticle { SellerCharId = 9, ItemId = 5, Count = 1, ItemUid = 77, StartPrice = 100,
            BuyoutPrice = 1000, StartedAt = now, ExpiresAt = now.AddHours(1) });

        var first = await fx.AuctionRepo.TryPlaceBidAsync(read, 1, "A", 1, 150, now);
        Assert.Equal(150, first!.TopBid);
        Assert.Null(await fx.AuctionRepo.TryPlaceBidAsync(read, 2, "B", 2, 150, now)); // read a bid that is gone
        Assert.NotNull(await fx.AuctionRepo.TryAcceptTopBidAsync(first, 9, now));
        Assert.Null(await fx.AuctionRepo.TryCancelAsync(read.Uid, 9, now));            // already closed
        Assert.Equal(AuctionArticleState.Sold, fx.Auction.Articles.Single().State);
    }

    // ---------- the scheduler ----------

    // Read, add, write back: two repo calls, so the two operations can interleave between them.
    private static Func<Task> NonAtomicIncrement(EconomyFixture fx, ulong itemUid) => async () =>
    {
        var row = await fx.InventoryRepo.GetByIdAsync(itemUid);
        row!.Count += 1;
        await fx.InventoryRepo.UpdateItemAsync(row);
    };

    private static Func<Task> AtomicIncrement(EconomyFixture fx, ulong itemUid) =>
        async () => await fx.InventoryRepo.AdjustCountAsync(itemUid, 1, +1);

    [Fact]
    public async Task Scheduler_OrdersTheInterleavingAtEveryRepoCall()
    {
        var fx = new EconomyFixture();
        var item = fx.GiveItem(1, 500, 0);

        // Both read before either writes: the classic lost update.
        var lost = await fx.RunInterleavedAsync([NonAtomicIncrement(fx, item.Id), NonAtomicIncrement(fx, item.Id)], "0101");
        lost.ThrowIfFaulted();
        Assert.Equal(1, fx.Inventory.Rows.Single().Count);
        Assert.Equal(["P0:Inventory.GetById", "P1:Inventory.GetById", "P0:Inventory.Update", "P1:Inventory.Update"], lost.Trace);

        // One after the other: both land.
        var fx2 = new EconomyFixture();
        var item2 = fx2.GiveItem(1, 500, 0);
        (await fx2.RunInterleavedAsync([NonAtomicIncrement(fx2, item2.Id), NonAtomicIncrement(fx2, item2.Id)], "0011")).ThrowIfFaulted();
        Assert.Equal(2, fx2.Inventory.Rows.Single().Count);
    }

    [Fact]
    public async Task Scheduler_ExploresEveryInterleaving_AndFindsTheLostUpdate()
    {
        // 2 operations x 2 repo calls each = C(4,2) = 6 schedules.
        EconomyFixture Make() { var f = new EconomyFixture(); f.GiveItem(1, 500, 0); return f; }

        var atomic = await RepoScheduler.ExploreAllAsync(Make,
            f => [AtomicIncrement(f, f.Inventory.Rows.Single().Id), AtomicIncrement(f, f.Inventory.Rows.Single().Id)],
            (f, r) => { r.ThrowIfFaulted(); Assert.Equal(2, f.Inventory.Rows.Single().Count); return Task.CompletedTask; });
        Assert.Equal(new ExploreSummary(2, true), atomic); // one point each: 2 schedules

        var racy = await Assert.ThrowsAsync<InvalidOperationException>(() => RepoScheduler.ExploreAllAsync(Make,
            f => [NonAtomicIncrement(f, f.Inventory.Rows.Single().Id), NonAtomicIncrement(f, f.Inventory.Rows.Single().Id)],
            (f, r) => { r.ThrowIfFaulted(); Assert.Equal(2, f.Inventory.Rows.Single().Count); return Task.CompletedTask; }));
        Assert.Contains("Interleaving [", racy.Message);

        var count = 0;
        var all = await RepoScheduler.ExploreAllAsync(Make,
            f => [NonAtomicIncrement(f, f.Inventory.Rows.Single().Id), NonAtomicIncrement(f, f.Inventory.Rows.Single().Id)],
            (_, _) => { count++; return Task.CompletedTask; });
        Assert.Equal(new ExploreSummary(6, true), all);
        Assert.Equal(6, count);
    }

    [Fact]
    public async Task Scheduler_ExplorationOfAMailZenyClaim_ShowsTheDupe_AndTheAtomicFix()
    {
        // The end-to-end shape cluster tests use. 500 zeny waits in a mail; two claims race.
        EconomySnapshot? before = null;
        EconomyFixture Make()
        {
            var f = new EconomyFixture();
            f.AddPlayer(1, gold: 1000);
            f.GiveMail(1, gold: 500);
            return f;
        }
        Task Verify(EconomyFixture f, InterleaveResult r)
        {
            r.ThrowIfFaulted();
            EconomyInvariants.AssertNoNegativeWalletFault(r);
            EconomyFixture.CheckAll(before!, f.Snapshot());
            return Task.CompletedTask;
        }

        // Buggy: read the mail, then write it back claimed. Two readers both see it unclaimed.
        Func<Task> BuggyClaim(EconomyFixture f) => async () =>
        {
            var mail = await f.MailRepo.GetByUidAsync(1, f.Mail.Mails.Single().Uid);
            if (mail is null || mail.GoldClaimed) return;
            mail.GoldClaimed = true;
            await f.MailRepo.ReplaceAsync(mail);
            f.Players[1].Credit(mail.Gold);
        };
        var buggy = await Assert.ThrowsAsync<InvalidOperationException>(() => RepoScheduler.ExploreAllAsync(Make,
            f => { before = f.Snapshot(); return [BuggyClaim(f), BuggyClaim(f)]; }, Verify));
        Assert.Contains("A (zeny conservation)", buggy.Message);

        // Fixed: one guarded flip decides the winner.
        Func<Task> AtomicClaim(EconomyFixture f) => async () =>
        {
            var won = await f.MailRepo.TryClaimGoldAsync(1, f.Mail.Mails.Single().Uid);
            if (won is not null) f.Players[1].Credit(won.Gold);
        };
        var fixedRun = await RepoScheduler.ExploreAllAsync(Make,
            f => { before = f.Snapshot(); return [AtomicClaim(f), AtomicClaim(f)]; }, Verify);
        Assert.True(fixedRun.Exhausted);
        Assert.True(fixedRun.Runs >= 2);
    }

    [Fact]
    public async Task Scheduler_ParticipantBlockedOnALock_IsSkippedUntilItIsReleased()
    {
        var fx = new EconomyFixture();
        var item = fx.GiveItem(1, 500, 1);
        using var gate = new SemaphoreSlim(1, 1);   // stands in for the per-character inventory gate
        Func<Task> Gated() => async () =>
        {
            await gate.WaitAsync();
            try { await fx.InventoryRepo.GetByIdAsync(item.Id); }
            finally { gate.Release(); }
        };
        fx.Scheduler.BlockedAfter = TimeSpan.FromMilliseconds(40);

        // Participant 1 asks to go first but is stuck behind participant 0's gate, so 0 is advanced instead.
        var result = await fx.RunInterleavedAsync([Gated(), Gated()], "1");
        result.ThrowIfFaulted();
        Assert.Equal(["P0:Inventory.GetById", "P1:Inventory.GetById"], result.Trace);
    }

    [Fact]
    public async Task Scheduler_CapturesAnOperationException_WithoutHangingTheOthers()
    {
        var fx = new EconomyFixture();
        var result = await fx.RunInterleavedAsync([
            () => throw new InvalidOperationException("boom"),
            async () => await fx.CharacterRepo.GetByIdAsync(1),
        ]);
        Assert.IsType<InvalidOperationException>(result.Errors[0]);
        Assert.Null(result.Errors[1]);
        Assert.Equal(["P1:Character.GetById"], result.Trace);
    }
}
