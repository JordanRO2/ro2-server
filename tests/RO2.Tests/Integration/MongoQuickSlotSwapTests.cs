using RO2.Characters.Domain;
using RO2.Characters.Infrastructure.Mongo;
using RO2.Persistence.Mongo.Persistence;
using Xunit;

namespace RO2.Tests.Integration;

// Audit [29]: the Mongo swap moves the two documents by _id instead of clear,clear,set,set, against a REAL mongo:7 container.
[Collection("MongoMail")]
public sealed class MongoQuickSlotSwapTests
{
    private readonly MongoMailFixture _fx;
    public MongoQuickSlotSwapTests(MongoMailFixture fx) => _fx = fx;

    private static CharacterQuickSlot Entry(uint ch, byte page, byte slot, uint entryId) =>
        new() { CharacterId = ch, Page = page, Slot = slot, EntryType = 2, EntryId = entryId, Amount = 1 };

    private QuickSlotRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new QuickSlotRepository(new MongoDbContext(_fx.ConnectionString, "ro2_it_quickslot"));
    }

    [DockerFact]
    public async Task Swap_two_occupied_slots_exchanges_them_and_keeps_both()
    {
        var repo = Repo();
        const uint ch = 910001;
        await repo.SetAsync(Entry(ch, 0, 1, 111));
        await repo.SetAsync(Entry(ch, 0, 2, 222));

        await repo.SwapAsync(ch, 0, 1, 0, 2);

        var got = await repo.GetByCharacterAsync(ch);
        Assert.Equal([(0, 1, 222u), (0, 2, 111u)], got.Select(s => ((int)s.Page, (int)s.Slot, s.EntryId)));
    }

    [DockerFact]
    public async Task Swap_with_an_empty_slot_moves_the_entry()
    {
        var repo = Repo();
        const uint ch = 910002;
        await repo.SetAsync(Entry(ch, 0, 1, 111));

        await repo.SwapAsync(ch, 0, 1, 0, 5);
        await repo.SwapAsync(ch, 0, 7, 0, 8);   // both empty: no-op

        var got = await repo.GetByCharacterAsync(ch);
        Assert.Equal([(0, 5, 111u)], got.Select(s => ((int)s.Page, (int)s.Slot, s.EntryId)));
    }
}
