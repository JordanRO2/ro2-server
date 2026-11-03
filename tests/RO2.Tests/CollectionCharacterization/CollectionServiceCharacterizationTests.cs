using Microsoft.Extensions.Logging.Abstractions;
using RO2.Collections.Application;
using RO2.Collections.Domain;
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

    /// <summary>The monster use case over the same book repo (the host passes the derived-flag delegate; none of these tests reads the flag).</summary>
    private static MonsterCollectionUseCase Uc(FakeCollectionRepo repo, FakeMonsterProgressRepo? progress = null) =>
        new(repo, progress ?? new FakeMonsterProgressRepo(), _ => 0);

    /// <summary>The pet use case over the same book repo, with the synthetic PetInfo rows (or none).</summary>
    private static async Task<PetCollectionUseCase> PetUcAsync(FakeCollectionRepo repo, bool withStore = true) =>
        new(repo, withStore ? (await StoreAsync()).PetInfo : null);

    // ------------------------------------------------------------------------------------------------ register and select

    [Fact]
    public async Task Register_is_idempotent_ignores_id_0_and_returns_the_registered_count_without_saving_when_nothing_changed()
    {
        var (_, repo) = await NewAsync();
        var uc = Uc(repo);
        var pets = await PetUcAsync(repo);

        Assert.Equal(1, await uc.RegisterAsync(C, Mon));
        Assert.Equal(1, await uc.RegisterAsync(C, Mon));              // already in
        Assert.Equal(1, await uc.RegisterAsync(C, 0));                // 0 is never registered
        Assert.Equal(1, repo.Saves);                                  // only the first call wrote
        Assert.Equal(2, await uc.RegisterAsync(C, MonSingle));
        Assert.Equal(1, await pets.RegisterAsync(C, Pet));
        Assert.Equal(1, await pets.RegisterAsync(C, Pet));
        Assert.Equal(1, await pets.RegisterAsync(C, 0));
        Assert.Equal(new uint[] { Mon, MonSingle }, repo.Stored(C)!.RegisteredMonsterIds);                // insertion order, not sorted
        Assert.Equal(new uint[] { Pet }, repo.Stored(C)!.RegisteredPetIds);
    }

    [Fact]
    public async Task SelectMonsterSkill_stores_the_selection_without_registering_and_returns_whether_it_is_registered()
    {
        var (_, repo) = await NewAsync();
        var uc = Uc(repo);
        var rows = (await StoreAsync()).CollectMonster;

        Assert.False((await uc.SelectAsync(C, Mon, rows)).Registered);
        Assert.False((await uc.SelectAsync(C, 0, rows)).Registered);  // 0: not stored, and registered-contains(0) is false
        await uc.RegisterAsync(C, Mon);
        Assert.True((await uc.SelectAsync(C, Mon, rows)).Registered); // already selected: not duplicated
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
        var (_, repo) = await NewAsync();
        var pets = await PetUcAsync(repo);
        Assert.Null(await pets.FeedAsync(C, Pet));                    // not in the book
        await pets.RegisterAsync(C, PetUnknown);
        Assert.Null(await pets.FeedAsync(C, PetUnknown));             // booked but PetInfo has no row
        await pets.RegisterAsync(C, Pet);

        Assert.Equal((ushort)10, await pets.FeedAsync(C, Pet));
        Assert.Equal((ushort)20, await pets.FeedAsync(C, Pet));
        Assert.Equal((ushort)25, await pets.FeedAsync(C, Pet));
        var saves = repo.Saves;
        Assert.Null(await pets.FeedAsync(C, Pet));                    // at the cap
        Assert.Equal(saves, repo.Saves);                              // a refused feed writes nothing

        var (_, repo2) = await NewAsync(withStore: false);
        var noData = await PetUcAsync(repo2, withStore: false);
        await noData.RegisterAsync(C, Pet);
        Assert.Null(await noData.FeedAsync(C, Pet));
        Assert.Empty(repo2.Stored(C)!.PetLoyalties);
    }

    [Fact]
    public async Task SetActivePet_stores_only_registered_pets_a_refusal_returns_the_current_pair_and_pet_0_clears()
    {
        var (_, repo) = await NewAsync();
        var pets = await PetUcAsync(repo);
        await pets.RegisterAsync(C, Pet);
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { Pet }, PetLoyalties = { new PetLoyalty { PetId = Pet, Loyalty = 25 } } });

        Assert.Equal(new ActivePetOutcome(false, 0, 0), await pets.SetActiveAsync(C, PetBig));      // refused: the (unchanged) current pair
        Assert.Equal(new ActivePetOutcome(true, Pet, 1), await pets.SetActiveAsync(C, Pet));
        Assert.Equal((Pet, (byte)1), await pets.GetActiveAsync(C));
        Assert.Equal(new ActivePetOutcome(false, Pet, 1), await pets.SetActiveAsync(C, PetBig));     // refused: still Pet
        Assert.Equal(new ActivePetOutcome(true, 0, 0), await pets.SetActiveAsync(C, 0));             // cleared
        Assert.Equal((0u, (byte)0), await pets.GetActiveAsync(C));
        Assert.Equal(new uint[] { Pet }, repo.Stored(C)!.RegisteredPetIds);       // activating never registers
    }

    // ------------------------------------------------------------------------------------------------ egg use (1627)

    [Fact]
    public async Task UseEgg_registers_an_unbooked_pet_then_feeds_it_up_to_its_cap_and_refuses_at_the_cap_without_writing()
    {
        var (_, repo) = await NewAsync();
        var pets = await PetUcAsync(repo);

        Assert.Equal(new PetEggUse(PetEggKind.Registered, 0, 1, false), await pets.UseEggAsync(C, Pet));
        Assert.Equal(new uint[] { Pet }, repo.Stored(C)!.RegisteredPetIds);
        Assert.Empty(repo.Stored(C)!.PetLoyalties);                   // registering writes no loyalty

        Assert.Equal(new PetEggUse(PetEggKind.Fed, 10, 1, false), await pets.UseEggAsync(C, Pet));
        Assert.Equal(new PetEggUse(PetEggKind.Fed, 20, 1, false), await pets.UseEggAsync(C, Pet));
        Assert.Equal(new PetEggUse(PetEggKind.Fed, 25, 1, false), await pets.UseEggAsync(C, Pet));   // min(loyalty + 10, cap)
        var saves = repo.Saves;
        Assert.Equal(new PetEggUse(PetEggKind.Capped, 25, 1, false), await pets.UseEggAsync(C, Pet)); // the 30001 refusal
        Assert.Equal(saves, repo.Saves);
    }

    [Fact]
    public async Task UseEgg_reports_the_book_count_and_whether_the_fed_pet_is_active_as_read_before_the_feed()
    {
        var (_, repo) = await NewAsync();
        var pets = await PetUcAsync(repo);
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { Pet, PetBig }, ActivePetId = Pet });

        Assert.Equal(new PetEggUse(PetEggKind.Fed, 10, 2, true), await pets.UseEggAsync(C, Pet));
        Assert.Equal(new PetEggUse(PetEggKind.Fed, 10, 2, false), await pets.UseEggAsync(C, PetBig));
    }

    [Fact]
    public async Task UseEgg_on_a_booked_pet_with_no_row_is_refused_as_capped_and_writes_nothing()
    {
        var (_, repo) = await NewAsync();
        var pets = await PetUcAsync(repo);
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { PetUnknown } });

        Assert.Equal(new PetEggUse(PetEggKind.Capped, 0, 1, false), await pets.UseEggAsync(C, PetUnknown));
        Assert.Equal(0, repo.Saves);
    }

    [Fact]
    public async Task The_active_flag_is_re_derived_from_the_stored_loyalty_on_every_pet_load()
    {
        var (_, repo) = await NewAsync();
        var pets = await PetUcAsync(repo);
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { Pet }, ActivePetId = Pet, ActivePetFlag = 9, PetLoyalties = { new PetLoyalty { PetId = Pet, Loyalty = 25 } } });

        Assert.Equal((Pet, (byte)1), await pets.GetActiveAsync(C));   // derived 1 from loyalty 25, not the stored 9
    }

    // ------------------------------------------------------------------------------------------------ storage failure policy

    [Fact]
    public async Task A_load_failure_yields_an_empty_default_document_and_a_save_failure_is_swallowed_the_caller_still_gets_its_result()
    {
        var (_, repo) = await NewAsync();
        var uc = Uc(repo);
        repo.Seed(new CollectionDocument { Id = C, RegisteredMonsterIds = { Mon, MonSingle } });

        repo.ThrowOnGet = true;
        Assert.Equal(1, await uc.RegisterAsync(C, 99));       // the load threw: the service works on an EMPTY document ...
        repo.ThrowOnGet = false;
        Assert.Equal(new uint[] { 99 }, repo.Stored(C)!.RegisteredMonsterIds);   // ... and then SAVES it: QUIRK, the stored registrations Mon and MonSingle are overwritten
        repo.Seed(new CollectionDocument { Id = C, RegisteredMonsterIds = { Mon, MonSingle } });

        repo.ThrowOnSave = true;
        Assert.Equal(3, await uc.RegisterAsync(C, MonNoRecipe));    // the count includes the unsaved registration
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
        var (_, repo) = await NewAsync();
        var pets = await PetUcAsync(repo);
        repo.Seed(new CollectionDocument { Id = C, RegisteredPetIds = { Pet } });

        repo.HoldLoadsUntil(2);                                        // both requests load before either saves
        var results = await Task.WhenAll(pets.FeedAsync(C, Pet), pets.FeedAsync(C, Pet));

        Assert.Equal(new ushort?[] { 10, 10 }, results);              // each saw loyalty 0 and answered +10
        Assert.Equal((ushort)10, repo.Stored(C)!.LoyaltyOf(Pet));     // two eggs' worth was applied once (the host still consumed two eggs)
        Assert.Equal(2, repo.Saves);
    }

    [Fact]
    public async Task QUIRK_two_concurrent_registrations_of_different_things_overwrite_each_other_whole_document()
    {
        var (_, repo) = await NewAsync();

        repo.HoldLoadsUntil(2);
        await Task.WhenAll(Uc(repo).RegisterAsync(C, Mon), (await PetUcAsync(repo)).RegisterAsync(C, Pet));

        var stored = repo.Stored(C)!;
        Assert.Equal(1, stored.RegisteredMonsterIds.Count + stored.RegisteredPetIds.Count);   // exactly one survived: the later whole-document write replaced the other
        Assert.Equal(2, repo.Saves);
    }

    [Fact]
    public async Task QUIRK_two_concurrent_feeds_of_one_monster_both_read_the_same_progress_so_one_exp_is_lost_last_writer_wins()
    {
        var repo = new FakeCollectionRepo();
        var progress = new FakeMonsterProgressRepo();
        var rows = (await StoreAsync()).CollectMonster;
        repo.Seed(new CollectionDocument { Id = C, RegisteredMonsterIds = { Mon } });
        var uc = Uc(repo, progress);

        repo.HoldLoadsUntil(2);                                        // both feeds load the book before either saves progress
        var results = await Task.WhenAll(uc.FeedAsync(C, Mon, rows), uc.FeedAsync(C, Mon, rows));

        Assert.All(results, r => Assert.Equal((MonsterFeedKind.ExpGained, (byte)1, 1u), (r.Kind, r.Level, r.Exp)));   // each saw exp 0 and answered exp 1
        Assert.Equal(1u, progress.Stored(C)!.Find(Mon)!.Exp);          // two DNA, one exp (the host still consumed two)
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
