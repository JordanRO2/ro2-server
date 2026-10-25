using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// 2588 AnsRegistCardBook: the body (<see cref="CardPackets.BuildRegistCardBook2588"/>), which newly arrived
/// cards are announced (<see cref="CardRegistrationNotifier.CardsToAnnounce"/>), and which held cards are
/// announced at world entry (<see cref="CardRegistrationNotifier.HeldMvpCards"/>).
/// </summary>
public class CardRegistration2588Tests
{
 /// <summary>Socket removal of MVP card 22320029 (1544).</summary>
    private const string RetailMvp22320029 = "0a00000001009d93540102000000";

 /// <summary>Quest reward card 22310555 (1378), new to the card book.</summary>
    private const string RetailCard22310555 = "0a00000001009b6e540102000000";

    [Theory]
    [InlineData(22320029u, RetailMvp22320029)]
    [InlineData(22310555u, RetailCard22310555)]
    public void TheBodyIsRetailsForBothCardTypes(uint cardId, string retail)
        => Assert.Equal(Convert.FromHexString(retail), CardPackets.BuildRegistCardBook2588(cardId));

    private static byte? TypeOf(uint itemId) => itemId switch
    {
        22310555 or 22310556 => CardRegistryStore.ListedCardItemType,   
        22320029 => CardRegistrationNotifier.MvpCardItemType,         
        _ => null,                                                    
    };

    [Fact]
    public void ANewListableCardIsRegisteredAsNewAndAnnouncedOnce()
    {
        var registry = new List<CardRegistryEntry>();

        var announce = CardRegistrationNotifier.CardsToAnnounce(registry, new[] { 22310555u, 22310555u }, TypeOf, out bool changed);

        Assert.Equal(new[] { 22310555u }, announce);
        Assert.True(changed);
        var entry = Assert.Single(registry);
        Assert.Equal(22310555u, entry.CardId);
        Assert.Equal(CardRegistryStore.StateNew, entry.State);
    }

    [Fact]
    public void ARegisteredCardIsNotAnnouncedAgain()
    {
        var registry = new List<CardRegistryEntry> { new() { CardId = 22310555u, State = CardRegistryStore.StateSeen } };

        var announce = CardRegistrationNotifier.CardsToAnnounce(registry, new[] { 22310555u }, TypeOf, out bool changed);

        Assert.Empty(announce);
        Assert.False(changed);
        Assert.Equal(CardRegistryStore.StateSeen, Assert.Single(registry).State);
    }

    [Fact]
    public void AnMvpCardIsAnnouncedEveryTimeAndNeverStored()
    {
        var registry = new List<CardRegistryEntry>();

        var first = CardRegistrationNotifier.CardsToAnnounce(registry, new[] { 22320029u }, TypeOf, out bool changed1);
        var second = CardRegistrationNotifier.CardsToAnnounce(registry, new[] { 22320029u }, TypeOf, out bool changed2);

        Assert.Equal(new[] { 22320029u }, first);
        Assert.Equal(new[] { 22320029u }, second);
        Assert.False(changed1);
        Assert.False(changed2);
        Assert.Empty(registry);
    }

    [Fact]
    public void ItemsThatAreNoCardsGetNothing()
    {
        var registry = new List<CardRegistryEntry>();

        var announce = CardRegistrationNotifier.CardsToAnnounce(registry, new[] { 66666666u, 0u, 22310556u }, TypeOf, out _);

        Assert.Equal(new[] { 22310556u }, announce);
    }

    [Fact]
    public async Task WorldEntryAnnouncesTheMvpCardsInTheBagOnly()
    {
        var store = await CardStoreAsync();
        var items = new List<InventoryItem>
        {
            new() { ItemId = 22320029, Count = 1, StorageType = 0, Slot = 3 },
            new() { ItemId = 22320029, Count = 2, StorageType = 0, Slot = 4 },   
            new() { ItemId = 22320049, Count = 1, StorageType = 2, Slot = 0 },   
            new() { ItemId = 22310555, Count = 1, StorageType = 0, Slot = 5 },   
            new() { ItemId = 66666666, Count = 1, StorageType = 0, Slot = 6 },   
        };

        Assert.Equal(new[] { 22320029u }, CardRegistrationNotifier.HeldMvpCards(items, store));
    }

 /// <summary>ItemInfo and CardInfo rows of the cards above, with their shipped Item_Type.</summary>
    private static async Task<GameDataStore> CardStoreAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-card2588-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":22320029,"Item_Type":139},{"ID":22320049,"Item_Type":139},
                 {"ID":22310555,"Item_Type":13},{"ID":66666666,"Item_Type":33}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "CardInfo.json"), """
                [{"Card_ID":22320029,"Grade_Type":5},{"Card_ID":22320049,"Grade_Type":5},{"Card_ID":22310555,"Grade_Type":2}]
                """);
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
