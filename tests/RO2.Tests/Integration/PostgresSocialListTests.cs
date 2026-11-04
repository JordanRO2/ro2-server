using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RO2.Network.Handlers;
using RO2.Socials.Domain;
using RO2.Socials.Infrastructure.Mongo;
using RO2.Socials.Infrastructure.Postgres;
using Xunit;
using ChatConfig = RO2.Server.Chat.Configuration.ServerConfiguration;
using ChatProgram = RO2.Server.Chat.Program;

namespace RO2.Tests.Integration;

// B14 S7: the Postgres provider for the Chat social-graph repositories, against a REAL postgres:16 container (PostgresFixture). Mirrors the S1 Mongo
// stored-shape assertions (order, the 200 caps, dedup, remove, reverse lookups, the 64-bit block Uid) and PROVES the atomic cap-race: parallel adds
// at 199 land exactly one, parallel first-inserts leave exactly one row.

/// <summary><see cref="PostgresFriendListRepository"/> over <c>chat_friend_lists</c>.</summary>
[Collection("Postgres")]
public sealed class PostgresFriendListTests
{
    private static long _seq = Random.Shared.NextInt64(1_000_000, 1_000_000_000);
    private readonly PostgresFixture _fx;
    public PostgresFriendListTests(PostgresFixture fx) => _fx = fx;

    private static uint New() => (uint)Interlocked.Increment(ref _seq);

