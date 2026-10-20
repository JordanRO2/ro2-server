using Npgsql;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="GuildEmblemRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers the load-once registry, memory-only mode before init, index advance
/// with skip-0 wrap, upsert, the all-zero and wrong-length guid guard, remove, and a restart round-trip (a fresh
/// repository plus <c>InitAsync</c> stands in for a server restart).
/// </summary>
/// <remarks>Each test uses fresh random guilds so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresGuildEmblemTests
{
    private readonly PostgresFixture _fx;
    public PostgresGuildEmblemTests(PostgresFixture fx) => _fx = fx;

    private static byte[] NewGuid() => Guid.NewGuid().ToByteArray();

    private static async Task SeedAsync(PostgresDbContext db, string id, long index, byte[] image)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {GuildEmblemRepository.TableName} (id, emblem_index, image, updated_utc) " +
            "VALUES (@id, @idx, @image, @updated) " +
            "ON CONFLICT (id) DO UPDATE SET emblem_index = EXCLUDED.emblem_index, image = EXCLUDED.image, updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("idx", index);
        cmd.Parameters.AddWithValue("image", image);
        cmd.Parameters.AddWithValue("updated", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long?> StoredIndexAsync(PostgresDbContext db, string id)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT emblem_index FROM {GuildEmblemRepository.TableName} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        var value = await cmd.ExecuteScalarAsync();
        return value is long l ? l : null;
    }

    [Fact]
    public async Task Set_stores_index_1_for_a_new_guild_and_get_returns_it()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var guid = NewGuid();
        var image = new byte[] { 1, 2, 3, 4 };

        var before = DateTime.UtcNow;
        var set = await repo.SetAsync(guid, image);

        Assert.Equal(Convert.ToHexStringLower(guid), set.Id);
        Assert.Equal((ushort)1, set.Index);
        Assert.Equal(image, set.Image);
        Assert.InRange(set.UpdatedUtc, before, DateTime.UtcNow);
        var got = repo.Get(guid);
        Assert.NotNull(got);
        Assert.Equal((ushort)1, got!.Index);
        Assert.Equal(image, got.Image);
        Assert.Equal((ushort)1, repo.IndexOf(guid));
    }

    [Fact]
    public async Task Set_advances_the_index_by_one_each_time_and_replaces_the_image()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var guid = NewGuid();

        Assert.Equal((ushort)1, (await repo.SetAsync(guid, new byte[] { 1 })).Index);
        Assert.Equal((ushort)2, (await repo.SetAsync(guid, new byte[] { 2 })).Index);
        var third = await repo.SetAsync(guid, new byte[] { 3, 3 });

        Assert.Equal((ushort)3, third.Index);
        Assert.Equal((ushort)3, repo.IndexOf(guid));
        Assert.Equal(new byte[] { 3, 3 }, repo.Get(guid)!.Image);

        
        Assert.Equal(3L, await StoredIndexAsync(_fx.Db!, Convert.ToHexStringLower(guid)));
    }

    [Fact]
    public async Task Emblems_of_different_guilds_are_independent()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var a = NewGuid();
        var b = NewGuid();

        await repo.SetAsync(a, new byte[] { 10 });
        await repo.SetAsync(a, new byte[] { 11 });
        await repo.SetAsync(b, new byte[] { 20 });

        Assert.Equal((ushort)2, repo.IndexOf(a));
        Assert.Equal((ushort)1, repo.IndexOf(b));
        Assert.Equal(new byte[] { 11 }, repo.Get(a)!.Image);
        Assert.Equal(new byte[] { 20 }, repo.Get(b)!.Image);
    }

    [Fact]
    public async Task Get_unknown_guild_is_null_and_index_is_zero()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var guid = NewGuid();

        Assert.Null(repo.Get(guid));
        Assert.Equal((ushort)0, repo.IndexOf(guid));
    }

    [Fact]
    public async Task All_zero_guid_never_has_an_emblem()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var zero = new byte[16];

        
        await repo.SetAsync(zero, new byte[] { 9 });

        Assert.Null(repo.Get(zero));
        Assert.Equal((ushort)0, repo.IndexOf(zero));
        await repo.RemoveAsync(zero); 
    }

    [Fact]
    public async Task Wrong_length_guid_never_has_an_emblem()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var guid = NewGuid();
        await repo.SetAsync(guid, new byte[] { 1 });

        Assert.Null(repo.Get(guid.AsSpan(0, 15)));
        Assert.Null(repo.Get(guid.Concat(new byte[] { 1 }).ToArray()));
        Assert.Equal((ushort)0, repo.IndexOf(guid.AsSpan(0, 15)));
        Assert.Null(repo.Get(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public async Task Row_is_keyed_by_32_lowercase_hex_chars()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var guid = NewGuid();
        await repo.SetAsync(guid, new byte[] { 1 });

        var key = Convert.ToHexStringLower(guid);
        Assert.Equal(32, key.Length);
        Assert.Equal(key, key.ToLowerInvariant());
        Assert.Equal(1L, await StoredIndexAsync(_fx.Db!, key));
    }

    [Fact]
    public async Task Emblem_survives_a_restart()
    {
        if (!_fx.Available) return;
        var first = new GuildEmblemRepository(_fx.Db!);
        await first.InitAsync();
        var guid = NewGuid();
        var image = new byte[] { 7, 8, 9 };
        await first.SetAsync(guid, new byte[] { 1 });
        var set = await first.SetAsync(guid, image);

        var second = new GuildEmblemRepository(_fx.Db!);
        Assert.Null(second.Get(guid)); 
        await second.InitAsync();

        var loaded = second.Get(guid);
        Assert.NotNull(loaded);
        Assert.Equal(Convert.ToHexStringLower(guid), loaded!.Id);
        Assert.Equal((ushort)2, loaded.Index);
        Assert.Equal(image, loaded.Image);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedUtc.Kind);
        
        var ticks = set.UpdatedUtc.Ticks;
        Assert.Equal(new DateTime(ticks - ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc), loaded.UpdatedUtc);
        Assert.Equal((ushort)2, second.IndexOf(guid));
    }

    [Fact]
    public async Task Set_continues_from_the_stored_index_after_a_restart()
    {
        if (!_fx.Available) return;
        var first = new GuildEmblemRepository(_fx.Db!);
        await first.InitAsync();
        var guid = NewGuid();
        await first.SetAsync(guid, new byte[] { 1 });
        await first.SetAsync(guid, new byte[] { 2 });

        var second = new GuildEmblemRepository(_fx.Db!);
        await second.InitAsync();
        var next = await second.SetAsync(guid, new byte[] { 3 });

        Assert.Equal((ushort)3, next.Index);
        var third = new GuildEmblemRepository(_fx.Db!);
        await third.InitAsync();
        Assert.Equal((ushort)3, third.IndexOf(guid));
        Assert.Equal(new byte[] { 3 }, third.Get(guid)!.Image);
    }

    [Fact]
    public async Task Index_wraps_from_65535_to_1_skipping_0()
    {
        if (!_fx.Available) return;
        var guid = NewGuid();
        await SeedAsync(_fx.Db!, Convert.ToHexStringLower(guid), 65535, new byte[] { 1 });

        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        Assert.Equal((ushort)65535, repo.IndexOf(guid)); 

        var wrapped = await repo.SetAsync(guid, new byte[] { 2 });
        Assert.Equal((ushort)1, wrapped.Index);
        Assert.Equal((ushort)2, (await repo.SetAsync(guid, new byte[] { 3 })).Index);

        
        var restarted = new GuildEmblemRepository(_fx.Db!);
        await restarted.InitAsync();
        Assert.Equal((ushort)2, restarted.IndexOf(guid));
    }

    [Fact]
    public async Task Index_wraps_after_65535_uploads_in_memory()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!); 
        var guid = NewGuid();
        var image = new byte[] { 1 };

        GuildEmblem last = null!;
        for (var i = 0; i < 65535; i++)
            last = await repo.SetAsync(guid, image);
        Assert.Equal((ushort)65535, last.Index);

        Assert.Equal((ushort)1, (await repo.SetAsync(guid, image)).Index);
        Assert.Equal((ushort)2, (await repo.SetAsync(guid, image)).Index);
    }

    [Fact]
    public async Task Set_before_init_is_memory_only()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        var guid = NewGuid();

        var set = await repo.SetAsync(guid, new byte[] { 5 });

        Assert.Equal((ushort)1, set.Index);
        Assert.Equal((ushort)1, repo.IndexOf(guid));
        Assert.Null(await StoredIndexAsync(_fx.Db!, Convert.ToHexStringLower(guid))); 

        var restarted = new GuildEmblemRepository(_fx.Db!);
        await restarted.InitAsync();
        Assert.Null(restarted.Get(guid));
    }

    [Fact]
    public async Task Init_loads_stored_rows_over_memory_only_entries()
    {
        if (!_fx.Available) return;
        var guid = NewGuid();
        var key = Convert.ToHexStringLower(guid);
        await SeedAsync(_fx.Db!, key, 7, new byte[] { 0xAA });

        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.SetAsync(guid, new byte[] { 1 }); 
        Assert.Equal((ushort)1, repo.IndexOf(guid));

        await repo.InitAsync();

        Assert.Equal((ushort)7, repo.IndexOf(guid)); 
        Assert.Equal(new byte[] { 0xAA }, repo.Get(guid)!.Image);
    }

    [Fact]
    public async Task Init_loads_only_once()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();

        var guid = NewGuid();
        await SeedAsync(_fx.Db!, Convert.ToHexStringLower(guid), 4, new byte[] { 1 });
        await repo.InitAsync(); 

        Assert.Null(repo.Get(guid));
        var fresh = new GuildEmblemRepository(_fx.Db!);
        await fresh.InitAsync();
        Assert.Equal((ushort)4, fresh.IndexOf(guid));
    }

    [Fact]
    public async Task Concurrent_init_calls_all_complete_and_load_the_registry()
    {
        if (!_fx.Available) return;
        var guid = NewGuid();
        await SeedAsync(_fx.Db!, Convert.ToHexStringLower(guid), 3, new byte[] { 2 });

        var repo = new GuildEmblemRepository(_fx.Db!);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => repo.InitAsync())));

        Assert.Equal((ushort)3, repo.IndexOf(guid));
    }

    [Fact]
    public async Task Concurrent_sets_for_one_guild_get_distinct_consecutive_indexes()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var guid = NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => Task.Run(() => repo.SetAsync(guid, new[] { (byte)i }))));

        
        Assert.Equal(Enumerable.Range(1, 40), results.Select(r => (int)r.Index).Order());
        Assert.Equal((ushort)40, repo.IndexOf(guid));
    }

    [Fact]
    public async Task Remove_forgets_the_emblem_in_memory_and_in_the_table()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var guid = NewGuid();
        await repo.SetAsync(guid, new byte[] { 1 });
        await repo.SetAsync(guid, new byte[] { 2 });

        await repo.RemoveAsync(guid);

        Assert.Null(repo.Get(guid));
        Assert.Equal((ushort)0, repo.IndexOf(guid));
        Assert.Null(await StoredIndexAsync(_fx.Db!, Convert.ToHexStringLower(guid)));
        var restarted = new GuildEmblemRepository(_fx.Db!);
        await restarted.InitAsync();
        Assert.Null(restarted.Get(guid));

        
        Assert.Equal((ushort)1, (await repo.SetAsync(guid, new byte[] { 3 })).Index);
    }

    [Fact]
    public async Task Remove_unknown_guild_is_a_no_op()
    {
        if (!_fx.Available) return;
        var repo = new GuildEmblemRepository(_fx.Db!);
        await repo.InitAsync();
        var other = NewGuid();
        await repo.SetAsync(other, new byte[] { 1 });

        await repo.RemoveAsync(NewGuid());

        Assert.Equal((ushort)1, repo.IndexOf(other));
    }

    [Fact]
    public async Task Remove_before_init_only_touches_memory()
    {
        if (!_fx.Available) return;
        var writer = new GuildEmblemRepository(_fx.Db!);
        await writer.InitAsync();
        var guid = NewGuid();
        await writer.SetAsync(guid, new byte[] { 1 });

        var uninitialised = new GuildEmblemRepository(_fx.Db!);
        await uninitialised.SetAsync(guid, new byte[] { 2 }); 
        await uninitialised.RemoveAsync(guid);
        Assert.Null(uninitialised.Get(guid));

        
        Assert.Equal(1L, await StoredIndexAsync(_fx.Db!, Convert.ToHexStringLower(guid)));
    }
}
