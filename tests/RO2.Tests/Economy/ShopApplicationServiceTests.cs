using RO2.BuildingBlocks;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Inventory.Domain;
using Xunit;

namespace RO2.Tests.Economy;

/// <summary>
/// Pins the NPC-shop buy / sell / buy-back flows of <see cref="ShopApplicationService"/> as the pre-extraction
/// handlers ran them, over scripted ports that record every step in ONE ordered event list: the wallet debit, the
/// inventory steps, the host hook and the wallet persist. The oracle is the order and the refund rule of the original
/// handlers (zeny reserved BEFORE the checks and handed back on every later refusal; the buy persists after the
/// write and the host's 2379 sends, zeny path only; the sale persists after the credit; the buy-back after the add).
/// </summary>
public sealed class ShopApplicationServiceTests
{
    private const uint CharId = 4711;
    private const uint Stocked = 21110001;
    private const uint Token = 60000121;
    private const long Start = 1000;

    // ---------- scripted ports ----------

    private sealed class Wallet(List<string> events, long gold) : IWallet
    {
        public long Gold { get; private set; } = gold;
        public long BankGold => 0;

        /// <summary>Runs inside <see cref="Credit"/> (before the balance moves), to prove what the flow had already read.</summary>
        public Action? OnCredit { get; set; }

        public bool TryDebit(long amount)
        {
            if (amount < 0 || Gold < amount) return false;
            Gold -= amount;
            events.Add($"debit:{amount}");
            return true;
        }

        public long DebitUpTo(long amount) => throw new NotSupportedException();

        public void Credit(long amount)
        {
            OnCredit?.Invoke();
            Gold += amount;
            events.Add($"credit:{amount}");
        }
    }

    private sealed class Bag(IReadOnlyList<BagStack> stacks, IReadOnlyList<short> freeCells) : IShopBag
    {
        public IReadOnlyList<BagStack> Stacks { get; } = stacks;
        public IReadOnlyList<short> FreeCells { get; } = freeCells;
    }

    private sealed class Inventory(List<string> events) : IShopInventory
    {
        public IShopBag Bag { get; set; } = new Bag([], [0, 1, 2]);
        public bool ReadThrows { get; set; }
        public bool WriteThrows { get; set; }
        public bool PlaceThrows { get; set; }
        public bool NoFreeCell { get; set; }
        public Dictionary<ulong, ItemStackSnapshot> Rows { get; } = [];
        public ShopBuyPlan? WrittenPlan { get; private set; }
        public IReadOnlyList<StackTake>? WrittenTakes { get; private set; }

        public Task<IShopBag> ReadBagAsync(uint characterId)
        {
            events.Add("readbag");
            return ReadThrows ? throw new InvalidOperationException("bag") : Task.FromResult(Bag);
        }

        public Task<ShopBuyWrite> WriteBuyAsync(IShopBag bag, uint characterId, uint itemId, int quantity, ShopBuyPlan plan,
            uint templateDurability, IReadOnlyList<StackTake>? tokenTakes)
        {
            events.Add("write");
            if (WriteThrows) throw new InvalidOperationException("write");
            WrittenPlan = plan;
            WrittenTakes = tokenTakes;
            return Task.FromResult(new ShopBuyWrite(
                [new ItemStackSnapshot { Id = 9000, ItemId = itemId, Count = quantity }],
                (tokenTakes ?? []).Select(t => new ShopTokenTouch(new ItemStackSnapshot { Id = t.StackId, ItemId = Token, Count = t.Left }, t.Left <= 0)).ToList()));
        }

        public Task TakeForSaleAsync(uint characterId, IReadOnlyList<ShopSellEntry> entries,
            Func<ItemStackSnapshot, bool> isSellable, Action<ShopSaleLine> onSold)
        {
            foreach (var (uid, qty) in entries)
            {
                if (!Rows.TryGetValue(uid, out var row) || !isSellable(row)) continue;
                var sellQty = ShopPricing.SellQty(qty, row.Count);
                events.Add($"take:{uid}x{sellQty}");
                onSold(new ShopSaleLine(row, sellQty, row.Count - sellQty));
            }
            return Task.CompletedTask;
        }

