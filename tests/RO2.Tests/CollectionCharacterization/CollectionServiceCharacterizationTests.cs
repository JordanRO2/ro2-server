using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.CollectionCharacterization.CollData;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>
/// B10 S1: <see cref="CollectionService"/> and <see cref="MonsterCollectionProgressStore"/> over the in-memory ports: the register / select
/// / feed / activate decisions, the log-and-swallow storage policy, and the NON-ATOMIC whole-document read-modify-write (a lost update
/// is pinned as the known, preserved behaviour; there is no compare-and-set on any of the three ports).
/// </summary>
public class CollectionServiceCharacterizationTests
{
    private const uint C = 7;

    private static async Task<(CollectionService Svc, FakeCollectionRepo Repo)> NewAsync(bool withStore = true)
    {
        var repo = new FakeCollectionRepo();
        return (new CollectionService(repo, withStore ? await StoreAsync() : null, NullLogger.Instance), repo);
    }

    // ------------------------------------------------------------------------------------------------ register and select

    [Fact]
    public async Task Register_is_idempotent_ignores_id_0_and_returns_the_registered_count_without_saving_when_nothing_changed()
    {
        var (svc, repo) = await NewAsync();

        Assert.Equal(1, await svc.RegisterMonsterAsync(C, Mon));
        Assert.Equal(1, await svc.RegisterMonsterAsync(C, Mon));      // already in
        Assert.Equal(1, await svc.RegisterMonsterAsync(C, 0));        // 0 is never registered
        Assert.Equal(1, repo.Saves);                                  // only the first call wrote
        Assert.Equal(2, await svc.RegisterMonsterAsync(C, MonSingle));
        Assert.Equal(1, await svc.RegisterPetAsync(C, Pet));
        Assert.Equal(1, await svc.RegisterPetAsync(C, Pet));
        Assert.Equal(1, await svc.RegisterPetAsync(C, 0));
        Assert.Equal(new uint[] { Mon, MonSingle }, repo.Stored(C)!.RegisteredMonsterIds);                // insertion order, not sorted
        Assert.Equal(new uint[] { Pet }, repo.Stored(C)!.RegisteredPetIds);
    }

    [Fact]
    public async Task SelectMonsterSkill_stores_the_selection_without_registering_and_returns_whether_it_is_registered()
    {
        var (svc, repo) = await NewAsync();

        Assert.False(await svc.SelectMonsterSkillAsync(C, Mon));
        Assert.False(await svc.SelectMonsterSkillAsync(C, 0));        // 0: not stored, and registered-contains(0) is false
        await svc.RegisterMonsterAsync(C, Mon);
        Assert.True(await svc.SelectMonsterSkillAsync(C, Mon));       // already selected: not duplicated
        Assert.Equal(new uint[] { Mon }, repo.Stored(C)!.SelectedMonsterSkills);
    }

