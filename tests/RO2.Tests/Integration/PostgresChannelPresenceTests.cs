using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="ChannelPresenceStore"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers account channel upsert and get, character label upsert, the batched
/// label lookup (empty input, unknown ids, empty labels omitted), and the uint-to-bigint round-trip.
/// </summary>
/// <remarks>Each test uses fresh random ids so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresChannelPresenceTests
{
    private readonly PostgresFixture _fx;
    public PostgresChannelPresenceTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewId() => (uint)Random.Shared.NextInt64(3_000_000_000L, uint.MaxValue);

    [Fact]
    public async Task Account_channel_round_trips_uint_handle()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var account = NewId();
        await repo.SetAccountChannelAsync(account, 4_000_000_001u);
        Assert.Equal(4_000_000_001u, await repo.GetAccountChannelAsync(account));
    }

    [Fact]
    public async Task Account_channel_unknown_is_null()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        Assert.Null(await repo.GetAccountChannelAsync(NewId()));
    }

    [Fact]
    public async Task Account_channel_set_overwrites_latest()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var account = NewId();
        await repo.SetAccountChannelAsync(account, 111);
        await repo.SetAccountChannelAsync(account, 222); 
        Assert.Equal(222u, await repo.GetAccountChannelAsync(account));
    }

    [Fact]
    public async Task Account_channel_zero_handle_is_stored_not_null()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var account = NewId();
        await repo.SetAccountChannelAsync(account, 0);
        Assert.Equal(0u, await repo.GetAccountChannelAsync(account));
    }

    [Fact]
    public async Task Account_channel_concurrent_sets_last_writer_wins_without_error()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var account = NewId();
        var tasks = Enumerable.Range(1, 20).Select(n => repo.SetAccountChannelAsync(account, (uint)n)).ToArray();
        await Task.WhenAll(tasks);
        var final = await repo.GetAccountChannelAsync(account);
        Assert.NotNull(final);
        Assert.InRange(final!.Value, 1u, 20u);
    }

    [Fact]
    public async Task Account_and_character_tables_are_independent()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var id = NewId();
        await repo.SetAccountChannelAsync(id, 5);
        
        Assert.Empty(await repo.GetLocationsAsync(new uint[] { id }));
        await repo.SetCharacterLocationAsync(id, 9, "Channel 2");
        Assert.Equal(5u, await repo.GetAccountChannelAsync(id));
        Assert.Equal("Channel 2", (await repo.GetLocationsAsync(new uint[] { id }))[id]);
    }

    [Fact]
    public async Task Locations_returns_labels_by_character_id()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var a = NewId();
        var b = NewId() - 1;
        await repo.SetCharacterLocationAsync(a, 10, "Channel 1");
        await repo.SetCharacterLocationAsync(b, 11, "Channel 3");
        var got = await repo.GetLocationsAsync(new[] { a, b });
        Assert.Equal(2, got.Count);
        Assert.Equal("Channel 1", got[a]);
        Assert.Equal("Channel 3", got[b]);
    }

    [Fact]
    public async Task Locations_empty_input_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var got = await repo.GetLocationsAsync(Array.Empty<uint>());
        Assert.Empty(got);
    }

    [Fact]
    public async Task Locations_leaves_out_unknown_characters()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var known = NewId();
        var unknown = NewId() - 2;
        await repo.SetCharacterLocationAsync(known, 1, "Channel 4");
        var got = await repo.GetLocationsAsync(new[] { known, unknown });
        Assert.Single(got);
        Assert.Equal("Channel 4", got[known]);
        Assert.False(got.ContainsKey(unknown));
    }

    [Fact]
    public async Task Locations_leaves_out_empty_label()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var empty = NewId();
        var labelled = NewId() - 3;
        await repo.SetCharacterLocationAsync(empty, 1, "");
        await repo.SetCharacterLocationAsync(labelled, 2, "Channel 7");
        var got = await repo.GetLocationsAsync(new[] { empty, labelled });
        Assert.Single(got);
        Assert.Equal("Channel 7", got[labelled]);
    }

    [Fact]
    public async Task Location_set_overwrites_and_can_blank_a_label()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var ch = NewId();
        await repo.SetCharacterLocationAsync(ch, 1, "Channel 1");
        await repo.SetCharacterLocationAsync(ch, 2, "Channel 5"); 
        Assert.Equal("Channel 5", (await repo.GetLocationsAsync(new[] { ch }))[ch]);
        await repo.SetCharacterLocationAsync(ch, 3, ""); 
        Assert.Empty(await repo.GetLocationsAsync(new[] { ch }));
    }

    [Fact]
    public async Task Locations_duplicate_ids_in_input_are_returned_once()
    {
        if (!_fx.Available) return;
        var repo = new ChannelPresenceStore(_fx.Db!);
        var ch = NewId();
        await repo.SetCharacterLocationAsync(ch, 1, "Channel 6");
        var got = await repo.GetLocationsAsync(new[] { ch, ch });
        Assert.Single(got);
        Assert.Equal("Channel 6", got[ch]);
    }

    [Fact]
    public void Label_helpers_match_the_channel_list_naming()
    {
        Assert.Equal("Channel 3", ChannelPresenceStore.ChannelLabel(3));
        Assert.Equal("Channel 2", ChannelPresenceStore.LabelOf(1002, 5, n => (uint)(1000 + n)));
        Assert.Equal("", ChannelPresenceStore.LabelOf(9999, 5, n => (uint)(1000 + n)));
    }
}
