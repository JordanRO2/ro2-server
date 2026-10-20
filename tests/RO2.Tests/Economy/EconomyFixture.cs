using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Server.Zone.Models;

namespace RO2.Tests.Economy;

/// <summary>
/// Shared harness for the economy hardening tests: in-memory fakes of the inventory, character, mail and auction
/// repositories on one <see cref="RepoScheduler"/> (so any repo call can be a deterministic interleaving point),
/// a set of live <see cref="PlayerState"/>s, and the conservation invariants that are the oracle for the whole
/// dupe/mint class:
/// <list type="bullet">
/// <item><b>A</b> zeny conservation: wallets (live where online, else the stored row) + bank + unclaimed mail zeny
/// + auction bid escrow changes only by the delta the test declares (<see cref="EconomyInvariants.AssertZenyConserved"/>).</item>
/// <item><b>B</b> items: no uid in two places, none lost, none minted, and per-template quantity conserved, apart
/// from what the test declares (<see cref="EconomyInvariants.AssertItemsConserved"/>).</item>
/// <item><b>C</b> unique placement: no two rows share (character, storage, slot &gt;= 0)
/// (<see cref="EconomyInvariants.AssertUniquePlacement"/>).</item>
/// <item><b>D</b> no negative gold anywhere: stored or live, and no operation tripped the throwing
/// <c>PlayerState.Gold</c> setter (<see cref="EconomyInvariants.AssertNoNegativeGold"/>).</item>
/// </list>
/// Invariants are checked at quiescence (between operations): zeny/items legitimately sit in no store for the
/// instant between two writes of one operation.
/// </summary>
public sealed class EconomyFixture
{
    private readonly Dictionary<uint, PlayerState> _players = [];

    public EconomyFixture()
    {
        Inventory = new FakeInventoryRepository(Scheduler);
        Characters = new FakeCharacterRepository(Scheduler);
        Mail = new FakeMailRepository(Scheduler);
        Auction = new FakeAuctionRepository(Scheduler);
    }

    public RepoScheduler Scheduler { get; } = new();
    public FakeInventoryRepository Inventory { get; }
    public FakeCharacterRepository Characters { get; }
    public FakeMailRepository Mail { get; }
    public FakeAuctionRepository Auction { get; }

    /// <summary>Repositories as the interfaces handlers and services take.</summary>
    public IInventoryRepository InventoryRepo => Inventory;
    public ICharacterRepository CharacterRepo => Characters;
    public IMailRepository MailRepo => Mail;
    public IAuctionRepository AuctionRepo => Auction;

    /// <summary>The live players, by character id.</summary>
    public IReadOnlyDictionary<uint, PlayerState> Players => _players;

    // ---------- setup (no scheduler points) ----------

    /// <summary>Adds an ONLINE character: a stored row plus a live <see cref="PlayerState"/> that holds the wallet
    /// (the live state is authoritative while the player is online).</summary>
    public PlayerState AddPlayer(uint characterId, long gold = 0, long bankGold = 0, string? name = null)
    {
        var character = AddOfflineCharacter(characterId, gold, bankGold, name);
        var player = new PlayerState
        {
            CharacterId = characterId,
            AccountId = character.AccountId,
            SessionId = characterId,
            Name = character.Name,
            Gold = gold,
            BankGold = bankGold,
        };
        _players[characterId] = player;
        return player;
    }

    /// <summary>Adds a character that is not online: only the stored row, whose wallet is then the balance.</summary>
    public Character AddOfflineCharacter(uint characterId, long gold = 0, long bankGold = 0, string? name = null)
    {
        var character = new Character
        {
            Id = characterId,
            AccountId = characterId,
            Name = name ?? $"Char{characterId}",
            Gold = gold,
            BankGold = bankGold,
        };
        Characters.Seed(character);
        return character;
    }

    /// <summary>Drops the live state, as a logout does after its flush: the stored row becomes the balance again
    /// (so a test that logs out without saving shows the loss).</summary>
    public void RemovePlayer(uint characterId) => _players.Remove(characterId);

