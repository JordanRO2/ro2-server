using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Validation;

/// <summary>INV-H3 (1346 bank swap) and INV-H7 (1323 item use) pure decisions; the handlers are not test-instantiable.</summary>
public class BankItemUseGateTests
{
    private static InventoryItem Row(ulong id, byte storage, short slot, uint itemId = 100)
        => new() { Id = id, CharacterId = 7, ItemId = itemId, StorageType = storage, Slot = slot };

    private static readonly Dictionary<uint, ItemData> NoTemplates = new();

    // --- INV-H3 ---

    [Fact]
    public void BankOpenHandler_MarkOpen_OpensForThePlayerInRange_AndLapsOrWalksOff()
    {
        var p = new PlayerState { X = 10f, Y = 20f };
        var now = DateTime.UtcNow;
        Assert.False(p.IsBankOpen(now, BankOpenHandler.MaxRange));                      // 1346 without a 1341 -> refused
        BankOpenHandler.MarkOpen(p);
        Assert.True(p.IsBankOpen(DateTime.UtcNow, BankOpenHandler.MaxRange));
        Assert.False(p.IsBankOpen(now + BankOpenHandler.OpenWindow + TimeSpan.FromSeconds(5), BankOpenHandler.MaxRange));
        p.X += BankOpenHandler.MaxRange + 1f;
        Assert.False(p.IsBankOpen(DateTime.UtcNow, BankOpenHandler.MaxRange));
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 1, true)]
    [InlineData(0, 2, false)]
    [InlineData(2, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    public void BankWornDirect_IsRefused_OnlyWhenExactlyOneSideIsWornAndTheOtherIsTheBank(byte a, byte b, bool refused)
        => Assert.Equal(refused, BankSwapRules.CrossesBankAndWorn(a, b));

    [Fact]
    public void Deposit_OfAnIsDepositZeroTemplate_IsRefused_ButWithdrawAndBagMovesAreNot()
    {
        var noDeposit = new HashSet<uint> { 555 };
        Assert.True(BankSwapRules.DepositBlocked(0, 2, 555, noDeposit));     // bag -> bank
        Assert.True(BankSwapRules.DepositBlocked(1, 2, 555, noDeposit));     // worn -> bank
        Assert.False(BankSwapRules.DepositBlocked(2, 0, 555, noDeposit));    // withdraw
        Assert.False(BankSwapRules.DepositBlocked(2, 2, 555, noDeposit));    // rearrange inside the bank
        Assert.False(BankSwapRules.DepositBlocked(0, 0, 555, noDeposit));
        Assert.False(BankSwapRules.DepositBlocked(0, 2, 556, noDeposit));    // Is_Deposit 1 template
    }

    [Fact]
    public void WithdrawIntoAFullBag_IsBagFull_AndPlacesNothing()
    {
        var rows = new List<InventoryItem> { Row(900, 2, 3) };
        for (short s = 0; s < BagCapacity.MainBagCells; s++) rows.Add(Row((ulong)(s + 1), 0, s));
        var free = BagCapacity.FreeCells(rows, NoTemplates);
        Assert.Empty(free);

        Assert.Equal(BankSwapRefusal.BagFull, BankSwapRules.ResolveBagCell(free, null, 1, 0xFF, fromBank: true).Refusal);
        Assert.Equal(BankSwapRefusal.BagFull, BankSwapRules.ResolveBagCell(free, 4, 1, 54, fromBank: true).Refusal);
        // A bag-to-bag move with no free cell is not a "full bag" message, just a refusal.
        Assert.Equal(BankSwapRefusal.BadDestination, BankSwapRules.ResolveBagCell(free, 4, 1, 54, fromBank: false).Refusal);
        // No row was touched by the decision.
        Assert.Equal(2, rows[0].StorageType);
        Assert.Equal(3, rows[0].Slot);
    }

    [Fact]
    public void Withdraw_AnyFreeCell_TakesTheLowestFree_AndANamedCellMustBeFreeAndExist()
    {
        var rows = new List<InventoryItem>();
        for (short s = 0; s < BagCapacity.MainBagCells; s++)
            if (s != 5 && s != 9) rows.Add(Row((ulong)(s + 1), 0, s));
        var free = BagCapacity.FreeCells(rows, NoTemplates);

        Assert.Equal((BankSwapRefusal.None, (short)5), BankSwapRules.ResolveBagCell(free, null, 1, 0xFF, fromBank: true));
        Assert.Equal((BankSwapRefusal.None, (short)9), BankSwapRules.ResolveBagCell(free, 9, 1, 59, fromBank: true));
        Assert.Equal(BankSwapRefusal.BadDestination, BankSwapRules.ResolveBagCell(free, 6, 1, 56, fromBank: true).Refusal);   // occupied
        // Extra bag 2 with no bag equipped: flat 20.. does not exist, so it is never offered.
        Assert.Equal(BankSwapRefusal.BadDestination, BankSwapRules.ResolveBagCell(free, 20, 2, 0, fromBank: true).Refusal);
    }

    [Fact]
    public void Withdraw_CharBagWindowSlots70To72_AreNotBagCells()
    {
        var free = new List<short> { 0, 1, 20, 21, 22 };
        foreach (byte slot in new byte[] { 70, 71, 72 })
            Assert.Equal(BankSwapRefusal.BadDestination, BankSwapRules.ResolveBagCell(free, (short)(slot - 50), 1, slot, fromBank: true).Refusal);
    }

    [Fact]
    public void Withdraw_IntoAnEquippedExtraBag_UsesItsCells()
    {
        var rows = new List<InventoryItem> { Row(50, 3, 0, itemId: 8001) };
        for (short s = 0; s < BagCapacity.MainBagCells; s++) rows.Add(Row((ulong)(s + 1), 0, s));
        var templates = new Dictionary<uint, ItemData> { [8001] = new ItemData { ID = 8001, Item_Type = 7, Bag_Size = 4 } };
        var free = BagCapacity.FreeCells(rows, templates);
        Assert.Equal(new short[] { 20, 21, 22, 23 }, free);
        Assert.Equal((BankSwapRefusal.None, (short)22), BankSwapRules.ResolveBagCell(free, 22, 2, 2, fromBank: true));
        Assert.Equal(BankSwapRefusal.BadDestination, BankSwapRules.ResolveBagCell(free, 24, 2, 4, fromBank: true).Refusal);   // past Bag_Size
    }

    // --- INV-H7 ---

    private static ItemData Potion(byte reqLevel = 0, long reqJob = 0, byte reqSex = 0, byte type = 5)
        => new() { ID = 1, Item_Type = type, Require_Level = reqLevel, Require_Job = reqJob, Require_Sex = reqSex };

    private static ItemUseRefusal Use(ItemData? t, byte storage = 0, int level = 50, int job = 6, int sex = 1,
        bool dead = false, bool trading = false)
        => ItemUseRules.CanUse(t, storage, level, job, sex, dead, trading);

    [Fact]
    public void ABagConsumable_IsAllowed() => Assert.Equal(ItemUseRefusal.None, Use(Potion()));

    [Theory]
    [InlineData(1)]   // worn
    [InlineData(2)]   // bank (used remotely)
    [InlineData(3)]   // equipped bag window
    public void OnlyBagRowsCanBeUsed(byte storage) => Assert.Equal(ItemUseRefusal.NotInBag, Use(Potion(), storage));

    [Fact]
    public void OverLevel_Dead_Trading_UnknownTemplate_AreRefused()
    {
        Assert.Equal(ItemUseRefusal.LevelTooLow, Use(Potion(reqLevel: 100), level: 1));
        Assert.Equal(ItemUseRefusal.Dead, Use(Potion(), dead: true));
        Assert.Equal(ItemUseRefusal.Trading, Use(Potion(), trading: true));
        Assert.Equal(ItemUseRefusal.UnknownTemplate, Use(null));
    }

    [Fact]
    public void JobAndSexRequirements_FollowTheEquipReading()
    {
        Assert.Equal(ItemUseRefusal.JobNotAllowed, Use(Potion(reqJob: 1L << 3), job: 6));    // bit job-1 = 5 not set
        Assert.Equal(ItemUseRefusal.None, Use(Potion(reqJob: 1L << 5), job: 6));
        Assert.Equal(ItemUseRefusal.SexNotAllowed, Use(Potion(reqSex: 2), sex: 1));
        Assert.Equal(ItemUseRefusal.None, Use(Potion(reqSex: 3), sex: 1));                   // 3 = both
    }

    [Fact]
    public void WornGearAndEquippableBags_AreNotUsableTypes_EvenWithAnEffect()
    {
        var weapon = new ItemData { ID = 2, Item_Type = 1, Equip_Slot = 6, Effect_ID_1 = 77 };
        var armour = new ItemData { ID = 3, Item_Type = 2, Equip_Slot = 1 };
        var bag = new ItemData { ID = 4, Item_Type = 7, Bag_Size = 20 };
        Assert.Equal(ItemUseRefusal.NotUsableType, Use(weapon));
        Assert.Equal(ItemUseRefusal.NotUsableType, Use(armour));
        Assert.Equal(ItemUseRefusal.NotUsableType, Use(bag));
        // The types the handler acts on stay usable.
        foreach (byte type in new byte[] { 11, 19, 25, 26, 27, 38, 39, 43 })
            Assert.Equal(ItemUseRefusal.None, Use(Potion(type: type)));
    }

    [Fact]
    public void SameTemplateTwiceIn50ms_SecondIsRefused_OthersAreNot()
    {
        const uint ch = 4_000_001, tpl = 9001;
        Assert.True(ItemUseCooldown.TryAcquire(ch, tpl, 1_000));
        Assert.False(ItemUseCooldown.TryAcquire(ch, tpl, 1_050));            // inside the gap
        Assert.True(ItemUseCooldown.TryAcquire(ch, tpl + 1, 1_050));         // other template
        Assert.True(ItemUseCooldown.TryAcquire(ch + 1, tpl, 1_050));         // other character
        Assert.True(ItemUseCooldown.TryAcquire(ch, tpl, 1_000 + ItemUseCooldown.MinGapMs));   // gap measured from the last ACCEPTED use
    }
}