    private PostgresFriendListRepository Open()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        return new PostgresFriendListRepository(_fx.Db!);
    }

    private async Task<string?> RawAsync(uint owner)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT friend_ids::text FROM chat_friend_lists WHERE owner_id = @o", conn);
        cmd.Parameters.AddWithValue("o", (long)owner);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<long> RowsAsync(uint owner)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM chat_friend_lists WHERE owner_id = @o", conn);
        cmd.Parameters.AddWithValue("o", (long)owner);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task SeedAsync(uint owner, int count)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO chat_friend_lists (owner_id, friend_ids) SELECT @o, jsonb_agg(g ORDER BY g) FROM generate_series(1, @n) g", conn);
        cmd.Parameters.AddWithValue("o", (long)owner);
        cmd.Parameters.AddWithValue("n", count);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T[]> RunParallelAsync<T>(int n, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () => { await gate.Task; return await work(i); })).ToArray();
        await Task.Delay(100);
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [DockerFact]
    public async Task Load_of_an_unknown_owner_is_empty_and_added_ids_round_trip_in_insertion_order()
    {
        var repo = Open();
        var owner = New();
        Assert.Empty(await repo.LoadAsync(owner));
        Assert.Equal(AddOutcome.Added, await repo.AddAsync(owner, 30));
        Assert.Equal(AddOutcome.Added, await repo.AddAsync(owner, 10));
        Assert.Equal(AddOutcome.Added, await repo.AddAsync(owner, 20));
        Assert.Equal(new uint[] { 30, 10, 20 }, await repo.LoadAsync(owner));     // added order, not sorted
        Assert.Equal("[30, 10, 20]", await RawAsync(owner));
        Assert.Equal(1, await RowsAsync(owner));
    }

    [DockerFact]
    public async Task Add_is_idempotent_and_Remove_drops_only_a_listed_id_keeping_order_and_the_row()
    {
        var repo = Open();
        var owner = New();
        foreach (uint id in new uint[] { 5, 6, 7 }) await repo.AddAsync(owner, id);
        Assert.Equal(AddOutcome.AlreadyFriend, await repo.AddAsync(owner, 6));
        Assert.Equal(new uint[] { 5, 6, 7 }, await repo.LoadAsync(owner));

        Assert.True(await repo.ListsFriendAsync(owner, 6));
        Assert.False(await repo.ListsFriendAsync(owner, 99));
        Assert.False(await repo.ListsFriendAsync(New(), 6));                     // no row at all

        await repo.RemoveAsync(owner, 99);                                        // never listed: nothing written
        Assert.Equal(new uint[] { 5, 6, 7 }, await repo.LoadAsync(owner));
        await repo.RemoveAsync(owner, 6);
        Assert.Equal(new uint[] { 5, 7 }, await repo.LoadAsync(owner));
        await repo.RemoveAsync(owner, 5);
        await repo.RemoveAsync(owner, 7);
        Assert.Empty(await repo.LoadAsync(owner));
        Assert.Equal("[]", await RawAsync(owner));                                // the row stays, the array empties
        await repo.RemoveAsync(New(), 1);                                         // unknown owner: no-op, no throw
    }

    [DockerFact]
    public async Task The_cap_is_enforced_at_200_with_AlreadyFriend_winning_on_a_full_list()
    {
        var repo = Open();
        Assert.Equal(200, SocialRules.MaxFriends);
        var owner = New();
        await SeedAsync(owner, SocialRules.MaxFriends - 1);
        Assert.Equal(AddOutcome.Added, await repo.AddAsync(owner, 5000));         // 199 -> 200
        Assert.Equal(AddOutcome.ListFull, await repo.AddAsync(owner, 5001));      // 200: new id refused
        Assert.Equal(AddOutcome.AlreadyFriend, await repo.AddAsync(owner, 5000)); // 200: full but already listed
        Assert.Equal(AddOutcome.AlreadyFriend, await repo.AddAsync(owner, 1));
        Assert.Equal(SocialRules.MaxFriends, (await repo.LoadAsync(owner)).Count);
    }

    [DockerFact]
    public async Task Ids_up_to_uint_max_round_trip_through_bigint()
    {
        var repo = Open();
        var owner = New();
        Assert.Equal(AddOutcome.Added, await repo.AddAsync(owner, uint.MaxValue));
        Assert.Equal(new[] { uint.MaxValue }, await repo.LoadAsync(owner));
        Assert.Contains(owner, await repo.OwnersListingAsync(uint.MaxValue));
    }

    [DockerFact]
    public async Task OwnersListing_returns_exactly_the_owners_whose_list_holds_the_friend()
    {
        var repo = Open();
        var a = New(); var b = New(); var c = New();
        var friend = New();
        await repo.AddAsync(a, friend);
        await repo.AddAsync(b, New());
        await repo.AddAsync(c, friend);
        await repo.RemoveAsync(c, friend);
        Assert.Equal(new[] { a }, await repo.OwnersListingAsync(friend));
        Assert.Empty(await repo.OwnersListingAsync(New()));
    }

    [DockerFact]
    public async Task Concurrent_adds_at_199_land_exactly_one_over_several_rounds()
    {
        var repo = Open();
        for (var round = 0; round < 5; round++)
        {
            var owner = New();
            await SeedAsync(owner, SocialRules.MaxFriends - 1);
            const int N = 16;
            var results = await RunParallelAsync(N, i => repo.AddAsync(owner, (uint)(10_000 + i)));

            Assert.Equal(1, results.Count(r => r == AddOutcome.Added));
            Assert.Equal(N - 1, results.Count(r => r == AddOutcome.ListFull));
            var stored = await repo.LoadAsync(owner);
            Assert.Equal(SocialRules.MaxFriends, stored.Count);                   // exactly at the cap, never over
            Assert.Equal(stored.Count, stored.Distinct().Count());
        }
    }

    [DockerFact]
    public async Task Concurrent_adds_of_the_same_id_land_once_and_the_rest_are_AlreadyFriend()
    {
        var repo = Open();
        var owner = New();
        await repo.AddAsync(owner, 1);
        const int N = 12;
        var results = await RunParallelAsync(N, _ => repo.AddAsync(owner, 777));
        Assert.Equal(1, results.Count(r => r == AddOutcome.Added));
        Assert.Equal(N - 1, results.Count(r => r == AddOutcome.AlreadyFriend));
        Assert.Equal(new uint[] { 1, 777 }, await repo.LoadAsync(owner));
    }

    [DockerFact]
    public async Task Concurrent_first_inserts_leave_one_row_holding_every_distinct_id_or_one_AlreadyFriend_for_the_same_id()
    {
        var repo = Open();

        var distinct = New();
        var two = await RunParallelAsync(8, i => repo.AddAsync(distinct, (uint)(100 + i)));
        Assert.All(two, r => Assert.Equal(AddOutcome.Added, r));                  // Mongo: the dup-key loser retries and lands too
        Assert.Equal(1, await RowsAsync(distinct));
        Assert.Equal(Enumerable.Range(100, 8).Select(i => (uint)i).Order(), (await repo.LoadAsync(distinct)).Order());

        var same = New();
        var dup = await RunParallelAsync(8, _ => repo.AddAsync(same, 55));
        Assert.Equal(1, dup.Count(r => r == AddOutcome.Added));
        Assert.Equal(7, dup.Count(r => r == AddOutcome.AlreadyFriend));
        Assert.Equal(1, await RowsAsync(same));
        Assert.Equal(new uint[] { 55 }, await repo.LoadAsync(same));
    }
}

