using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="MonsterCollectionProgressRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers upsert and replace, entry order, field round-trips, the empty list and per-character isolation. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresMonsterCollectionProgressTests
{
    private readonly PostgresFixture _fx;
    public PostgresMonsterCollectionProgressTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(2_200_000_000L, uint.MaxValue);

    private static MonsterCollectionEntry Entry(uint npcId, byte level, uint exp) =>
        new() { NpcId = npcId, Level = level, Exp = exp };

    [Fact]
    public async Task Get_unknown_character_is_null()
    {
        if (!_fx.Available) return;
        var repo = new MonsterCollectionProgressRepository(_fx.Db!);
        Assert.Null(await repo.GetAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Save_then_get_round_trips_entries_in_order()
    {
        if (!_fx.Available) return;
        var repo = new MonsterCollectionProgressRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new MonsterCollectionProgressDocument
        {
            Id = id,
            Entries =
            {
                Entry(11004185, 2, 7),
                Entry(11000001, 4, uint.MaxValue), 
                Entry(11004185, 1, 0),             
            },
        });

        var got = await repo.GetAsync(id);

        Assert.NotNull(got);
        Assert.Equal(id, got!.Id);
        Assert.Collection(got.Entries,
            e => { Assert.Equal(11004185u, e.NpcId); Assert.Equal((byte)2, e.Level); Assert.Equal(7u, e.Exp); },
            e => { Assert.Equal(11000001u, e.NpcId); Assert.Equal((byte)4, e.Level); Assert.Equal(uint.MaxValue, e.Exp); },
            e => { Assert.Equal(11004185u, e.NpcId); Assert.Equal((byte)1, e.Level); Assert.Equal(0u, e.Exp); });
    }

    [Fact]
    public async Task Saved_document_is_usable_through_Find()
    {
        if (!_fx.Available) return;
        var repo = new MonsterCollectionProgressRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = id, Entries = { Entry(42, 3, 9) } });

        var got = await repo.GetAsync(id);

        var hit = got!.Find(42);
        Assert.NotNull(hit);
        Assert.Equal((byte)3, hit!.Level);
        Assert.Equal(9u, hit.Exp);
        Assert.Null(got.Find(43));
    }

    [Fact]
    public async Task Save_twice_replaces_the_whole_document()
    {
        if (!_fx.Available) return;
        var repo = new MonsterCollectionProgressRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new MonsterCollectionProgressDocument
        {
            Id = id,
            Entries = { Entry(1, 1, 1), Entry(2, 2, 2), Entry(3, 3, 3) },
        });

        
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = id, Entries = { Entry(2, 4, 99) } });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        var only = Assert.Single(got!.Entries);
        Assert.Equal(2u, only.NpcId);
        Assert.Equal((byte)4, only.Level);
        Assert.Equal(99u, only.Exp);
    }

    [Fact]
    public async Task Save_empty_entries_stores_an_empty_document()
    {
        if (!_fx.Available) return;
        var repo = new MonsterCollectionProgressRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = id, Entries = { Entry(5, 2, 2) } });

        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = id }); 

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);           
        Assert.Equal(id, got!.Id);
        Assert.Empty(got.Entries);
    }

    [Fact]
    public async Task Characters_are_isolated()
    {
        if (!_fx.Available) return;
        var repo = new MonsterCollectionProgressRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = a, Entries = { Entry(10, 1, 1) } });
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = b, Entries = { Entry(20, 2, 2) } });

        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = a, Entries = { Entry(10, 3, 3) } });

        var gotA = await repo.GetAsync(a);
        var gotB = await repo.GetAsync(b);
        var ea = Assert.Single(gotA!.Entries);
        Assert.Equal((byte)3, ea.Level);
        var eb = Assert.Single(gotB!.Entries);
        Assert.Equal(20u, eb.NpcId);
        Assert.Equal((byte)2, eb.Level);
        Assert.Equal(2u, eb.Exp);
    }
}
