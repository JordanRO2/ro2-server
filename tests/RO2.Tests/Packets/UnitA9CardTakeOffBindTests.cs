using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// 1552 ReqCardBookTakeOff_1Pcs: the request's kind byte is the book copy's bind bit, so the card comes back bound
/// (<see cref="CardService.TakenOffRow"/>) and joins only a stack of the same bind state (<see cref="CardStacks"/>).
/// The fixture is card 22310832 (Bind_Type 1, Stack_Max 20).
/// </summary>
public class UnitA9CardTakeOffBindTests
{
    private const uint Owner = 7;
    private const uint Card = 22310832;

 /// <summary>The shipped ItemInfo row of 22310832.</summary>
    private static readonly ItemData CardTemplate = new() { ID = Card, Item_Type = 13, Stack_Max = 20, Bind_Type = 1, Durability = 10000 };

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void KindOne_ComesBackBound_KindZero_Unbound()
    {
        
        
        var bound = CardService.TakenOffRow(Owner, Card, 10, kind: 1);
        Assert.True(bound.Bound);
        Assert.Equal((byte)10, bound.Refine);
        Assert.Equal(1, bound.Count);
        Assert.Equal((short)-1, bound.Slot);
        Assert.Equal((byte)2, ItemPackets.RecordBindFlags(CardTemplate.Bind_Type, bound.StorageType, bound.Bound));

        var unbound = CardService.TakenOffRow(Owner, Card, 10, kind: 0);
        Assert.False(unbound.Bound);
        Assert.Equal((byte)0, ItemPackets.RecordBindFlags(CardTemplate.Bind_Type, unbound.StorageType, unbound.Bound));
    }

    [Fact]
    public void KindOneCopies_JoinTheBoundStack_AsRetail()
    {
        
        var boundStack = CardService.TakenOffRow(Owner, Card, 10, kind: 1);
        boundStack.Id = 0x0E043DA80E742B5DUL;
        boundStack.Slot = 0;
        boundStack.Durability = 10000;
        
        
        
        var unboundStack = new InventoryItem
        {
            Id = 0x0E043DA80E742B39UL, CharacterId = Owner, ItemId = Card, Count = 1, Slot = 1, Refine = 10, Durability = 10000,
        };
        var owned = new List<InventoryItem> { unboundStack, boundStack };
        Assert.Same(unboundStack, CardStacks.FindJoinStack(owned, CardTemplate, Card, 10, bound: false));

        
        var join = CardStacks.FindJoinStack(owned, CardTemplate, Card, 10, bound: true);
        Assert.Same(boundStack, join);
        join!.Count += 1;

        
        
        
        const string retail64 = "000002005d2b740ea83d040eb06f540110270201320000000000000000000002000a000000000000000000000000000000000000000000000000000000000000";
        var body = LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(join), pickupKind: 2);
        var ours = Hex(body)[..128];
        Assert.Equal(retail64[..(2 * (4 + 0x1B))], ours[..(2 * (4 + 0x1B))]);
        Assert.Equal(retail64[(2 * (4 + 0x1C))..], ours[(2 * (4 + 0x1C))..]);
        Assert.Equal("02", retail64.Substring(2 * (4 + 0x1B), 2));
        Assert.Equal((byte)2, ItemPackets.RecordBindFlags(CardTemplate.Bind_Type, join.StorageType, join.Bound));

        
        Assert.Equal("0c00000000000100b06f540101030a01",
            Hex(CardStatusPackets.BuildUpdateCardStatus2589(new[] { new CardStatusRecord(Card, 1, 3, 10, 1) })));
    }

    [Fact]
    public void BindTypeTwo_AndZero_StackRegardlessOfTheRowFlag()
    {
        
        
        var btTwo = CardTemplate with { Bind_Type = 2 };
        var btZero = CardTemplate with { Bind_Type = 0 };
        var stack = new InventoryItem { Id = 1, CharacterId = Owner, ItemId = Card, Count = 1, Slot = 0, Refine = 0 };
        Assert.Same(stack, CardStacks.FindJoinStack(new[] { stack }, btTwo, Card, 0, bound: true));
        Assert.Same(stack, CardStacks.FindJoinStack(new[] { stack }, btZero, Card, 0, bound: true));
        
        stack.Count = 20;
        Assert.Null(CardStacks.FindJoinStack(new[] { stack }, btZero, Card, 0, bound: false));
        stack.Count = 1;
        Assert.Null(CardStacks.FindJoinStack(new[] { stack }, btZero with { Stack_Max = 1 }, Card, 0, bound: false));
        Assert.Null(CardStacks.FindJoinStack(new[] { stack }, null, Card, 0, bound: false));
    }
}