    /// <summary>Seeds one inventory row (a fresh uid, the lowest free slot of that storage when <paramref name="slot"/>
    /// is negative) and returns the stored copy.</summary>
    public InventoryItem GiveItem(uint characterId, uint itemId, int count = 1, byte storageType = 0, short slot = -1)
    {
        if (slot < 0)
        {
            var used = Inventory.Rows.Where(r => r.CharacterId == characterId && r.StorageType == storageType)
                .Select(r => (int)r.Slot).ToHashSet();
            slot = 0;
            while (used.Contains(slot)) slot++;
        }
        var id = Inventory.Seed(new InventoryItem
        {
            CharacterId = characterId, ItemId = itemId, Count = count, StorageType = storageType, Slot = slot,
        });
        return Inventory.Rows.Single(r => r.Id == id);
    }

    /// <summary>Seeds a mail (<paramref name="gold"/> zeny and/or an attached item), returns the stored copy.</summary>
    public MailMessage GiveMail(uint receiverCharId, long gold = 0, uint attachedItemId = 0, ulong attachedItemUid = 0,
        int attachedItemCount = 1, uint senderCharId = 0)
        => Mail.Seed(new MailMessage
        {
            ReceiverCharId = receiverCharId,
            SenderCharId = senderCharId,
            Gold = gold,
            AttachedItemId = attachedItemId,
            AttachedItemUid = attachedItemUid,
            AttachedItemCount = attachedItemId == 0 ? 0 : attachedItemCount,
        });

    // ---------- observation ----------

    /// <summary>The balance of a character: the live wallet when online, else the stored row.</summary>
    public (long Gold, long BankGold) WalletOf(uint characterId)
    {
        if (_players.TryGetValue(characterId, out var p)) return (p.Gold, p.BankGold);
        var row = Characters.Rows.SingleOrDefault(c => c.Id == characterId)
                  ?? throw new InvalidOperationException($"no character {characterId} in the fixture");
        return (row.Gold, row.BankGold);
    }

    /// <summary>Everything the invariants look at, read without scheduler points.</summary>
    public EconomySnapshot Snapshot()
    {
        var ids = Characters.Rows.Select(c => c.Id).Concat(_players.Keys).Distinct().Order().ToList();
        var gold = new SortedDictionary<uint, long>();
        var bank = new SortedDictionary<uint, long>();
        foreach (var id in ids) (gold[id], bank[id]) = WalletOf(id);

        var mails = Mail.Mails;
        var articles = Auction.Articles;
        var rows = Inventory.Rows;

        var items = new List<ItemLocation>();
        items.AddRange(rows.Select(r => new ItemLocation(r.Id, r.ItemId, r.Count, $"inventory:char{r.CharacterId}/st{r.StorageType}/slot{r.Slot}")));
        items.AddRange(mails.Where(m => m.AttachedItemId != 0 && !m.ItemClaimed)
            .Select(m => new ItemLocation(m.AttachedItemUid, m.AttachedItemId, m.AttachedItemCount, $"mail:{m.Uid:X}")));
        items.AddRange(articles.Where(a => a.State == AuctionArticleState.Active)
            .Select(a => new ItemLocation(a.ItemUid, a.ItemId, a.Count, $"auction:{a.Uid:X}")));

        return new EconomySnapshot(
            gold, bank,
            MailZeny: mails.Where(m => m.Gold > 0 && !m.GoldClaimed).Sum(m => m.Gold),
            MailZenyMin: mails.Select(m => m.Gold).DefaultIfEmpty(0).Min(),
            AuctionEscrow: articles.Where(a => a.State == AuctionArticleState.Active && a.TopBidderCharId != 0).Sum(a => a.TopBid),
            AuctionEscrowMin: articles.Select(a => a.TopBid).DefaultIfEmpty(0).Min(),
            items,
            Placements: rows.Where(r => r.Slot >= 0).Select(r => (r.CharacterId, r.StorageType, r.Slot, r.Id)).ToList());
    }

    // ---------- running ----------

    /// <summary>
    /// Snapshot, run <paramref name="operation"/> to completion, snapshot, then check A (zeny moved by exactly
    /// <paramref name="zenyDelta"/>: negative for a fee or NPC purchase that really removes zeny, positive for a
    /// mint the test expects), B (per <paramref name="items"/>), C and D. Returns the two snapshots.
    /// </summary>
    public async Task<(EconomySnapshot Before, EconomySnapshot After)> AssertInvariantsAsync(
        Func<Task> operation, long zenyDelta = 0, ItemExpectation? items = null)
    {
        var before = Snapshot();
        await operation();
        var after = Snapshot();
        CheckAll(before, after, zenyDelta, items);
        return (before, after);
    }

