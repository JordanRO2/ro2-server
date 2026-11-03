using Microsoft.Extensions.Logging.Abstractions;
using RO2.Collections.Application;
using RO2.Collections.Domain;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.CollectionCharacterization.CollData;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>B10 S7: the read port the world-entry and warp bursts take the book counts and the active pet from returns the same values CollectionService.GetStateAsync gave them.</summary>
[Collection(GameDataStoreLoadedCollection.Name)]
public class CollectionBookViewAdapterTests
{
    private const uint C = 7;

    private static async Task<(ICollectionBookView View, FakeCollectionRepo Repo, CollectionService Svc)> NewAsync()
    {
        var repo = new FakeCollectionRepo();
        var svc = new CollectionService(repo, await StoreAsync(), NullLogger.Instance);
        return (new CollectionBookViewAdapter(svc), repo, svc);
    }

    [Fact]
    public async Task GetBook_returns_the_counts_the_active_pet_and_the_flag_derived_from_loyalty()
    {
        var (view, repo, svc) = await NewAsync();
        repo.Seed(new CollectionDocument { Id = C, RegisteredMonsterIds = { Mon, MonSingle }, RegisteredPetIds = { Pet }, ActivePetId = Pet, ActivePetFlag = 9, PetLoyalties = { new PetLoyalty { PetId = Pet, Loyalty = 25 } } });

        var book = await view.GetBookAsync(C);
        var doc = await svc.GetStateAsync(C);

        Assert.Equal(new CollectionBook(2, 1, Pet, doc.ActivePetFlag), book);
        Assert.Equal((byte)1, book.ActivePetFlag);                    // re-derived from loyalty 25, not the stored 9
    }

    [Fact]
    public async Task GetBook_of_an_unknown_character_is_the_empty_book()
    {
        var (view, _, _) = await NewAsync();

        Assert.Equal(new CollectionBook(0, 0, 0, 0), await view.GetBookAsync(C));
    }
}
