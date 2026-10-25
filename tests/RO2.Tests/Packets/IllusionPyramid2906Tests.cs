using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte layout of 2906 AnsIllusionPyramidGetMyState, the answer to 1616.
/// </summary>
/// <remarks>
/// The per-player tail holds the Mystical Phantom Stones (u16 at offset 23), Bright Phantom Stones (u16 at 25), bag Phantom Stones (u32 at 27) and the top-record flag (u8 at 31). The server fills only the bag count. The result is the near-cap warning, derived from the two balances (values 0, 29018, 29019 or 29020).
/// </remarks>
public class IllusionPyramid2906Tests
{
    
    private const string RetailRecordHex = "000000010000008bc4c901020000008cc4c90179889303090004000705000007";

    
    
    private const string RetailNeverExploredHex = "000000010000008bc4c901020000008cc4c9017988930300000000c609000000";

    
    
    private const string RetailMystical989Hex = "000000010000008bc4c901020000008cc4c90179889303dd0394008007000007"; 
    private const string RetailMystical990Hex = "5a7100010000008bc4c901020000008cc4c90179889303de0395007e07000007"; 
    private const string RetailMystical999Hex = "5a7100010000008bc4c901020000008cc4c90179889303e70397007607000007"; 

    
    
    private const string RetailBrightOnly992Hex = "5b7100010000008bc4c901020000008cc4c90179889303dd03e003e104000007"; 
    private const string RetailBrightOnly991Hex = "5b7100010000008bc4c901020000008cc4c90179889303ad03df030000000007"; 
    private const string RetailBoth990Hex = "5c7100010000008bc4c901020000008cc4c90179889303de03df034a08000007";       
    private const string RetailBothBright990Hex = "5c7100010000008bc4c901020000008cc4c90179889303e703de038a02000007"; 
    private const string RetailBright982Hex = "000000010000008bc4c901020000008cc4c901798893039902d603d909000007";    

    
    private static readonly Mileage[] PyramidMileage =
    {
        new() { Mileage_ID = 1, Mileage_Type = 1, Item = 30000267 },
        new() { Mileage_ID = 2, Mileage_Type = 1, Item = 30000268 },
    };

    [Fact]
    public void BuilderReproducesRetailRecordByteForByte()
    {
        var body = IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage,
            mysticalStones: 9, brightStones: 4, phantomStones: 1287, topRecord: 7);

        Assert.Equal(Convert.FromHexString(RetailRecordHex), body);
    }

    [Fact]
    public void MysticalNearCapWarningStartsAt990LikeRetail()
    {
        
        Assert.Equal(Convert.FromHexString(RetailMystical989Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 989, 148, 1920, 7));
        
        Assert.Equal(Convert.FromHexString(RetailMystical990Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 990, 149, 1918, 7));
        
        Assert.Equal(Convert.FromHexString(RetailMystical999Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 999, 151, 1910, 7));
    }

    [Fact]
    public void BrightNearCapWarningIs29019LikeRetail()
    {
        
        Assert.Equal(Convert.FromHexString(RetailBrightOnly992Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 989, 992, 1249, 7));
        
        Assert.Equal(Convert.FromHexString(RetailBrightOnly991Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 941, 991, 0, 7));
        
        Assert.Equal(Convert.FromHexString(RetailBright982Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 665, 982, 2521, 7));
    }

    [Fact]
    public void BothNearCapWarningIs29020LikeRetail()
    {
        
        Assert.Equal(Convert.FromHexString(RetailBoth990Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 990, 991, 2122, 7));
        
        Assert.Equal(Convert.FromHexString(RetailBothBright990Hex),
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 999, 990, 650, 7));
    }

    [Theory]
    [InlineData((ushort)0, (ushort)0, (ushort)0)]
    [InlineData((ushort)989, (ushort)989, (ushort)0)]
    [InlineData((ushort)990, (ushort)0, IllusionPyramidGetMyStateHandler.MysticalNearCapResult)]
    [InlineData((ushort)999, (ushort)989, IllusionPyramidGetMyStateHandler.MysticalNearCapResult)]
    [InlineData((ushort)0, (ushort)990, IllusionPyramidGetMyStateHandler.BrightNearCapResult)]
    [InlineData((ushort)989, (ushort)999, IllusionPyramidGetMyStateHandler.BrightNearCapResult)]
    [InlineData((ushort)990, (ushort)990, IllusionPyramidGetMyStateHandler.BothNearCapResult)]
    [InlineData((ushort)999, (ushort)999, IllusionPyramidGetMyStateHandler.BothNearCapResult)]
    public void ResultNamesTheBalancesAtOrAbove990(ushort mystical, ushort bright, ushort expected)
    {
        Assert.Equal(expected, IllusionPyramidGetMyStateHandler.NearCapResult(mystical, bright));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(
            IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, mystical, bright, 0, 0)));
    }

    [Fact]
    public void NoProgressRecordIsRetailsNeverExploredRecord()
    {
        
        
        var body = IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage,
            mysticalStones: 0, brightStones: 0, phantomStones: 2502, topRecord: 0);

        Assert.Equal(Convert.FromHexString(RetailNeverExploredHex), body);
        Assert.Equal(IllusionPyramidGetMyStateHandler.MyStateLength, body.Length);
    }

    [Fact]
    public void StoneCountSitsWhereTheClientGatesFloorOne()
    {
        
        var body = IllusionPyramidGetMyStateHandler.BuildMyState2906(PyramidMileage, 0, 0, phantomStones: 3, topRecord: 0);

        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(27)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0)));
    }

    [Fact]
    public void BagCountSkipsBankGearAndOtherItems()
    {
        
        
        const uint stone = IllusionPyramidGetMyStateHandler.PhantomStoneItemId;
        var items = new List<InventoryItem>
        {
            new() { ItemId = stone, Count = 99, StorageType = 0 },
            new() { ItemId = stone, Count = 3, StorageType = 0 },
            new() { ItemId = stone, Count = 99, StorageType = 2 },  
            new() { ItemId = stone, Count = 1, StorageType = 1 },   
            new() { ItemId = 30000267, Count = 50, StorageType = 0 },
            new() { ItemId = stone, Count = 0, StorageType = 0 },
        };

        Assert.Equal(102u, IllusionPyramidGetMyStateHandler.CountInBag(items, stone));
        Assert.Equal(0u, IllusionPyramidGetMyStateHandler.CountInBag(new List<InventoryItem>(), stone));
    }

    [Fact]
    public void MissingMileageRowsGoOutAsZeros()
    {
        var body = IllusionPyramidGetMyStateHandler.BuildMyState2906(Array.Empty<Mileage>(), 0, 0, 0, 0);

        Assert.Equal(IllusionPyramidGetMyStateHandler.MyStateLength, body.Length);
        Assert.True(body.AsSpan(3, 16).ToArray().All(b => b == 0));
        Assert.Equal(IllusionPyramidGetMyStateHandler.PhantomStoneItemId, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(19)));
    }
}
