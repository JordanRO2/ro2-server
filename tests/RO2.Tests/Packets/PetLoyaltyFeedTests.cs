using RO2.Domain.GameData;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Pet-collection loyalty: the +10 per fed egg (PetCollectionBook.FedLoyalty), the stored loyalty on CollectionDocument,
/// and the 2916, 2918, 2919, 2920 and 2925 bodies built from it.
/// </summary>
/// <remarks>
/// Packet bodies are compared byte for byte. The header size follows the encryption mode: 3 bytes when unencrypted,
/// 5 bytes for RC4 or AES.
/// </remarks>
public class PetLoyaltyFeedTests
{
    private const uint Pet83000005 = 83000005, Pet83000015 = 83000015, Pet83000016 = 83000016, Pet83000019 = 83000019;

    private static byte[] Hex(string s) => Convert.FromHexString(s);

 /// <summary>
 /// A feed adds 10 loyalty and stops at PetInfo.LoyaltyLevel_1 (500 for pets 83000016 and 83000019, 100 for 83000001).
 /// At the cap there is nothing to feed.
 /// </summary>
    [Fact]
    public async Task FeedAddsTenUpToTheTopLevelThreshold()
    {
        var store = await LoadPetInfoAsync();
        var pet19 = MonsterCollectionRules.Pet(store, Pet83000019)!;
        var pet16 = MonsterCollectionRules.Pet(store, Pet83000016)!;
        var pet01 = MonsterCollectionRules.Pet(store, 83000001)!;

        Assert.Equal((ushort)500, pet19.LoyaltyLevel_1);
        Assert.Equal((ushort)20, PetCollectionBook.FedLoyalty(pet19, 10));
        Assert.Equal((ushort)10, PetCollectionBook.FedLoyalty(pet19, 0));
        Assert.Equal((ushort)500, PetCollectionBook.FedLoyalty(pet16, 490));
        Assert.Null(PetCollectionBook.FedLoyalty(pet16, 500));
        
        Assert.Equal((ushort)100, PetCollectionBook.FedLoyalty(pet01, 95));
        Assert.Null(PetCollectionBook.FedLoyalty(pet01, 100));
    }

 /// <summary>
 /// The responses to one feed of a pet at loyalty 10 are 2920, 2918, 2919 and 2925, with flag 2 and message 16093
 /// ("@1 Egg successfully fed to @2.").
 /// </summary>
    [Fact]
    public async Task FeedWindowBodiesAreRetailsFrames()
    {
        var store = await LoadPetInfoAsync();
        var pet = MonsterCollectionRules.Pet(store, Pet83000019)!;

        var fed = PetCollectionBook.FedLoyalty(pet, 10)!.Value;
        var record = PetCollectionBook.State(store, Pet83000019, fed);

        Assert.Equal(Hex("d37af2047d340300001400"), CollectionPackets.BuildNfyPCollectionSkill(record));
        Assert.Equal(Hex("d37af204000000"),
            CollectionPackets.BuildAnsPCollectionSetActivePet(Pet83000019, record.LoyaltyLevel, 0));
        Assert.Equal(Hex("03000000"), CollectionPackets.BuildNfyPCollection(3));
        Assert.Equal(Hex("d37af204020000"), CollectionPackets.BuildAnsPCollectionUsePetEgg(Pet83000019, 2, 0));
    }

 /// <summary>
 /// The feed that reaches the threshold switches the record to LoyaltyEffect_1 and level 1. A level-1 pet reports 1 in
 /// its 2918 body.
 /// </summary>
    [Fact]
    public async Task FeedToTheThresholdRaisesTheLevel()
    {
        var store = await LoadPetInfoAsync();
        var pet = MonsterCollectionRules.Pet(store, Pet83000016)!;

        Assert.Equal(Hex("d07af2047734030000ea01"),
            CollectionPackets.BuildNfyPCollectionSkill(PetCollectionBook.State(store, Pet83000016, 490)));

        var fed = PetCollectionBook.FedLoyalty(pet, 490)!.Value;
        Assert.Equal(Hex("d07af2047834030001f401"),
            CollectionPackets.BuildNfyPCollectionSkill(PetCollectionBook.State(store, Pet83000016, fed)));

        Assert.Equal((byte)1, PetCollectionBook.ActiveFlag(store, Pet83000016, fed));
        Assert.Equal(Hex("d07af204010000"),
            CollectionPackets.BuildAnsPCollectionSetActivePet(Pet83000016, PetCollectionBook.ActiveFlag(store, Pet83000016, fed), 0));
        Assert.Equal((byte)0, PetCollectionBook.ActiveFlag(store, Pet83000016, 490));
    }

 /// <summary>
 /// The 2916 book lists each pet's stored loyalty, ascending by id regardless of registration order.
 /// </summary>
    [Fact]
    public async Task BookCarriesTheStoredLoyalties()
    {
        var store = await LoadPetInfoAsync();
        var book = new CollectionDocument
        {
            Id = 1,
            RegisteredPetIds = { Pet83000019, Pet83000005, Pet83000016, Pet83000015 },
        };
        book.SetLoyalty(Pet83000005, 400);
        book.SetLoyalty(Pet83000015, 500);
        book.SetLoyalty(Pet83000016, 490);
        book.SetLoyalty(Pet83000016, 500); 
        book.SetLoyalty(Pet83000019, 500);

        Assert.Equal(4, book.PetLoyalties.Count);
        Assert.Equal((ushort)500, book.LoyaltyOf(Pet83000016));
        Assert.Equal((ushort)0, book.LoyaltyOf(83000001)); 

        var body = CollectionPackets.BuildAnsPCollection(PetCollectionBook.BookStates(store, book));
        Assert.Equal(Hex("040000000104c57af20464340300019001cf7af2047634030001f401d07af2047834030001f401" +
                         "d37af2047e34030001f4010000"), body);
    }

 /// <summary>
 /// A pet never fed keeps the unfed record: LoyaltyEffect_0 210027, level 0, loyalty 0.
 /// </summary>
    [Fact]
    public async Task UnfedPetIsTheLoyaltyZeroRecord()
    {
        var store = await LoadPetInfoAsync();
        var book = new CollectionDocument { Id = 1, RegisteredPetIds = { 83000009 } };

        var records = PetCollectionBook.BookStates(store, book);

        Assert.Single(records);
        Assert.Equal(Hex("c97af2046b340300000000"), CollectionPackets.BuildNfyPCollectionSkill(records[0]));
    }

 /// <summary>A GameDataStore holding the shipped PetInfo table only.</summary>
    private static async Task<GameDataStore> LoadPetInfoAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-petloyalty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            
            File.Copy(Path.Combine(RequireDataDirectory(), "PetInfo.json"), Path.Combine(dir, "PetInfo.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            Assert.NotEmpty(store.PetInfo);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "PetInfo.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "no ancestor of " + AppContext.BaseDirectory + " holds data/PetInfo.json");
    }
}