        public Task<ItemStackSnapshot?> PlaceBackAsync(uint characterId, ItemStackSnapshot sold)
        {
            events.Add("placeback");
            if (PlaceThrows) throw new InvalidOperationException("place");
            return Task.FromResult<ItemStackSnapshot?>(NoFreeCell ? null : sold with { Slot = 5 });
        }
    }

    private sealed class Log(List<string> events) : IShopLog
    {
        public void CannotAfford(string playerName, uint quantity, string itemName, long cost, long have) => events.Add("log:CannotAfford");
        public void BagUnreadable(Exception ex, string playerName, uint quantity, string itemName) => events.Add("log:BagUnreadable");
        public void PossessionExceeded(string playerName, uint quantity, string itemName, long held, int limit) => events.Add($"log:PossessionExceeded:{held}/{limit}");
        public void TokensShort(string playerName, uint quantity, string itemName, ushort tokenRow) => events.Add("log:TokensShort");
        public void NoFreeCell(string playerName, uint quantity, string itemName) => events.Add("log:NoFreeCell");
        public void Buying(string playerName, uint quantity, string itemName, long cost, uint npcHandle) => events.Add($"log:Buying:{cost}");
        public void BuyNotSaved(Exception ex, string playerName, uint quantity, string itemName) => events.Add("log:BuyNotSaved");
        public void Sold(string playerName, ulong uid, string itemName, int quantity, long gold) => events.Add($"log:Sold:{uid}x{quantity}={gold}");
        public void SoldNothing(string playerName, int requested) => events.Add($"log:SoldNothing:{requested}");
        public void BuyBackNotListed(string playerName, ulong uid) => events.Add("log:BuyBackNotListed");
        public void BuyBackCannotAfford(string playerName, ulong uid, long cost, long have) => events.Add("log:BuyBackCannotAfford");
        public void BuyBackNotWritten(Exception ex, string playerName, ulong uid) => events.Add("log:BuyBackNotWritten");
        public void BuyBackNoFreeCell(string playerName, ulong uid) => events.Add("log:BuyBackNoFreeCell");
        public void BoughtBack(string playerName, ulong uid, uint itemId, int count, long cost) => events.Add($"log:BoughtBack:{cost}");
    }

    private sealed class Rig
    {
        public Rig(long gold = Start)
        {
            Wallet = new Wallet(Events, gold);
            Inventory = new Inventory(Events);
            Log = new Log(Events);
        }

        public List<string> Events { get; } = [];
        public Wallet Wallet { get; }
        public Inventory Inventory { get; }
        public Log Log { get; }
        public ShopApplicationService Service { get; } = new();

        /// <summary>Wallet balance seen by each persist call, in order.</summary>
        public List<long> PersistedGold { get; } = [];

        public Task PersistAsync()
        {
            Events.Add("persist");
            PersistedGold.Add(Wallet.Gold);
            return Task.CompletedTask;
        }

        public Task<ShopBuyResult> BuyAsync(ShopBuyCommand cmd, bool hook = true)
            => Service.BuyAsync(cmd, Wallet, Inventory, Log, PersistAsync,
                hook ? written => { Events.Add($"onWritten:{written.TokenTouches.Count}"); return Task.CompletedTask; } : null);
    }

    private static ShopBuyCommand ZenyBuy(uint quantity = 3, int possessionLimit = 0, byte stackMax = 99)
        => new(CharId, "Buyer", 0x00AB, Stocked, "Stocked", quantity, UnitBuyPrice: 40, stackMax, TemplateDurability: 0,
            possessionLimit, Token: null, TokenRowId: 0);

    private static ShopBuyCommand TokenBuy(uint quantity = 1)
        => new(CharId, "Buyer", 0x00AB, Stocked, "Stocked", quantity, UnitBuyPrice: 40, StackMax: 99, TemplateDurability: 0,
            PossessionLimit: 0, new TokenCost(new[] { (Token, 6) }), TokenRowId: 1125);