    /// <summary>Checks A, B, C, D between two snapshots (see <see cref="AssertInvariantsAsync"/>).</summary>
    public static void CheckAll(EconomySnapshot before, EconomySnapshot after, long zenyDelta = 0, ItemExpectation? items = null)
    {
        EconomyInvariants.AssertZenyConserved(before, after, zenyDelta);
        EconomyInvariants.AssertItemsConserved(before, after, items);
        EconomyInvariants.AssertUniquePlacement(after);
        EconomyInvariants.AssertNoNegativeGold(after);
    }

    /// <summary>Runs the operations as interleaved participants (see <see cref="RepoScheduler.RunAsync(IReadOnlyList{Func{Task}}, string)"/>).</summary>
    public Task<InterleaveResult> RunInterleavedAsync(IReadOnlyList<Func<Task>> operations, string schedule = "")
        => Scheduler.RunAsync(operations, schedule);
}

/// <summary>One item stack found in a store: its uid, template, quantity and where it sits.</summary>
public readonly record struct ItemLocation(ulong Uid, uint ItemId, int Count, string Where);

/// <summary>The economy state the invariants compare, as taken by <see cref="EconomyFixture.Snapshot"/>.</summary>
/// <param name="Gold">Per character, the wallet (live when online, else stored).</param>
/// <param name="Bank">Per character, the bank zeny.</param>
/// <param name="MailZeny">Zeny attached to mails and not yet claimed.</param>
/// <param name="AuctionEscrow">The top bids held by Active articles (taken from the bidders' wallets).</param>
/// <param name="Items">Every item stack in an inventory row, an unclaimed mail attachment or an Active article.</param>
/// <param name="Placements">(character, storage, slot, row id) of every inventory row with a real slot.</param>
public sealed record EconomySnapshot(
    IReadOnlyDictionary<uint, long> Gold, IReadOnlyDictionary<uint, long> Bank,
    long MailZeny, long MailZenyMin, long AuctionEscrow, long AuctionEscrowMin,
    IReadOnlyList<ItemLocation> Items,
    IReadOnlyList<(uint CharacterId, byte StorageType, short Slot, ulong RowId)> Placements)
{
    /// <summary>Invariant A's quantity: every wallet and bank + unclaimed mail zeny + auction escrow.</summary>
    public long TotalZeny => Gold.Values.Sum() + Bank.Values.Sum() + MailZeny + AuctionEscrow;
}

/// <summary>What a test declares about the items an operation may legitimately change (invariant B).</summary>
public sealed class ItemExpectation
{
    public static readonly ItemExpectation None = new();

    /// <summary>Uids that may disappear (consumed by a craft, a refine, a sale to an NPC).</summary>
    public IReadOnlySet<ulong> ConsumedUids { get; init; } = new HashSet<ulong>();

    /// <summary>Uids that may appear (a split stack, a crafted item, an NPC purchase).</summary>
    public IReadOnlySet<ulong> CreatedUids { get; init; } = new HashSet<ulong>();

    /// <summary>Expected change of the total quantity per item template (default 0 for every other template).</summary>
    public IReadOnlyDictionary<uint, long> QuantityDelta { get; init; } = new Dictionary<uint, long>();
}

/// <summary>An invariant of the economy was violated; the message says which and how.</summary>
public sealed class EconomyInvariantViolation(string message) : Exception(message);

/// <summary>The invariant checkers A, B, C, D (see <see cref="EconomyFixture"/>).</summary>
public static class EconomyInvariants
{
    /// <summary>A: total zeny moved by exactly <paramref name="expectedDelta"/> (0 for a pure transfer).</summary>
    public static void AssertZenyConserved(EconomySnapshot before, EconomySnapshot after, long expectedDelta = 0)
    {
        var actual = after.TotalZeny - before.TotalZeny;
        if (actual == expectedDelta) return;
        var moved = after.Gold.Keys.Where(k => !before.Gold.TryGetValue(k, out var g) || g != after.Gold[k]
                                               || before.Bank.GetValueOrDefault(k) != after.Bank[k])
            .Select(k => $"char{k}: {before.Gold.GetValueOrDefault(k)}+{before.Bank.GetValueOrDefault(k)}bank -> {after.Gold[k]}+{after.Bank[k]}bank");
        throw new EconomyInvariantViolation(
            $"A (zeny conservation): total moved by {actual}, expected {expectedDelta} "
            + $"({before.TotalZeny} -> {after.TotalZeny}; mail {before.MailZeny} -> {after.MailZeny}; "
            + $"escrow {before.AuctionEscrow} -> {after.AuctionEscrow}; {string.Join("; ", moved)})");
    }

