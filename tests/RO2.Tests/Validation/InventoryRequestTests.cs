using System.Reflection;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Validation;

/// <summary>INV-H12 (<see cref="InventoryRequest.Validate"/>), INV-H2 (worn-slot stat cap) and the per-session bank-open mark.</summary>
public class InventoryRequestTests
{
    private const ushort Tag = 58024;
    private const uint Me = 0x00100007;

    private static InvReject V(ushort tag = Tag, uint handle = Me, byte container = 1, int slot = 0, int qty = 1, int count = 1, byte max = 4)
        => InventoryRequest.Validate(tag, Tag, handle, Me, container, slot, qty, count, max);

    [Fact]
    public void ALegalRequestPasses() => Assert.Equal(InvReject.None, V(container: 1, slot: 55, qty: 3, count: 5));

    [Fact]
    public void WrongMsgTagIsRejected_BeforeAnythingElse()
        => Assert.Equal(InvReject.BadMsgTag, V(tag: 1, handle: 9, container: 99, slot: -4, qty: 0));

    [Fact]
    public void HandleMismatchIsRejected_BeforeContainerSlotAndQty()
        => Assert.Equal(InvReject.HandleMismatch, V(handle: Me + 1, container: 99, slot: -4, qty: 0));

    [Fact]
    public void Container5_IsRejectedOnMax4_AndAcceptedOnMax5()
    {
        Assert.Equal(InvReject.BadContainer, V(container: 5, slot: 3, max: 4));
        Assert.Equal(InvReject.None, V(container: 5, slot: 3, max: 5));
        Assert.Equal(InvReject.BadContainer, V(container: 6, slot: 3, max: 5));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 49, true)]    // worn window
    [InlineData(1, 69, true)]    // main bag
    [InlineData(1, 70, true)]    // character-bag window 70..72
    [InlineData(1, 72, true)]
    [InlineData(1, 73, false)]
    [InlineData(1, -1, false)]
    [InlineData(2, 34, true)]    // extra bag: below the 35 stride ceiling
    [InlineData(4, 34, true)]
    [InlineData(2, 35, false)]
    [InlineData(2, 70, false)]   // a char-bag slot sent against an extra-bag container
    [InlineData(3, 70, false)]
    public void SlotIsBoundedPerContainer(int container, int slot, bool ok)
        => Assert.Equal(ok ? InvReject.None : InvReject.SlotOutOfRange, V(container: (byte)container, slot: slot));

    [Fact]
    public void BankSlotIsBoundedBy119()
    {
        Assert.Equal(InvReject.None, V(container: 5, slot: 119, max: 5));
        Assert.Equal(InvReject.SlotOutOfRange, V(container: 5, slot: 120, max: 5));
        Assert.Equal(InvReject.SlotOutOfRange, V(container: 5, slot: 0xFF, max: 5));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(6, 5)]
    [InlineData(1, 0)]   // an empty stack cannot be moved
    public void QuantityMustBeInOneToStackCount(int qty, int count)
        => Assert.Equal(InvReject.BadQuantity, V(qty: qty, count: count));

    [Fact]
    public void QuantityEqualToTheWholeStackPasses() => Assert.Equal(InvReject.None, V(qty: 5, count: 5));

    // --- INV-H2: a worn row only counts for stats when its template may stand in that cell ---

    private static async Task<GameDataStore> LoadStoreAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-invreq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "ItemInfo.json", "TraitInfo.json" })
                File.Copy(Packets.ItemLookTestData.File(name), Path.Combine(dir, name));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // StatService caches the TraitInfo index process-wide from the FIRST store that asks; another test class may have
    // cached it from a store without TraitInfo. Drop that cache right before each compute so this store's own
    // TraitInfo is read (the window to a parallel test re-caching an empty one is the next call).
    private static StatService.EquipStats Compute(GameDataStore store, params InventoryItem[] rows)
    {
        typeof(StatService).GetField("_index", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        return StatService.ComputeEquipmentStats(store, rows);
    }

    [Fact]
    public async Task ComputeEquipmentStats_IgnoresAWornRowInAnIllegalCell()
    {
        // 12100001: Equip_Slot 2 body armour, TraitInfo STR (type 0) flat 77.
        var store = await LoadStoreAsync();
        int Str(short slot, byte storage = 1) =>
            Compute(store, new InventoryItem { Id = 1, ItemId = 12100001, Count = 1, StorageType = storage, Slot = slot }).FlatOf(0);
        Assert.Equal(77, Str(2));        // its own cell
        Assert.Equal(0, Str(3));         // another armour cell
        Assert.Equal(0, Str(6));         // the weapon hand
        Assert.Equal(0, Str(30));        // not a worn cell at all
        Assert.Equal(0, Str(2, storage: 0));   // in a bag: never worn
    }

    [Fact]
    public async Task ComputeEquipmentStats_IgnoresAWornRowWhoseTemplateIsUnknown()
    {
        var store = await LoadStoreAsync();
        var stats = Compute(store,
            new InventoryItem { Id = 1, ItemId = 99999901, Count = 1, StorageType = 1, Slot = 2, EnchantReinforceId = 10014, EnchantSeconds = 2400 });
        Assert.Equal(0, stats.FlatOf(0));
    }

    // --- bank-open mark ---

    [Fact]
    public void BankOpenMark_HoldsWhileFreshAndNear_AndLapsOtherwise()
    {
        var p = new PlayerState { X = 10f, Y = 20f };
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(p.IsBankOpen(now, 50f));                       // never opened
        p.MarkBankOpen(now.AddMinutes(5), 10f, 20f);
        Assert.True(p.IsBankOpen(now.AddMinutes(1), 50f));
        Assert.False(p.IsBankOpen(now.AddMinutes(6), 50f));         // lapsed
        p.X = 10f + 60f;
        Assert.False(p.IsBankOpen(now.AddMinutes(1), 50f));         // walked away
        p.X = 10f + 30f;
        Assert.True(p.IsBankOpen(now.AddMinutes(1), 50f));
        p.ClearBankOpen();
        Assert.False(p.IsBankOpen(now.AddMinutes(1), 50f));
    }
}