    private static string[] Indexed(IEnumerable<string> events, params string[] prefixes)
        => events.Where(e => prefixes.Any(e.StartsWith)).ToArray();

    // ============================== BUY ==============================

    [Fact]
    public async Task Buy_ZenyPath_DebitsThenReadsBagThenWritesThenHooksThenPersists_AndNothingAfter()
    {
        var rig = new Rig();

        var result = await rig.BuyAsync(ZenyBuy(quantity: 3));

        Assert.True(result.Bought);
        Assert.Equal(["debit:120", "readbag", "log:Buying:120", "write", "onWritten:0", "persist"], rig.Events);
        // The wallet is persisted AFTER the debit, so a crash after the write cannot keep the item and the zeny.
        Assert.Equal([Start - 120], rig.PersistedGold);
        Assert.Equal(Start - 120, rig.Wallet.Gold);
    }

    [Fact]
    public async Task Buy_TokenPath_ChargesNoZeny_AndNeverPersistsTheWallet()
    {
        var rig = new Rig();
        rig.Inventory.Bag = new Bag([new BagStack(7, Token, 10, 0)], [1, 2]);

        var result = await rig.BuyAsync(TokenBuy());

        Assert.True(result.Bought);
        Assert.Equal(["debit:0", "readbag", "log:Buying:0", "write", "onWritten:1"], rig.Events);   // no "persist"
        Assert.Empty(rig.PersistedGold);
        Assert.Equal(Start, rig.Wallet.Gold);
        // The token take handed to the write is the planner's: 6 of the 10, the stack left with 4.
        var take = Assert.Single(rig.Inventory.WrittenTakes!);
        Assert.Equal((7UL, 4), (take.StackId, take.Left));
    }

    [Fact]
    public async Task Buy_OnWrittenRunsBeforeThePersist_SoTheTokenFramesPrecedeIt()
    {
        var rig = new Rig();
        var seenBy = new List<string>();

        await rig.Service.BuyAsync(ZenyBuy(), rig.Wallet, rig.Inventory, rig.Log, rig.PersistAsync,
            _ => { seenBy.Add(rig.Events.Contains("persist") ? "after-persist" : "before-persist"); return Task.CompletedTask; });

        Assert.Equal(["before-persist"], seenBy);
    }

    [Fact]
    public async Task Buy_CannotAfford_RefusesGeneric_NothingToRefund_BagNeverRead()
    {
        var rig = new Rig(gold: 100);

        var result = await rig.BuyAsync(ZenyBuy(quantity: 3));   // 120 > 100

        Assert.False(result.Bought);
        Assert.Equal(ShopCodes.BuyFailGeneric, result.RefusalCode);
        Assert.Equal(["log:CannotAfford"], rig.Events);
        Assert.Equal(100, rig.Wallet.Gold);
    }

    public static TheoryData<string, ushort> RefusalsAfterTheDebit => new()
    {
        { "possession", ShopCodes.BuyFailPossession },
        { "tokens", ShopCodes.BuyFailGeneric },
        { "bagfull", ShopCodes.BuyFailBagFull },
        { "unreadable", ShopCodes.BuyFailGeneric },
        { "writefails", ShopCodes.BuyFailGeneric },
    };