    [Fact]
    public async Task SelectPetSkill_returns_the_document_with_the_derived_flag_and_does_not_register()
    {
        var (svc, repo) = await NewAsync();
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { Pet }, ActivePetId = Pet, ActivePetFlag = 9, PetLoyalties = { new PetLoyalty { PetId = Pet, Loyalty = 25 } } });

        var doc = await svc.SelectPetSkillAsync(C, PetBig);

        Assert.Equal(new uint[] { PetBig }, doc.SelectedPetSkills);
        Assert.Equal(new uint[] { Pet }, doc.RegisteredPetIds);
        Assert.Equal((byte)1, doc.ActivePetFlag);                     // re-derived from loyalty 25, not the stored 9
    }

    // ------------------------------------------------------------------------------------------------ feed

    [Fact]
    public async Task FeedPet_returns_null_for_an_unregistered_pet_a_missing_row_a_null_store_and_a_pet_at_its_cap()
    {
        var (svc, repo) = await NewAsync();
        Assert.Null(await svc.FeedPetAsync(C, Pet));                  // not in the book
        await svc.RegisterPetAsync(C, PetUnknown);
        Assert.Null(await svc.FeedPetAsync(C, PetUnknown));           // booked but PetInfo has no row
        await svc.RegisterPetAsync(C, Pet);

        Assert.Equal((ushort)10, await svc.FeedPetAsync(C, Pet));
        Assert.Equal((ushort)20, await svc.FeedPetAsync(C, Pet));
        Assert.Equal((ushort)25, await svc.FeedPetAsync(C, Pet));
        var saves = repo.Saves;
        Assert.Null(await svc.FeedPetAsync(C, Pet));                  // at the cap
        Assert.Equal(saves, repo.Saves);                              // a refused feed writes nothing

        var (noData, repo2) = await NewAsync(withStore: false);
        await noData.RegisterPetAsync(C, Pet);
        Assert.Null(await noData.FeedPetAsync(C, Pet));
        Assert.Empty(repo2.Stored(C)!.PetLoyalties);
    }

    [Fact]
    public async Task SetActivePet_stores_only_registered_pets_a_refusal_returns_the_current_pair_and_pet_0_clears()
    {
        var (svc, repo) = await NewAsync();
        await svc.RegisterPetAsync(C, Pet);
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { Pet }, PetLoyalties = { new PetLoyalty { PetId = Pet, Loyalty = 25 } } });

        Assert.Equal((0u, (byte)0), await svc.SetActivePetAsync(C, PetBig));      // refused: the (unchanged) current pair
        Assert.Equal((Pet, (byte)1), await svc.SetActivePetAsync(C, Pet));
        Assert.Equal((Pet, (byte)1), await svc.GetActivePetAsync(C));
        Assert.Equal((Pet, (byte)1), await svc.SetActivePetAsync(C, PetBig));     // refused: still Pet
        Assert.Equal((0u, (byte)0), await svc.SetActivePetAsync(C, 0));           // cleared
        Assert.Equal((0u, (byte)0), await svc.GetActivePetAsync(C));
        Assert.Equal(new uint[] { Pet }, repo.Stored(C)!.RegisteredPetIds);       // activating never registers
    }

    // ------------------------------------------------------------------------------------------------ storage failure policy

    [Fact]
    public async Task A_load_failure_yields_an_empty_default_document_and_a_save_failure_is_swallowed_the_caller_still_gets_its_result()
    {
        var (svc, repo) = await NewAsync();
        repo.Seed(new CollectionDocument { Id = C, RegisteredMonsterIds = { Mon, MonSingle } });

        repo.ThrowOnGet = true;
        Assert.Equal(1, await svc.RegisterMonsterAsync(C, 99));       // the load threw: the service works on an EMPTY document ...
        repo.ThrowOnGet = false;
        Assert.Equal(new uint[] { 99 }, repo.Stored(C)!.RegisteredMonsterIds);   // ... and then SAVES it: QUIRK, the stored registrations Mon and MonSingle are overwritten
        repo.Seed(new CollectionDocument { Id = C, RegisteredMonsterIds = { Mon, MonSingle } });

        repo.ThrowOnSave = true;
        Assert.Equal(3, await svc.RegisterMonsterAsync(C, MonNoRecipe));    // the count includes the unsaved registration
        Assert.Equal(2, repo.Stored(C)!.RegisteredMonsterIds.Count);        // QUIRK: the client was told 3, storage holds 2
    }

    [Fact]
    public async Task The_progress_store_returns_an_empty_document_on_a_load_error_and_swallows_a_save_error()
    {
        var repo = new FakeMonsterProgressRepo();
        var store = MonsterCollectionProgressStore.From(new SingleServiceProvider<IMonsterCollectionProgressRepository>(repo), NullLogger.Instance);
        repo.Seed(new MonsterCollectionProgressDocument { Id = C, Entries = { new MonsterCollectionEntry { NpcId = Mon, Level = 3, Exp = 1 } } });

        Assert.Equal(3, (await store.GetAsync(C)).Find(Mon)!.Level);
        var fresh = await store.GetAsync(99);                                    // none stored: an empty document for that id, not null
        Assert.Equal((99u, 0), (fresh.Id, fresh.Entries.Count));

        repo.ThrowOnGet = true;
        var empty = await store.GetAsync(C);
        Assert.Equal((C, 0), (empty.Id, empty.Entries.Count));                   // the stored (3,1) is invisible on a load error
        repo.ThrowOnGet = false;

        repo.ThrowOnSave = true;
        await store.SaveAsync(new MonsterCollectionProgressDocument { Id = C });  // no throw
        Assert.Single(repo.Stored(C)!.Entries);                                  // and nothing changed
    }

    // ------------------------------------------------------------------------------------------------ non-atomic whole-document RMW

    [Fact]
    public async Task QUIRK_two_concurrent_feeds_of_one_pet_both_read_the_same_loyalty_so_one_feed_is_lost_last_writer_wins()
    {
        var (svc, repo) = await NewAsync();
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { Pet } });

        repo.HoldLoadsUntil(2);                                        // both requests load before either saves
        var results = await Task.WhenAll(svc.FeedPetAsync(C, Pet), svc.FeedPetAsync(C, Pet));

        Assert.Equal(new ushort?[] { 10, 10 }, results);              // each saw loyalty 0 and answered +10
        Assert.Equal((ushort)10, repo.Stored(C)!.LoyaltyOf(Pet));     // two eggs' worth was applied once (the host still consumed two eggs)
        Assert.Equal(2, repo.Saves);
    }

    [Fact]
    public async Task QUIRK_two_concurrent_registrations_of_different_things_overwrite_each_other_whole_document()
    {
        var (svc, repo) = await NewAsync();

        repo.HoldLoadsUntil(2);
        await Task.WhenAll(svc.RegisterMonsterAsync(C, Mon), svc.RegisterPetAsync(C, Pet));

        var stored = repo.Stored(C)!;
        Assert.Equal(1, stored.RegisteredMonsterIds.Count + stored.RegisteredPetIds.Count);   // exactly one survived: the later whole-document write replaced the other
        Assert.Equal(2, repo.Saves);
    }

    [Fact]
    public void The_three_repository_ports_offer_only_a_read_and_a_whole_document_write_no_compare_and_set()
    {
        static string[] Members(Type t) => [.. t.GetMethods().Select(m => m.Name).Order()];

        Assert.Equal(new[] { "GetOrCreateAsync", "SaveAsync" }, Members(typeof(ICollectionRepository)));
        Assert.Equal(new[] { "GetAsync", "SaveAsync" }, Members(typeof(IMonsterCollectionProgressRepository)));
        Assert.Equal(new[] { "GetSlotsAsync", "RestoreSlotAsync", "TryCollectAsync", "TryPlaceIncubatorAsync", "TryStartIncubationAsync" },
            Members(typeof(IPetIncubatorRepository)));
    }

    /// <summary>A minimal <see cref="IServiceProvider"/> that serves one service.</summary>
    private sealed class SingleServiceProvider<T>(T service) : IServiceProvider where T : class
    {
        public object? GetService(Type serviceType) => serviceType == typeof(T) ? service : null;
    }
}
