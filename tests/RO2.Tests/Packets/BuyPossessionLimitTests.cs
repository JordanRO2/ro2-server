using System.Collections.Generic;
using RO2.Domain.Entities;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the 1334 possession-limit check (BuyItemHandler.ExceedsPossessionLimit) against reference answers
/// for purchases of siege supplies (item 22261077). The reference limit there is 15; the shipped ItemInfo
/// data lists 10.
/// </summary>
public class BuyPossessionLimitTests
{
    private const uint SiegeSupplies = 22261077;

    private static List<InventoryItem> Holding(int count) => count <= 0
        ? new List<InventoryItem>()
        : new List<InventoryItem> { new() { Id = 1, CharacterId = 7, ItemId = SiegeSupplies, Count = count, Slot = 3, StorageType = 0 } };

    [Fact]
    public void SiegeSupplies_UseRetailsMeasuredLimitNotItemInfos()
        => Assert.Equal(15, BuyItemHandler.PossessionLimitOf(SiegeSupplies));

    [Theory]
    
    [InlineData(0, 15)]   
    [InlineData(1, 14)]   
    [InlineData(14, 1)]   
    [InlineData(3, 10)]   
    [InlineData(3, 12)]   
    public void BuysUpToFifteenAreAccepted(int held, int quantity)
        => Assert.False(BuyItemHandler.ExceedsPossessionLimit(Holding(held), SiegeSupplies, quantity, out _, out _));

    [Theory]
    
    [InlineData(15, 1)]   
    [InlineData(15, 4)]   
    [InlineData(3, 20)]   
    [InlineData(1, 155)]  
    [InlineData(1, 15)]   
    public void BuysPastFifteenAreRefused(int held, int quantity)
    {
        Assert.True(BuyItemHandler.ExceedsPossessionLimit(Holding(held), SiegeSupplies, quantity, out var h, out var limit));
        Assert.Equal(held, h);
        Assert.Equal(15, limit);
    }
}