    [Theory]
    [MemberData(nameof(RefusalsAfterTheDebit))]
    public async Task Buy_EveryRefusalAfterTheDebit_HandsTheZenyBack_BeforeReturning_AndPersistsNothing(string scenario, ushort code)
    {
        var rig = new Rig();
        var cmd = ZenyBuy(quantity: 3);
        switch (scenario)
        {
            case "possession":
                cmd = ZenyBuy(quantity: 3, possessionLimit: 5);
                rig.Inventory.Bag = new Bag([new BagStack(7, Stocked, 3, 0)], [1, 2]);   // 3 held + 3 > 5
                break;
            case "tokens":
                cmd = TokenBuy();
                rig.Inventory.Bag = new Bag([new BagStack(7, Token, 5, 0)], [1, 2]);     // needs 6
                break;
            case "bagfull":
                rig.Inventory.Bag = new Bag([], []);
                break;
            case "unreadable":
                rig.Inventory.ReadThrows = true;
                break;
            case "writefails":
                rig.Inventory.WriteThrows = true;
                break;
        }

        var result = await rig.BuyAsync(cmd);

        Assert.False(result.Bought);
        Assert.Equal(code, result.RefusalCode);
        // debit first; the credit of exactly the reserved amount happens before the result exists.
        long reserved = cmd.Token is null ? 120 : 0;
        Assert.Equal($"debit:{reserved}", rig.Events[0]);
        Assert.Equal(Start, rig.Wallet.Gold);
        if (reserved > 0) Assert.Contains($"credit:{reserved}", rig.Events);
        Assert.DoesNotContain("persist", rig.Events);
        Assert.DoesNotContain(rig.Events, e => e.StartsWith("onWritten"));
    }

    [Fact]
    public async Task Buy_PossessionRefusal_ReportsHeldAndTheLimit()
    {
        var rig = new Rig();
        rig.Inventory.Bag = new Bag([new BagStack(7, Stocked, 3, 0), new BagStack(8, Stocked, 1, 1), new BagStack(9, 1, 50, 2)], [3]);

        await rig.BuyAsync(ZenyBuy(quantity: 3, possessionLimit: 5));

        Assert.Contains("log:PossessionExceeded:4/5", rig.Events);   // only this template counts: 3 + 1
    }

    [Fact]
    public async Task Buy_WriteFailure_IsReportedAsNotSaved_BeforeTheRefund()
    {
        var rig = new Rig();
        rig.Inventory.WriteThrows = true;

        await rig.BuyAsync(ZenyBuy());

        Assert.Equal(["debit:120", "readbag", "log:Buying:120", "write", "log:BuyNotSaved", "credit:120"], rig.Events);
    }

    [Fact]
    public async Task Buy_Placement_RestacksOntoTheLowestCellStack_ElseSplitsIntoFreeCells()
    {
        var rig = new Rig();
        rig.Inventory.Bag = new Bag([new BagStack(7, Stocked, 90, 4), new BagStack(8, Stocked, 20, 2)], [0, 1]);
        await rig.BuyAsync(ZenyBuy(quantity: 5));
        Assert.Equal(8UL, rig.Inventory.WrittenPlan!.RestackId);   // 20+5 and 90+5 both fit; cell 2 is the lower

        var split = new Rig(gold: 10_000);
        split.Inventory.Bag = new Bag([], [6, 9]);
        await split.BuyAsync(ZenyBuy(quantity: 150));
        Assert.Null(split.Inventory.WrittenPlan!.RestackId);
        Assert.Equal(new (short, int)[] { (6, 99), (9, 51) }, split.Inventory.WrittenPlan.NewStacks.ToArray());
    }

    // ============================== SELL ==============================

    private static ShopSellCommand Sell(Func<uint, ShopSellTerms?> terms, params (ulong Uid, int Qty)[] entries)
        => new(CharId, "Seller", entries.Select(e => new ShopSellEntry(e.Uid, e.Qty)).ToList(), terms);

    private static ShopSellTerms? Pays10(uint itemId) => itemId == Stocked ? new ShopSellTerms(10, "Stocked") : null;

