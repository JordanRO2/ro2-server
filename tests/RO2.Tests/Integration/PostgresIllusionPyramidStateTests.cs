using RO2.Domain.Entities;
using RO2.Worlds.Domain;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="IllusionPyramidStateRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers the zero state for unknown characters, first-save creation,
/// whole-row replacement, the UpdatedUtc stamp, the uint id round-trip, and per-character isolation.
/// </summary>
/// <remarks>Each test uses a fresh random character id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresIllusionPyramidStateTests
{
    private readonly PostgresFixture _fx;
    public PostgresIllusionPyramidStateTests(PostgresFixture fx) => _fx = fx;

    private static uint NewId() => (uint)Random.Shared.NextInt64(1_000_000, uint.MaxValue);

    [Fact]
    public async Task Get_unknown_character_returns_zero_state_with_id_and_does_not_persist()
    {
        if (!_fx.Available) return;
        var repo = new IllusionPyramidStateRepository(_fx.Db!);
        var id = NewId();

        var doc = await repo.GetAsync(id);

        Assert.NotNull(doc);
        Assert.Equal(id, doc.Id);
        Assert.Equal(0, doc.Mystical);
        Assert.Equal(0, doc.Bright);
        Assert.Equal((byte)0, doc.TopRecord);
        Assert.Equal(default, doc.UpdatedUtc);

        
        var again = await repo.GetAsync(id);
        Assert.Equal(default, again.UpdatedUtc);
    }

    [Fact]
    public async Task Save_creates_on_first_save_and_get_round_trips_all_fields()
    {
        if (!_fx.Available) return;
        var repo = new IllusionPyramidStateRepository(_fx.Db!);
        
        var id = 4_000_000_000u - (uint)Random.Shared.Next(0, 1_000_000);
        var doc = new IllusionPyramidStateDocument { Id = id, Mystical = 125, Bright = 25, TopRecord = 37 };

        await repo.SaveAsync(doc);
        var got = await repo.GetAsync(id);

        Assert.Equal(id, got.Id);
        Assert.Equal(125, got.Mystical);
        Assert.Equal(25, got.Bright);
        Assert.Equal((byte)37, got.TopRecord);
    }

    [Fact]
    public async Task Save_stamps_UpdatedUtc_on_the_callers_document_and_persists_it()
    {
        if (!_fx.Available) return;
        var repo = new IllusionPyramidStateRepository(_fx.Db!);
        var id = NewId();
        var doc = new IllusionPyramidStateDocument { Id = id, Mystical = 1, UpdatedUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var before = DateTime.UtcNow;

        await repo.SaveAsync(doc);

        var after = DateTime.UtcNow;
        Assert.InRange(doc.UpdatedUtc, before, after);          
        var got = await repo.GetAsync(id);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
        
        Assert.InRange((doc.UpdatedUtc - got.UpdatedUtc).TotalMilliseconds, 0, 1);
    }

    [Fact]
    public async Task Save_replaces_the_whole_row_including_back_to_zero()
    {
        if (!_fx.Available) return;
        var repo = new IllusionPyramidStateRepository(_fx.Db!);
        var id = NewId();
        await repo.SaveAsync(new IllusionPyramidStateDocument { Id = id, Mystical = 500, Bright = 300, TopRecord = 99 });
        var first = await repo.GetAsync(id);

        await repo.SaveAsync(new IllusionPyramidStateDocument { Id = id, Mystical = 7, Bright = 0, TopRecord = 0 });
        var second = await repo.GetAsync(id);

        Assert.Equal(7, second.Mystical);
        Assert.Equal(0, second.Bright);              
        Assert.Equal((byte)0, second.TopRecord);
        Assert.True(second.UpdatedUtc >= first.UpdatedUtc);
    }

    [Fact]
    public async Task Save_preserves_negative_balances_and_max_top_record()
    {
        if (!_fx.Available) return;
        var repo = new IllusionPyramidStateRepository(_fx.Db!);
        var id = NewId();

        await repo.SaveAsync(new IllusionPyramidStateDocument { Id = id, Mystical = int.MinValue, Bright = int.MaxValue, TopRecord = byte.MaxValue });
        var got = await repo.GetAsync(id);

        Assert.Equal(int.MinValue, got.Mystical);
        Assert.Equal(int.MaxValue, got.Bright);
        Assert.Equal(byte.MaxValue, got.TopRecord);
    }

    [Fact]
    public async Task Characters_are_isolated_from_each_other()
    {
        if (!_fx.Available) return;
        var repo = new IllusionPyramidStateRepository(_fx.Db!);
        var a = NewId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;

        await repo.SaveAsync(new IllusionPyramidStateDocument { Id = a, Mystical = 10, Bright = 20, TopRecord = 3 });
        await repo.SaveAsync(new IllusionPyramidStateDocument { Id = b, Mystical = 11, Bright = 21, TopRecord = 4 });
        await repo.SaveAsync(new IllusionPyramidStateDocument { Id = a, Mystical = 12, Bright = 22, TopRecord = 5 });

        var gotA = await repo.GetAsync(a);
        var gotB = await repo.GetAsync(b);
        Assert.Equal((12, 22, (byte)5), (gotA.Mystical, gotA.Bright, gotA.TopRecord));
        Assert.Equal((11, 21, (byte)4), (gotB.Mystical, gotB.Bright, gotB.TopRecord));
    }
}