/// <summary><see cref="PostgresBlockListRepository"/> over <c>chat_block_lists</c>.</summary>
[Collection("Postgres")]
public sealed class PostgresBlockListTests
{
    private const ulong OwnerGid = 0x0DE0B6B300000000UL;
    private static long _seq = Random.Shared.NextInt64(1_000_000, 1_000_000_000);
    private readonly PostgresFixture _fx;
    public PostgresBlockListTests(PostgresFixture fx) => _fx = fx;

    private static uint New() => (uint)Interlocked.Increment(ref _seq);

    private PostgresBlockListRepository Open()
    {
        Assert.True(_fx.Available, "Docker is up but the postgres:16 fixture did not start");
        return new PostgresBlockListRepository(_fx.Db!);
    }

    private async Task<T> ScalarAsync<T>(string sql, uint owner)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("o", (long)owner);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task SeedAsync(uint owner, int count)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO chat_block_lists (owner_id, entries) " +
            "SELECT @o, jsonb_agg(jsonb_build_object('uid', g::bigint, 'name', 'n' || g) ORDER BY g) FROM generate_series(1, @n) g", conn);
        cmd.Parameters.AddWithValue("o", (long)owner);
        cmd.Parameters.AddWithValue("n", count);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T[]> RunParallelAsync<T>(int n, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () => { await gate.Task; return await work(i); })).ToArray();
        await Task.Delay(100);
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [DockerFact]
    public async Task An_added_block_round_trips_as_a_bare_64bit_Uid_with_its_name_in_added_order()
    {
        var repo = Open();
        var owner = New();
        Assert.Empty(await repo.GetStoredEntriesAsync(owner));
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(owner, 30, "Cee"));
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(owner, 10, "Aye"));
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(owner, uint.MaxValue, "Max"));

        var entries = await repo.GetStoredEntriesAsync(owner);
        Assert.Equal(new ulong[] { 30, 10, uint.MaxValue }, entries.Select(e => e.Uid).ToArray());   // bare character id, added order
        Assert.Equal(new[] { "Cee", "Aye", "Max" }, entries.Select(e => e.Name).ToArray());
        Assert.Equal("number", await ScalarAsync<string>("SELECT jsonb_typeof(entries -> 0 -> 'uid') FROM chat_block_lists WHERE owner_id = @o", owner));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM chat_block_lists WHERE owner_id = @o", owner));
    }

    [DockerFact]
    public async Task Add_refuses_a_duplicate_as_AlreadyRegistered_and_Blocks_and_Remove_work_on_the_bare_id()
    {
        var repo = Open();
        var owner = New();
        await repo.AddAsync(owner, 7, "Seven");
        await repo.AddAsync(owner, 8, "Eight");
        Assert.Equal(BlockAddOutcome.AlreadyRegistered, await repo.AddAsync(owner, 7, "Renamed"));
        var stored = await repo.GetStoredEntriesAsync(owner);
        Assert.Equal(2, stored.Count);
        Assert.Equal("Seven", stored[0].Name);                                    // the stored name is not rewritten

        Assert.True(await repo.BlocksAsync(owner, 7));
        Assert.False(await repo.BlocksAsync(owner, 9));
        Assert.False(await repo.BlocksAsync(New(), 7));

        await repo.RemoveAsync(owner, 99);                                        // never blocked: nothing written
        Assert.Equal(2, (await repo.GetStoredEntriesAsync(owner)).Count);
        await repo.RemoveAsync(owner, OwnerGid | 7);                              // a GID names the same row through its low dword
        Assert.Equal(new ulong[] { 8 }, (await repo.GetStoredEntriesAsync(owner)).Select(e => e.Uid).ToArray());
        await repo.RemoveAsync(owner, 8);
        Assert.Empty(await repo.GetStoredEntriesAsync(owner));
        Assert.Equal("[]", await ScalarAsync<string>("SELECT entries::text FROM chat_block_lists WHERE owner_id = @o", owner));   // the row stays
        await repo.RemoveAsync(New(), 1);                                         // unknown owner: no-op
    }

    [DockerFact]
    public async Task The_cap_is_enforced_at_200_with_AlreadyRegistered_winning_on_a_full_list()
    {
        var repo = Open();
        Assert.Equal(200, SocialRules.MaxBlocked);
        var owner = New();
        await SeedAsync(owner, SocialRules.MaxBlocked - 1);
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(owner, 5000, "last"));        // 199 -> 200
        Assert.Equal(BlockAddOutcome.ListFull, await repo.AddAsync(owner, 5001, "over"));
        Assert.Equal(BlockAddOutcome.AlreadyRegistered, await repo.AddAsync(owner, 5000, "last"));
        Assert.Equal(SocialRules.MaxBlocked, (await repo.GetStoredEntriesAsync(owner)).Count);
    }

    [DockerFact]
    public async Task OwnersBlocking_returns_exactly_the_requested_owners_whose_list_holds_the_character()
    {
        var repo = Open();
        var blocker = New(); var other = New(); var empty = New(); var outsider = New();
        const uint target = 4242;
        await repo.AddAsync(blocker, target, "T");
        await repo.AddAsync(other, target + 1, "U");
        await repo.AddAsync(outsider, target, "T");                               // blocks the target but is not in the asked set
        await repo.AddAsync(empty, 1, "x");
        await repo.RemoveAsync(empty, 1);

        var got = await repo.OwnersBlockingAsync(new uint[] { blocker, other, empty, New() }, target);
        Assert.Equal(new[] { blocker }, got.ToArray());
        Assert.Empty(await repo.OwnersBlockingAsync(Array.Empty<uint>(), target));
    }

    [DockerFact]
    public async Task Concurrent_blocks_at_199_land_exactly_one_over_several_rounds()
    {
        var repo = Open();
        for (var round = 0; round < 5; round++)
        {
            var owner = New();
            await SeedAsync(owner, SocialRules.MaxBlocked - 1);
            const int N = 16;
            var results = await RunParallelAsync(N, i => repo.AddAsync(owner, (uint)(10_000 + i), "p" + i));

            Assert.Equal(1, results.Count(r => r == BlockAddOutcome.Added));
            Assert.Equal(N - 1, results.Count(r => r == BlockAddOutcome.ListFull));
            var stored = await repo.GetStoredEntriesAsync(owner);
            Assert.Equal(SocialRules.MaxBlocked, stored.Count);
            Assert.Equal(stored.Count, stored.Select(e => e.Uid).Distinct().Count());
        }
    }

    [DockerFact]
    public async Task Concurrent_first_blocks_leave_one_row_holding_every_distinct_id_or_one_AlreadyRegistered_for_the_same_id()
    {
        var repo = Open();

        var distinct = New();
        var results = await RunParallelAsync(8, i => repo.AddAsync(distinct, (uint)(100 + i), "n" + i));
        Assert.All(results, r => Assert.Equal(BlockAddOutcome.Added, r));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM chat_block_lists WHERE owner_id = @o", distinct));
        Assert.Equal(Enumerable.Range(100, 8).Select(i => (ulong)i).Order(), (await repo.GetStoredEntriesAsync(distinct)).Select(e => e.Uid).Order());

        var same = New();
        var dup = await RunParallelAsync(8, _ => repo.AddAsync(same, 55, "dup"));
        Assert.Equal(1, dup.Count(r => r == BlockAddOutcome.Added));
        Assert.Equal(7, dup.Count(r => r == BlockAddOutcome.AlreadyRegistered));
        Assert.Single(await repo.GetStoredEntriesAsync(same));
    }
}

