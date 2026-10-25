using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// RMI 1349 ReqCraftItem refusals: the 2539 OnAckMakeCraftItem result codes and the checks that select them
/// (<see cref="CraftItemHandler.RefusalCode"/>, <see cref="CraftItemHandler.TryClaimBatch"/>).
/// </summary>
public class CraftRefusalTests
{
    private static readonly Lazy<IReadOnlyList<CraftItemList>> Recipes =
        new(() => BankBagSizeChange2345Tests.Table<CraftItemList>("Craft_ItemList.json"));

    private static readonly Lazy<IReadOnlyList<CraftMaterial>> Materials =
        new(() => BankBagSizeChange2345Tests.Table<CraftMaterial>("Craft_Material.json"));

    private static CraftItemList? Recipe(ushort id) => Recipes.Value.FirstOrDefault(r => r.ID == id);
    private static CraftMaterial? MaterialRow(ushort id) => Materials.Value.FirstOrDefault(m => m.ID == id);

    [Theory]
    
    
    [InlineData(0x01C65311u, CraftItemHandler.ResultBatchRunning, "010a0a0016e41153c6016358")]
    
    [InlineData(0x0C8EDF11u, CraftItemHandler.ResultNotLearned, "010a0a0016e411df8e0c6458")]
    
    [InlineData(0x0C8EDF11u, CraftItemHandler.ResultNoMaterial, "010a0a0016e411df8e0c6658")]
    public void AckMakeCraftItem2539_ReproducesEachRetailRefusal(uint crafter, short result, string retailHex) =>
        Assert.Equal(Convert.FromHexString(retailHex), ShopItemPackets.BuildAckMakeCraftItem2539(crafter, result));

    [Fact]
    public void AckMakeCraftItem2539_DefaultIsTheStartAck() =>
        
        
        Assert.Equal(Convert.FromHexString("010a0a0016e411bb5f0e0000"),
            ShopItemPackets.BuildAckMakeCraftItem2539(0x0E5FBB11u));

    [Fact]
    public void Request1349_OfTheNotLearnedRefusal_NamesRecipe1700()
    {
        
        var body = Convert.FromHexString("0111110015e411df8e0c0011df8e0ca4060100");
        Assert.True(((ReadOnlySpan<byte>)body).TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob));
        Assert.Equal((ushort)1700, BitConverter.ToUInt16(blob.Slice(13, 2)));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(blob.Slice(15, 2)));
        
        Assert.Equal(1676, Recipes.Value.Max(r => (int)r.ID));
        Assert.Null(Recipe(1700));
    }

    [Fact]
    public void RecipeWithNoRow_IsNotLearned() =>
        Assert.Equal(CraftItemHandler.ResultNotLearned,
            CraftItemHandler.RefusalCode(Recipe(1700), MaterialRow(1700), new ushort[] { 1700 }, Array.Empty<InventoryItem>()));

    [Fact]
    public void RecipeOutsideTheKnownSet_IsNotLearned()
    {
        
        
        
        var known = CraftRecipeStore.KnownRecipeIds(Recipes.Value, 3, Array.Empty<ushort>());
        Assert.DoesNotContain((ushort)1620, known);
        Assert.Equal(CraftItemHandler.ResultNotLearned,
            CraftItemHandler.RefusalCode(Recipe(1620), MaterialRow(1620), known, BagFor(1620, 1)));
    }

    [Fact]
    public void KnownRecipeShortOfMaterials_IsNoMaterial()
    {
        
        
        var bag = BagFor(1598, 1);
        bag.First(i => i.ItemId == 22402017).Count = 9;
        Assert.Equal(CraftItemHandler.ResultNoMaterial,
            CraftItemHandler.RefusalCode(Recipe(1598), MaterialRow(1598), new ushort[] { 1598 }, bag));
    }

    [Fact]
    public void KnownRecipeWithItsMaterials_IsNotRefused() =>
        Assert.Null(CraftItemHandler.RefusalCode(Recipe(1598), MaterialRow(1598), new ushort[] { 1598 }, BagFor(1598, 1)));

    [Fact]
    public void MaterialsOnlyInTheBank_DoNotCount()
    {
        var bag = BagFor(1598, 1);
        foreach (var item in bag) item.StorageType = 2;
        Assert.Equal(CraftItemHandler.ResultNoMaterial,
            CraftItemHandler.RefusalCode(Recipe(1598), MaterialRow(1598), new ushort[] { 1598 }, bag));
    }

    [Fact]
    public async Task SecondClaimWhileABatchHoldsTheSlot_IsRefused()
    {
        const uint session = 0xA3C0_0001u;
        var first = CraftItemHandler.TryClaimBatch(session, 1620);
        Assert.NotNull(first);
        try
        {
            
            Assert.Null(CraftItemHandler.TryClaimBatch(session, 1620));
        }
        finally
        {
            CraftItemHandler.ReleaseUnstarted(session, first!);
        }

        var again = CraftItemHandler.TryClaimBatch(session, 1620);
        Assert.NotNull(again);
        CraftItemHandler.ReleaseUnstarted(session, again!);
        Assert.False(await CraftItemHandler.StopBatchAsync(session));
    }

    [Fact]
    public async Task CancelDuringTheStartChecks_WaitsForTheStartToBeSent()
    {
        
        
        const uint session = 0xA3C0_0002u;
        var batch = CraftItemHandler.TryClaimBatch(session, 196)!;
        var stop = CraftItemHandler.StopBatchAsync(session);
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);
        CraftItemHandler.ReleaseUnstarted(session, batch);
        Assert.True(await stop);
        Assert.True(batch.Cts.IsCancellationRequested);
    }

 /// <summary>A bag holding exactly enough of <paramref name="recipeId"/>'s materials for <paramref name="items"/>
 /// items, one stack each, from slot 0.</summary>
    private static List<InventoryItem> BagFor(ushort recipeId, int items)
    {
        short slot = 0;
        return CraftItemHandler.MaterialsOf(MaterialRow(recipeId)!)
            .Select(m => new InventoryItem
            {
                Id = (ulong)(5000 + slot), ItemId = m.ItemId, Count = m.Count * items, Slot = slot++, StorageType = 0,
            })
            .ToList();
    }
}