    /// <summary>B: no uid held in two places, none lost, none minted, per-template quantity conserved; the
    /// expectation says what may differ.</summary>
    public static void AssertItemsConserved(EconomySnapshot before, EconomySnapshot after, ItemExpectation? expected = null)
    {
        expected ??= ItemExpectation.None;

        var dup = after.Items.Where(i => i.Uid != 0).GroupBy(i => i.Uid).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new EconomyInvariantViolation(
                $"B (item uid): uid {dup.Key:X} is in {dup.Count()} places at once: {string.Join(", ", dup.Select(i => i.Where))}");

        var beforeUids = before.Items.Where(i => i.Uid != 0).Select(i => i.Uid).ToHashSet();
        var afterUids = after.Items.Where(i => i.Uid != 0).Select(i => i.Uid).ToHashSet();
        var lost = beforeUids.Where(u => !afterUids.Contains(u) && !expected.ConsumedUids.Contains(u)).ToList();
        if (lost.Count > 0)
            throw new EconomyInvariantViolation($"B (item uid): item uid(s) lost: {string.Join(", ", lost.Select(u => u.ToString("X")))}");
        var minted = afterUids.Where(u => !beforeUids.Contains(u) && !expected.CreatedUids.Contains(u)).ToList();
        if (minted.Count > 0)
            throw new EconomyInvariantViolation($"B (item uid): item uid(s) minted from nothing: {string.Join(", ", minted.Select(u => u.ToString("X")))}");

        var qb = before.Items.GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Count));
        var qa = after.Items.GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Count));
        foreach (var template in qb.Keys.Concat(qa.Keys).Concat(expected.QuantityDelta.Keys).Distinct())
        {
            var delta = qa.GetValueOrDefault(template) - qb.GetValueOrDefault(template);
            var want = expected.QuantityDelta.GetValueOrDefault(template);
            if (delta != want)
                throw new EconomyInvariantViolation(
                    $"B (item quantity): template {template} total moved by {delta}, expected {want} "
                    + $"({qb.GetValueOrDefault(template)} -> {qa.GetValueOrDefault(template)})");
        }
    }

    /// <summary>C: no two inventory rows share (character, storage type, slot) for a real slot (&gt;= 0).</summary>
    public static void AssertUniquePlacement(EconomySnapshot snapshot)
    {
        var clash = snapshot.Placements.GroupBy(p => (p.CharacterId, p.StorageType, p.Slot)).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
            throw new EconomyInvariantViolation(
                $"C (unique placement): rows {string.Join(", ", clash.Select(p => p.RowId))} share "
                + $"char {clash.Key.CharacterId} storage {clash.Key.StorageType} slot {clash.Key.Slot}");
    }

    /// <summary>D: no wallet, bank, unclaimed mail or escrowed bid is negative.</summary>
    public static void AssertNoNegativeGold(EconomySnapshot snapshot)
    {
        var bad = snapshot.Gold.Where(kv => kv.Value < 0).Select(kv => $"char{kv.Key} gold {kv.Value}")
            .Concat(snapshot.Bank.Where(kv => kv.Value < 0).Select(kv => $"char{kv.Key} bank {kv.Value}"))
            .ToList();
        if (snapshot.MailZenyMin < 0) bad.Add($"a mail holds {snapshot.MailZenyMin} zeny");
        if (snapshot.AuctionEscrowMin < 0) bad.Add($"an article holds a top bid of {snapshot.AuctionEscrowMin}");
        if (bad.Count > 0) throw new EconomyInvariantViolation($"D (no negative gold): {string.Join("; ", bad)}");
    }

    /// <summary>D, live half: no operation of the run tripped the throwing <c>PlayerState.Gold</c> setter.</summary>
    public static void AssertNoNegativeWalletFault(InterleaveResult result)
    {
        if (result.HitNegativeWallet)
            throw new EconomyInvariantViolation(
                "D (no negative gold): an operation tried to drive a PlayerState wallet below 0 (setter threw). "
                + $"Trace: {string.Join(" ", result.Trace)}");
    }
}
