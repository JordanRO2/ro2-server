using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="CardRegistryRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers the empty-registry read, whole-document round trips (order,
/// duplicates, uint ranges), the UpdatedAt stamp, upsert and replace semantics, and per-character isolation.
/// </summary>
/// <remarks>Each test uses a fresh random character id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresCardRegistryTests
{
    private readonly PostgresFixture _fx;
    public PostgresCardRegistryTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(3_000_000_000L, 4_294_967_295L);

    private static CardRegistryEntry Entry(uint cardId, byte state) => new() { CardId = cardId, State = state };

    [Fact]
    public async Task Get_unknown_character_returns_empty_registry_with_id()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();

        var doc = await repo.GetAsync(id);

        Assert.Equal(id, doc.Id);
        Assert.Empty(doc.Cards);
    }

    [Fact]
    public async Task Get_unknown_character_does_not_create_a_row()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();

        await repo.GetAsync(id);
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"SELECT count(*) FROM {CardRegistryRepository.TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)id);
        Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Save_then_get_round_trips_cards_in_order()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();
        var doc = new CardRegistryDocument
        {
            Id = id,
            Cards =
            {
                Entry(4_000_000_000, 2),   
                Entry(300, 1),
                Entry(0, 255),             
                Entry(17, 2),
            },
        };

        await repo.SaveAsync(doc);
        var got = await repo.GetAsync(id);

        Assert.Equal(id, got.Id);
        Assert.Equal(4, got.Cards.Count);
        Assert.Equal(4_000_000_000u, got.Cards[0].CardId);
        Assert.Equal((byte)2, got.Cards[0].State);
        Assert.Equal(300u, got.Cards[1].CardId);
        Assert.Equal((byte)1, got.Cards[1].State);
        Assert.Equal(0u, got.Cards[2].CardId);
        Assert.Equal((byte)255, got.Cards[2].State);
        Assert.Equal(17u, got.Cards[3].CardId);
        Assert.Equal((byte)2, got.Cards[3].State);
    }

    [Fact]
    public async Task Save_preserves_duplicate_entries()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new CardRegistryDocument { Id = id, Cards = { Entry(5, 2), Entry(5, 1), Entry(5, 2) } });

        var got = await repo.GetAsync(id);

        Assert.Equal(3, got.Cards.Count);
        Assert.Equal(new byte[] { 2, 1, 2 }, got.Cards.Select(c => c.State).ToArray());
        Assert.All(got.Cards, c => Assert.Equal(5u, c.CardId));
    }

    [Fact]
    public async Task Save_stamps_updated_at_on_the_callers_document_and_stores_it()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();
        var doc = new CardRegistryDocument { Id = id, Cards = { Entry(1, 2) }, UpdatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        var before = DateTime.UtcNow;
        await repo.SaveAsync(doc);
        var after = DateTime.UtcNow;

        
        Assert.InRange(doc.UpdatedAt, before, after);

        var got = await repo.GetAsync(id);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedAt.Kind);
        
        Assert.True(Math.Abs((got.UpdatedAt - doc.UpdatedAt).TotalMilliseconds) < 1.0);
    }

    [Fact]
    public async Task Save_existing_replaces_the_whole_document_and_advances_updated_at()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new CardRegistryDocument { Id = id, Cards = { Entry(1, 2), Entry(2, 2), Entry(3, 2) } });
        var first = await repo.GetAsync(id);

        await Task.Delay(20);
        
        first.Cards.Clear();
        first.Cards.Add(Entry(2, 1));
        await repo.SaveAsync(first);
        var second = await repo.GetAsync(id);

        Assert.Single(second.Cards);
        Assert.Equal(2u, second.Cards[0].CardId);
        Assert.Equal((byte)1, second.Cards[0].State);
        Assert.True(second.UpdatedAt > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(Math.Abs((second.UpdatedAt - first.UpdatedAt).TotalMilliseconds) < 1.0);
    }

    [Fact]
    public async Task Save_empty_card_list_round_trips_as_empty_but_existing()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.SaveAsync(new CardRegistryDocument { Id = id, Cards = { Entry(9, 2) } });

        await repo.SaveAsync(new CardRegistryDocument { Id = id }); 
        var got = await repo.GetAsync(id);

        Assert.Equal(id, got.Id);
        Assert.Empty(got.Cards);
        
        Assert.True(Math.Abs((DateTime.UtcNow - got.UpdatedAt).TotalMinutes) < 5);
    }

    [Fact]
    public async Task Characters_are_isolated_from_each_other()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        await repo.SaveAsync(new CardRegistryDocument { Id = a, Cards = { Entry(10, 2) } });
        await repo.SaveAsync(new CardRegistryDocument { Id = b, Cards = { Entry(20, 1), Entry(21, 1) } });

        var ga = await repo.GetAsync(a);
        var gb = await repo.GetAsync(b);

        Assert.Single(ga.Cards);
        Assert.Equal(10u, ga.Cards[0].CardId);
        Assert.Equal(2, gb.Cards.Count);
        Assert.Equal(20u, gb.Cards[0].CardId);
        Assert.Equal(21u, gb.Cards[1].CardId);
    }

    [Fact]
    public async Task Concurrent_saves_last_writer_wins_without_error()
    {
        if (!_fx.Available) return;
        var repo = new CardRegistryRepository(_fx.Db!);
        var id = NewCharacterId();

        
        await Task.WhenAll(Enumerable.Range(1, 8).Select(n =>
            repo.SaveAsync(new CardRegistryDocument { Id = id, Cards = { Entry((uint)n, 2), Entry((uint)n + 100, 1) } })));

        var got = await repo.GetAsync(id);
        Assert.Equal(2, got.Cards.Count);
        var n0 = got.Cards[0].CardId;
        Assert.InRange(n0, 1u, 8u);
        Assert.Equal(n0 + 100, got.Cards[1].CardId);
        Assert.Equal((byte)2, got.Cards[0].State);
        Assert.Equal((byte)1, got.Cards[1].State);
    }
}
