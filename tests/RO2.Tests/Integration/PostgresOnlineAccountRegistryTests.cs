using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="OnlineAccountRegistry"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers claim, owner-guarded heartbeat, transfer, release, kick flag and the race semantics of conditional updates. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresOnlineAccountRegistryTests
{
    private readonly PostgresFixture _fx;
    public PostgresOnlineAccountRegistryTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewId() => (uint)Random.Shared.NextInt64(2_500_000_000L, 4_000_000_000L);

    private static string NewOwner() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Get_unknown_account_is_null()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        Assert.Null(await repo.GetAsync(NewId()));
    }

    [Fact]
    public async Task Claim_then_get_returns_entry()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        var before = DateTime.UtcNow.AddSeconds(-1);
        await repo.ClaimAsync(id, owner);

        var e = await repo.GetAsync(id);
        Assert.NotNull(e);
        Assert.Equal(id, e!.AccountId);
        Assert.Equal(owner, e.Owner);
        Assert.Null(e.KickRequestedAt);
        Assert.Equal(DateTimeKind.Utc, e.SeenAt.Kind);
        Assert.InRange(e.SeenAt, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Claim_replaces_older_entry_and_clears_kick()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var first = NewOwner();
        var second = NewOwner();
        await repo.ClaimAsync(id, first);
        await repo.RequestKickAsync(id, first);
        Assert.NotNull((await repo.GetAsync(id))!.KickRequestedAt);

        await repo.ClaimAsync(id, second); 

        var e = await repo.GetAsync(id);
        Assert.Equal(second, e!.Owner);
        Assert.Null(e.KickRequestedAt);
    }

    [Fact]
    public async Task Heartbeat_by_holder_refreshes_and_returns_entry()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);
        var claimed = (await repo.GetAsync(id))!.SeenAt;
        await Task.Delay(50);

        var beat = await repo.HeartbeatAsync(id, owner);

        Assert.NotNull(beat);
        Assert.Equal(id, beat!.AccountId);
        Assert.Equal(owner, beat.Owner);
        Assert.True(beat.SeenAt > claimed);
        Assert.Equal(beat.SeenAt, (await repo.GetAsync(id))!.SeenAt); 
    }

    [Fact]
    public async Task Heartbeat_by_non_holder_is_null_and_changes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);
        var seen = (await repo.GetAsync(id))!.SeenAt;
        await Task.Delay(50);

        Assert.Null(await repo.HeartbeatAsync(id, NewOwner()));
        Assert.Equal(seen, (await repo.GetAsync(id))!.SeenAt);
    }

    [Fact]
    public async Task Heartbeat_unknown_account_is_null()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        Assert.Null(await repo.HeartbeatAsync(NewId(), NewOwner()));
    }

    [Fact]
    public async Task Heartbeat_keeps_pending_kick()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);
        await repo.RequestKickAsync(id, owner);

        var beat = await repo.HeartbeatAsync(id, owner);

        Assert.NotNull(beat!.KickRequestedAt);
        Assert.Equal(DateTimeKind.Utc, beat.KickRequestedAt!.Value.Kind);
    }

    [Fact]
    public async Task Transfer_from_holder_moves_entry_and_carries_kick()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var from = NewOwner();
        var to = NewOwner();
        await repo.ClaimAsync(id, from);
        await repo.RequestKickAsync(id, from);
        var kick = (await repo.GetAsync(id))!.KickRequestedAt;
        var seen = (await repo.GetAsync(id))!.SeenAt;
        await Task.Delay(50);

        Assert.True(await repo.TransferAsync(id, from, to));

        var e = await repo.GetAsync(id);
        Assert.Equal(to, e!.Owner);
        Assert.Equal(kick, e.KickRequestedAt); 
        Assert.True(e.SeenAt > seen);          
    }

    [Fact]
    public async Task Transfer_from_non_holder_is_false_and_does_not_overwrite_newer_claim()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var stale = NewOwner();
        var newer = NewOwner();
        await repo.ClaimAsync(id, newer); 

        Assert.False(await repo.TransferAsync(id, stale, NewOwner()));

        Assert.Equal(newer, (await repo.GetAsync(id))!.Owner);
    }

    [Fact]
    public async Task Transfer_unknown_account_is_false()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        Assert.False(await repo.TransferAsync(NewId(), NewOwner(), NewOwner()));
    }

    [Fact]
    public async Task Concurrent_transfers_from_same_owner_exactly_one_wins()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var from = NewOwner();
        await repo.ClaimAsync(id, from);
        var targets = Enumerable.Range(0, 8).Select(_ => NewOwner()).ToArray();

        var results = await Task.WhenAll(targets.Select(t => repo.TransferAsync(id, from, t)));

        Assert.Equal(1, results.Count(r => r));
        var winner = targets[Array.IndexOf(results, true)];
        Assert.Equal(winner, (await repo.GetAsync(id))!.Owner);
    }

    [Fact]
    public async Task Release_by_holder_removes_entry()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);

        await repo.ReleaseAsync(id, owner);

        Assert.Null(await repo.GetAsync(id));
    }

    [Fact]
    public async Task Release_by_non_holder_keeps_entry()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);

        await repo.ReleaseAsync(id, NewOwner()); 
        await repo.ReleaseAsync(NewId(), owner); 

        Assert.Equal(owner, (await repo.GetAsync(id))!.Owner);
    }

    [Fact]
    public async Task ReleaseAny_removes_entry_when_any_listed_owner_holds_it()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);

        await repo.ReleaseAnyAsync(id, new[] { NewOwner(), owner, NewOwner() });

        Assert.Null(await repo.GetAsync(id));
    }

    [Fact]
    public async Task ReleaseAny_keeps_entry_held_by_unlisted_owner()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);

        await repo.ReleaseAnyAsync(id, new[] { NewOwner(), NewOwner() }); 

        Assert.Equal(owner, (await repo.GetAsync(id))!.Owner);
    }

    [Fact]
    public async Task ReleaseAny_with_empty_owner_list_removes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);

        await repo.ReleaseAnyAsync(id, Array.Empty<string>());

        Assert.Equal(owner, (await repo.GetAsync(id))!.Owner);
    }

    [Fact]
    public async Task RequestKick_stamps_entry_of_holder()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);
        Assert.Null((await repo.GetAsync(id))!.KickRequestedAt);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await repo.RequestKickAsync(id, owner);

        var kick = (await repo.GetAsync(id))!.KickRequestedAt;
        Assert.NotNull(kick);
        Assert.Equal(DateTimeKind.Utc, kick!.Value.Kind);
        Assert.InRange(kick.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task RequestKick_first_request_wins()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);
        await repo.RequestKickAsync(id, owner);
        var first = (await repo.GetAsync(id))!.KickRequestedAt;
        await Task.Delay(50);

        await repo.RequestKickAsync(id, owner); 

        Assert.Equal(first, (await repo.GetAsync(id))!.KickRequestedAt);
    }

    [Fact]
    public async Task RequestKick_for_non_holder_or_unknown_account_is_noop()
    {
        if (!_fx.Available) return;
        var repo = new OnlineAccountRegistry(_fx.Db!);
        var id = NewId();
        var owner = NewOwner();
        await repo.ClaimAsync(id, owner);

        await repo.RequestKickAsync(id, NewOwner()); 
        await repo.RequestKickAsync(NewId(), owner); 

        Assert.Null((await repo.GetAsync(id))!.KickRequestedAt);
    }
}
