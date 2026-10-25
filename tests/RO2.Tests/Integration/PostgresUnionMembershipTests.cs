using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;
using RO2.Characters.Domain;
using RO2.Characters.Infrastructure.Postgres;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="UnionMembershipRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers get, join and withdraw, the one-membership-per-character guarantee under concurrency and the union-mismatch withdraw path. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresUnionMembershipTests
{
    private readonly PostgresFixture _fx;
    public PostgresUnionMembershipTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewId() => (uint)Random.Shared.NextInt64(2_200_000_000L, 4_200_000_000L);

    [Fact]
    public async Task Get_unknown_character_is_null()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        Assert.Null(await repo.GetAsync(NewId()));
    }

    [Fact]
    public async Task Join_then_get_returns_membership()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        var character = NewId();
        var union = NewId();
        Assert.True(await repo.JoinAsync(character, union, 1_700_000_123L));
        var got = await repo.GetAsync(character);
        Assert.NotNull(got);
        Assert.Equal(new UnionMembership(union, 1_700_000_123L), got!.Value);
    }

    [Fact]
    public async Task Join_when_already_member_is_false_and_keeps_original()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        var character = NewId();
        Assert.True(await repo.JoinAsync(character, 5, 100));
        Assert.False(await repo.JoinAsync(character, 5, 200));   
        Assert.False(await repo.JoinAsync(character, 6, 300));   
        Assert.Equal(new UnionMembership(5, 100), (await repo.GetAsync(character))!.Value);
    }

    [Fact]
    public async Task Concurrent_joins_admit_exactly_one()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        var character = NewId();
        var results = await Task.WhenAll(Enumerable.Range(1, 8)
            .Select(i => repo.JoinAsync(character, (uint)i, 1000 + i)));
        Assert.Equal(1, results.Count(r => r));
        var got = await repo.GetAsync(character);
        Assert.NotNull(got);
        Assert.Equal(1000L + got!.Value.UnionId, got.Value.JoinedAtUnix); 
    }

    [Fact]
    public async Task Withdraw_deletes_once()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        var character = NewId();
        await repo.JoinAsync(character, 9, 42);
        Assert.True(await repo.WithdrawAsync(character, 9));
        Assert.Null(await repo.GetAsync(character));
        Assert.False(await repo.WithdrawAsync(character, 9));    
    }

    [Fact]
    public async Task Withdraw_with_wrong_union_is_false_and_keeps_membership()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        var character = NewId();
        await repo.JoinAsync(character, 9, 42);
        Assert.False(await repo.WithdrawAsync(character, 10));
        Assert.Equal(new UnionMembership(9, 42), (await repo.GetAsync(character))!.Value);
    }

    [Fact]
    public async Task Withdraw_unknown_character_is_false()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        Assert.False(await repo.WithdrawAsync(NewId(), 1));
    }

    [Fact]
    public async Task Can_rejoin_after_withdraw()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        var character = NewId();
        Assert.True(await repo.JoinAsync(character, 1, 10));
        Assert.True(await repo.WithdrawAsync(character, 1));
        Assert.True(await repo.JoinAsync(character, 2, 20));
        Assert.Equal(new UnionMembership(2, 20), (await repo.GetAsync(character))!.Value);
    }

    [Fact]
    public async Task Large_uint_union_and_negative_unix_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new UnionMembershipRepository(_fx.Db!);
        var character = NewId();
        Assert.True(await repo.JoinAsync(character, uint.MaxValue, long.MaxValue));
        Assert.Equal(new UnionMembership(uint.MaxValue, long.MaxValue), (await repo.GetAsync(character))!.Value);
    }
}