    [Fact]
    public async Task Sell_TakesThenCreditsThenPersists_PaysTheTemplatesOwnPrice_AndRecordsEachSaleOnTheList()
    {
        var rig = new Rig();
        var list = new BuyBackList();
        rig.Inventory.Rows[11] = new ItemStackSnapshot { Id = 11, ItemId = Stocked, Count = 4 };
        rig.Inventory.Rows[12] = new ItemStackSnapshot { Id = 12, ItemId = Stocked, Count = 9 };

        var result = await rig.Service.SellAsync(Sell(Pays10, (11, 4), (12, 3)), rig.Wallet, list, rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.True(result.Sold);
        Assert.Equal(70, result.Proceeds);   // 4*10 + 3*10
        Assert.Equal(["take:11x4", "log:Sold:11x4=40", "take:12x3", "log:Sold:12x3=30", "credit:70", "persist"], rig.Events);
        Assert.Equal([Start + 70], rig.PersistedGold);   // persisted AFTER the credit
        Assert.Equal([Stocked], result.SoldTemplates);   // one template, listed once
        Assert.Equal([(4, 0), (3, 6)], result.Lines.Select(l => (l.SellQty, l.NewCount)));
        // What the buy-back charges is what the sale paid, and the copy carries the SOLD quantity.
        Assert.True(list.TryTake(12, out var entry, out _));
        Assert.Equal((3, 30L), (entry!.Item.Count, entry.Paid));
    }

    [Fact]
    public async Task Sell_TheBuyBackUidListIsReadAfterThisRequestsSales_AndBeforeTheCredit()
    {
        var rig = new Rig();
        var list = new BuyBackList();
        rig.Inventory.Rows[11] = new ItemStackSnapshot { Id = 11, ItemId = Stocked, Count = 1 };
        rig.Inventory.Rows[12] = new ItemStackSnapshot { Id = 12, ItemId = Stocked, Count = 1 };
        // Something that changes the list at the moment of the credit: a list read AFTER the credit would show it.
        rig.Wallet.OnCredit = () => list.RecordSale(new ItemStackSnapshot { Id = 99, ItemId = Stocked }, 1, 1);

        var result = await rig.Service.SellAsync(Sell(Pays10, (11, 1), (12, 1)), rig.Wallet, list, rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.Equal(new ulong[] { 11, 12 }, result.BuyBackUids);   // both sales in, the credit-time entry not yet
        Assert.Equal(new ulong[] { 11, 12, 99 }, list.Uids());
    }

    [Fact]
    public async Task Sell_NothingResolved_TouchesNeitherTheWalletNorTheList_AndLogsTheRequestedCount()
    {
        var rig = new Rig();
        var list = new BuyBackList();
        rig.Inventory.Rows[11] = new ItemStackSnapshot { Id = 11, ItemId = 777, Count = 1 };   // template cannot be sold

        var result = await rig.Service.SellAsync(Sell(Pays10, (11, 1), (404, 2)), rig.Wallet, list, rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.False(result.Sold);
        Assert.Equal(["log:SoldNothing:2"], rig.Events);   // no credit, no persist
        Assert.Equal(Start, rig.Wallet.Gold);
        Assert.Empty(list.Uids());
    }

    // ============================== BUY-BACK ==============================

    private static ShopBuyBackCommand BuyBack(ulong uid) => new(CharId, "Buyer", uid);

    private static BuyBackList ListWith(params ulong[] uids)
    {
        var list = new BuyBackList();
        foreach (var uid in uids) list.RecordSale(new ItemStackSnapshot { Id = uid, ItemId = Stocked, Count = 5 }, 2, 20);
        return list;
    }

    [Fact]
    public async Task BuyBack_DebitsThenPlacesThenPersists_AndTheEntryLeavesTheList()
    {
        var rig = new Rig();
        var list = ListWith(900, 901);

        var result = await rig.Service.BuyBackAsync(BuyBack(900), rig.Wallet, list, rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.Equal(0, result.Code);
        Assert.Equal((Stocked, 2), (result.Restored!.ItemId, result.Restored.Count));   // the SOLD quantity comes back
        Assert.Equal(["debit:20", "placeback", "persist", "log:BoughtBack:20"], rig.Events);
        Assert.Equal([Start - 20], rig.PersistedGold);
        Assert.Equal(new ulong[] { 901 }, list.Uids());
    }

    [Fact]
    public async Task BuyBack_NotOnTheList_IsRefused_NothingChargedOrPersisted()
    {
        var rig = new Rig();

        var result = await rig.Service.BuyBackAsync(BuyBack(900), rig.Wallet, ListWith(901), rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.Equal((ShopCodes.CancelRefused, true), (result.Code, result.Restored is null));
        Assert.Equal(["log:BuyBackNotListed"], rig.Events);
    }

    [Fact]
    public async Task BuyBack_CannotAfford_PutsTheEntryBackWhereItWas_AndChargesNothing()
    {
        var rig = new Rig(gold: 5);
        var list = ListWith(900, 901, 902);

        var result = await rig.Service.BuyBackAsync(BuyBack(901), rig.Wallet, list, rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.Equal(ShopCodes.CancelRefused, result.Code);
        Assert.Equal(["log:BuyBackCannotAfford"], rig.Events);
        Assert.Equal(new ulong[] { 900, 901, 902 }, list.Uids());   // restored at its old index
        Assert.Equal(5, rig.Wallet.Gold);
    }

    [Fact]
    public async Task BuyBack_BagFull_RefundsBeforeReturning_RestoresTheEntry_DoesNotPersist_Code20020()
    {
        var rig = new Rig();
        rig.Inventory.NoFreeCell = true;
        var list = ListWith(900, 901);

        var result = await rig.Service.BuyBackAsync(BuyBack(900), rig.Wallet, list, rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.Equal(ShopCodes.CancelBagFull, result.Code);
        Assert.Equal(["debit:20", "placeback", "credit:20", "log:BuyBackNoFreeCell"], rig.Events);
        Assert.Equal(new ulong[] { 900, 901 }, list.Uids());
        Assert.Equal(Start, rig.Wallet.Gold);
    }

    [Fact]
    public async Task BuyBack_WriteFailure_RefundsRestoresAndDoesNotPersist_GenericRefusal()
    {
        var rig = new Rig();
        rig.Inventory.PlaceThrows = true;
        var list = ListWith(900);

        var result = await rig.Service.BuyBackAsync(BuyBack(900), rig.Wallet, list, rig.Inventory, rig.Log, rig.PersistAsync);

        Assert.Equal(ShopCodes.CancelRefused, result.Code);
        Assert.Equal(["debit:20", "placeback", "credit:20", "log:BuyBackNotWritten"], rig.Events);
        Assert.Equal(new ulong[] { 900 }, list.Uids());
        Assert.Equal(Start, rig.Wallet.Gold);
    }

    // ============================== BuyBackList (moved verbatim from BuyBackLedger) ==============================

    [Fact]
    public void BuyBackList_Restore_KeepsTheQuirk_ExistsReturns_ElseInsertsAndDropsTheOldestWhenOverTen()
    {
        var list = ListWith(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        Assert.True(list.TryTake(4, out var four, out var index));
        Assert.Equal(3, index);
        list.RecordSale(new ItemStackSnapshot { Id = 11, ItemId = Stocked }, 1, 1);   // the list is full again: 1..3,5..11

        list.Restore(four!, index);   // 11 entries -> the oldest (index 0, uid 1) goes
        Assert.Equal(new ulong[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, list.Uids());

        list.Restore(four!, 0);       // already there: a no-op, even at another index
        Assert.Equal(new ulong[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, list.Uids());
    }

    [Fact]
    public void BuyBackList_RecordSale_CopiesTheStackWithTheSoldCount_AndAReSaleReplacesTheOlderEntry()
    {
        var list = new BuyBackList();
        var stack = new ItemStackSnapshot { Id = 42, ItemId = Stocked, Count = 250, Refine = 7 };

        list.RecordSale(stack, soldQty: 100, paid: 100);
        list.RecordSale(stack with { Id = 43 }, soldQty: 1, paid: 1);
        list.RecordSale(stack, soldQty: 50, paid: 50);   // a partly sold stack keeps its uid: the newer entry replaces

        Assert.Equal(new ulong[] { 43, 42 }, list.Uids());
        Assert.True(list.TryTake(42, out var entry, out _));
        Assert.Equal((50, 50L, (byte)7), (entry!.Item.Count, entry.Paid, entry.Item.Refine));
        Assert.Equal(250, stack.Count);   // the caller's snapshot is untouched
    }
}
