using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Validation;

/// <summary>INV-H9 (1322 dead looter, Possession_Max in PlanLanding, tag+handle) and INV-H12 (1321 tag+handle).</summary>
public class LootPickupGuardTests
{
    // A well-formed 1322 request: msgTag 0xE2A3, picker handle 0x070F5211.
    private const string Retail1322 = "01151500a3e211520f074d668c0da83d040e0013e79d01";
    private const uint Picker = 0x070F5211;

    private static byte[] Body() => Convert.FromHexString(Retail1322);

    [Fact]
    public void Pickup_TheRetailFrame_PassesForItsOwnPicker()
        => Assert.Equal(InvReject.None, ItemPickUpHandler.CheckRequest(Body(), Picker));

    [Fact]
    public void Pickup_AnotherPlayersHandle_IsRejected()
        => Assert.Equal(InvReject.HandleMismatch, ItemPickUpHandler.CheckRequest(Body(), Picker + 1));

    [Fact]
    public void Pickup_AForgedHandleInTheBody_IsRejected()
    {
        var body = Body();
        body[6] ^= 0xFF; // blob+4, low byte of the picker handle
        Assert.Equal(InvReject.HandleMismatch, ItemPickUpHandler.CheckRequest(body, Picker));
    }

    [Fact]
    public void Pickup_AWrongMsgTag_IsRejected()
    {
        var body = Body();
        body[4] = 0x00; // blob+2
        Assert.Equal(InvReject.BadMsgTag, ItemPickUpHandler.CheckRequest(body, Picker));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void Pickup_ATruncatedBody_IsRejected(int length)
        => Assert.Equal(InvReject.BadMsgTag, ItemPickUpHandler.CheckRequest(Body().AsSpan(0, length), Picker));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Pickup_ADeadLooterMayNotPick(bool isDead, bool expected)
        => Assert.Equal(expected, ItemPickUpHandler.LooterMayPick(isDead));

    // 22261077 (siege supplies): possession limit of 15 (see BuyItemHandler).
    private const uint Limited = 22261077;

    private static IReadOnlyDictionary<uint, ItemData> Items()
        => new Dictionary<uint, ItemData>
        {
            [Limited] = new ItemData { ID = Limited, Stack_Max = 99 },
            [11100001] = new ItemData { ID = 11100001, Stack_Max = 99 },
        };

    private static InventoryItem Held(uint itemId, int count, short slot, ulong id = 1)
        => new() { Id = id, CharacterId = 1, ItemId = itemId, Count = count, Slot = slot, StorageType = 0 };

    [Fact]
    public void Plan_RefusesAPickupThatPassesPossessionMax_AndSaysWhy()
    {
        var plan = ItemPickUpHandler.PlanLanding(new[] { Held(Limited, 10, 0) }, 1, Limited, 6, Items());
        Assert.True(plan.Refused);
        Assert.True(plan.OverPossession);
        Assert.Null(plan.MergeInto);
        Assert.Empty(plan.NewStacks);
    }

    [Fact]
    public void Plan_CountsEveryBagStackTowardPossessionMax()
    {
        var owned = new[] { Held(Limited, 8, 0, 1), Held(Limited, 8, 3, 2) };
        Assert.True(ItemPickUpHandler.PlanLanding(owned, 1, Limited, 1, Items()).OverPossession);
    }

    [Fact]
    public void Plan_AcceptsAPickupThatReachesPossessionMaxExactly()
    {
        var plan = ItemPickUpHandler.PlanLanding(new[] { Held(Limited, 10, 0) }, 1, Limited, 5, Items());
        Assert.False(plan.Refused);
        Assert.False(plan.OverPossession);
        Assert.Equal(15, plan.MergedCount);
    }

    [Fact]
    public void Plan_NoLimitItem_IsNeverRefusedForPossession()
    {
        var plan = ItemPickUpHandler.PlanLanding(new[] { Held(11100001, 90, 0) }, 1, 11100001, 5, Items());
        Assert.False(plan.OverPossession);
        Assert.False(plan.Refused);
    }

    [Fact]
    public void Plan_AFullBag_IsRefusedButNotAsPossession()
    {
        var items = new Dictionary<uint, ItemData> { [11100001] = new ItemData { ID = 11100001, Stack_Max = 1 } };
        var owned = Enumerable.Range(0, BagCapacity.MainBagCells)
            .Select(s => Held(11100001, 1, (short)s, (ulong)(100 + s))).ToArray();
        var plan = ItemPickUpHandler.PlanLanding(owned, 1, 11100001, 1, items);
        Assert.True(plan.Refused);
        Assert.False(plan.OverPossession);
    }

    // ---- 1321 ----------------------------------------------------------------------------------------------

    private static ItemDropRequest Drop(ushort tag = ItemDropHandler.MsgTag, uint handle = Picker)
        => new(tag, handle, Slot: 5, ItemUid: 1, Extra: 0, X: 0, Y: 0, Z: 0);

    [Fact]
    public void Drop_ALegalRequest_Passes()
        => Assert.Equal(InvReject.None, ItemDropHandler.CheckRequest(Drop(), Picker));

    [Fact]
    public void Drop_AWrongTag_IsRejected()
        => Assert.Equal(InvReject.BadMsgTag, ItemDropHandler.CheckRequest(Drop(tag: 1), Picker));

    [Fact]
    public void Drop_AnotherPlayersHandle_IsRejected()
        => Assert.Equal(InvReject.HandleMismatch, ItemDropHandler.CheckRequest(Drop(handle: Picker + 1), Picker));
}