/// <summary>The Chat container picks the friend/block repository by <c>Database.Provider</c> and boots under Postgres (the S5/S6 Mongo-only registration made it crash).</summary>
public sealed class PostgresSocialProviderSelectionTests
{
    private static ServiceProvider Boot(string provider, string connectionString)
    {
        var config = new ChatConfig();
        config.Database.Provider = provider;
        config.Database.ConnectionString = connectionString;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();
        ChatProgram.ConfigureServices(services, config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [DataFact]
    public void Provider_Postgres_boots_the_Chat_container_with_the_Postgres_repositories()
    {
        using var sp = Boot("Postgres", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x");   // never connected: no I/O at resolve time
        Assert.IsType<PostgresFriendListRepository>(sp.GetRequiredService<IFriendListRepository>());
        Assert.IsType<PostgresBlockListRepository>(sp.GetRequiredService<IBlockListRepository>());
        Assert.NotNull(sp.GetRequiredService<RO2.Server.Chat.FriendPresence>());                    // the singleton consumer resolves
    }

    [DataFact]
    public void Provider_Mongo_keeps_the_Mongo_repositories()
    {
        using var sp = Boot("Mongo", "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100");
        Assert.IsType<MongoFriendListRepository>(sp.GetRequiredService<IFriendListRepository>());
        Assert.IsType<MongoBlockListRepository>(sp.GetRequiredService<IBlockListRepository>());
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
