using Npgsql;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="CollectionRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers the default for an unpersisted collection, whole-document upsert
/// (first save and replace), list order and duplicate round-trips, ids above <see cref="int.MaxValue"/>, and
/// per-character isolation.
/// </summary>
/// <remarks>Each test uses a fresh random character id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresCollectionTests
{
    private readonly PostgresFixture _fx;
    public PostgresCollectionTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewId() => BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);

    private async Task<long> RowCountAsync(uint characterId)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {CollectionRepository.TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task GetOrCreate_unknown_character_returns_default_and_does_not_store()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();

        var doc = await repo.GetOrCreateAsync(id);

        Assert.Equal(id, doc.Id);
        Assert.Empty(doc.RegisteredMonsterIds);
        Assert.Empty(doc.RegisteredPetIds);
        Assert.Empty(doc.SelectedMonsterSkills);
        Assert.Empty(doc.SelectedPetSkills);
        Assert.Equal(0u, doc.ActivePetId);
        Assert.Equal((byte)0, doc.ActivePetFlag);
        Assert.Empty(doc.PetLoyalties);
        Assert.Equal(0, await RowCountAsync(id)); 
    }

    [Fact]
    public async Task Save_then_GetOrCreate_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();
        var doc = new CollectionDocument
        {
            Id = id,
            RegisteredMonsterIds = new List<uint> { 4_000_000_000, 7, 1_000_000 },
            RegisteredPetIds = new List<uint> { 90, 3_000_000_000 },
            SelectedMonsterSkills = new List<uint> { 7 },
            SelectedPetSkills = new List<uint> { 90, 3_000_000_000 },
            ActivePetId = 4_294_967_295,
            ActivePetFlag = 255,
            PetLoyalties = new List<PetLoyalty>
            {
                new() { PetId = 90, Loyalty = 500 },
                new() { PetId = 3_000_000_000, Loyalty = 12 },
            },
        };

        await repo.SaveAsync(doc);
        var back = await repo.GetOrCreateAsync(id);

        Assert.Equal(id, back.Id);
        Assert.Equal(new uint[] { 4_000_000_000, 7, 1_000_000 }, back.RegisteredMonsterIds);
        Assert.Equal(new uint[] { 90, 3_000_000_000 }, back.RegisteredPetIds);
        Assert.Equal(new uint[] { 7 }, back.SelectedMonsterSkills);
        Assert.Equal(new uint[] { 90, 3_000_000_000 }, back.SelectedPetSkills);
        Assert.Equal(4_294_967_295u, back.ActivePetId);
        Assert.Equal((byte)255, back.ActivePetFlag);
        Assert.Equal(2, back.PetLoyalties.Count);
        Assert.Equal(90u, back.PetLoyalties[0].PetId);
        Assert.Equal(500, back.PetLoyalties[0].Loyalty);
        Assert.Equal(3_000_000_000u, back.PetLoyalties[1].PetId);
        Assert.Equal(12, back.PetLoyalties[1].Loyalty);
        Assert.Equal((ushort)500, back.LoyaltyOf(90));
        Assert.Equal((ushort)12, back.LoyaltyOf(3_000_000_000));
        Assert.Equal((ushort)0, back.LoyaltyOf(555)); 
        Assert.Equal(1, await RowCountAsync(id));
    }

    [Fact]
    public async Task Save_preserves_list_order_and_duplicates()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();

        await repo.SaveAsync(new CollectionDocument
        {
            Id = id,
            RegisteredMonsterIds = new List<uint> { 30, 10, 20, 10, 30 },
        });

        var back = await repo.GetOrCreateAsync(id);
        Assert.Equal(new uint[] { 30, 10, 20, 10, 30 }, back.RegisteredMonsterIds);
    }

    [Fact]
    public async Task Save_twice_replaces_the_whole_document()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();

        await repo.SaveAsync(new CollectionDocument
        {
            Id = id,
            RegisteredMonsterIds = new List<uint> { 1, 2, 3 },
            RegisteredPetIds = new List<uint> { 4, 5 },
            SelectedMonsterSkills = new List<uint> { 1 },
            SelectedPetSkills = new List<uint> { 4 },
            ActivePetId = 4,
            ActivePetFlag = 3,
            PetLoyalties = new List<PetLoyalty> { new() { PetId = 4, Loyalty = 100 } },
        });
        
        await repo.SaveAsync(new CollectionDocument
        {
            Id = id,
            RegisteredMonsterIds = new List<uint> { 9 },
            ActivePetId = 0,
            ActivePetFlag = 0,
        });

        var back = await repo.GetOrCreateAsync(id);
        Assert.Equal(new uint[] { 9 }, back.RegisteredMonsterIds);
        Assert.Empty(back.RegisteredPetIds);
        Assert.Empty(back.SelectedMonsterSkills);
        Assert.Empty(back.SelectedPetSkills);
        Assert.Equal(0u, back.ActivePetId);
        Assert.Equal((byte)0, back.ActivePetFlag);
        Assert.Empty(back.PetLoyalties);
        Assert.Equal(1, await RowCountAsync(id)); 
    }

    [Fact]
    public async Task Save_default_document_creates_a_row_that_reads_back_empty()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();

        await repo.SaveAsync(new CollectionDocument { Id = id });

        Assert.Equal(1, await RowCountAsync(id));
        var back = await repo.GetOrCreateAsync(id);
        Assert.Equal(id, back.Id);
        Assert.Empty(back.RegisteredMonsterIds);
        Assert.Empty(back.PetLoyalties);
        Assert.Equal(0u, back.ActivePetId);
    }

    [Fact]
    public async Task Characters_are_isolated()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var a = NewId();
        var b = NewId();

        await repo.SaveAsync(new CollectionDocument { Id = a, RegisteredPetIds = new List<uint> { 1 }, ActivePetId = 1 });
        await repo.SaveAsync(new CollectionDocument { Id = b, RegisteredPetIds = new List<uint> { 2 }, ActivePetId = 2 });

        var backA = await repo.GetOrCreateAsync(a);
        var backB = await repo.GetOrCreateAsync(b);
        Assert.Equal(new uint[] { 1 }, backA.RegisteredPetIds);
        Assert.Equal(1u, backA.ActivePetId);
        Assert.Equal(new uint[] { 2 }, backB.RegisteredPetIds);
        Assert.Equal(2u, backB.ActivePetId);
    }

    [Fact]
    public async Task Save_mutated_loaded_document_persists_the_change()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();

        
        var doc = await repo.GetOrCreateAsync(id);
        doc.RegisteredPetIds.Add(77);
        doc.SetLoyalty(77, 250);
        doc.ActivePetId = 77;
        await repo.SaveAsync(doc);

        var back = await repo.GetOrCreateAsync(id);
        Assert.Equal(new uint[] { 77 }, back.RegisteredPetIds);
        Assert.Equal(77u, back.ActivePetId);
        Assert.Equal((ushort)250, back.LoyaltyOf(77));

        back.SetLoyalty(77, 300); 
        await repo.SaveAsync(back);
        var again = await repo.GetOrCreateAsync(id);
        Assert.Single(again.PetLoyalties);
        Assert.Equal((ushort)300, again.LoyaltyOf(77));
    }

    [Fact]
    public async Task Concurrent_first_saves_do_not_fail_and_leave_one_row()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();

        
        var saves = Enumerable.Range(1, 8).Select(n => repo.SaveAsync(new CollectionDocument
        {
            Id = id,
            ActivePetId = (uint)n,
        }));
        await Task.WhenAll(saves);

        Assert.Equal(1, await RowCountAsync(id));
        var back = await repo.GetOrCreateAsync(id);
        Assert.InRange(back.ActivePetId, 1u, 8u); 
    }

    [Fact]
    public async Task Cancelled_token_throws_instead_of_saving()
    {
        if (!_fx.Available) return;
        var repo = new CollectionRepository(_fx.Db!);
        var id = NewId();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.SaveAsync(new CollectionDocument { Id = id }, cts.Token));

        Assert.Equal(0, await RowCountAsync(id));
    }
}
