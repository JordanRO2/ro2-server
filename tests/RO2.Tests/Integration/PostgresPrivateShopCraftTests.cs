using RO2.Economy.Infrastructure.Postgres;
using RO2.Economy.Domain;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="PrivateShopCraftRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers the ordered whole-list replace, duplicates, price bit patterns, character-id round-trips and per-character isolation. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresPrivateShopCraftTests
{
    private readonly PostgresFixture _fx;
    public PostgresPrivateShopCraftTests(PostgresFixture fx) => _fx = fx;

    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    [Fact]
    public async Task Get_unknown_character_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        Assert.Empty(await repo.GetOffersAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Replace_then_get_returns_offers_in_list_order()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var id = NewCharacterId();
        var offers = new[]
        {
            new PrivateShopCraftOffer(1065, 1),
            new PrivateShopCraftOffer(1061, 99_999_999),
            new PrivateShopCraftOffer(1062, 5),
            new PrivateShopCraftOffer(1063, 1),
            new PrivateShopCraftOffer(1064, 7),
        };
        await repo.ReplaceOffersAsync(id, offers);
        Assert.Equal(offers, await repo.GetOffersAsync(id)); 
    }

    [Fact]
    public async Task Replace_overwrites_the_whole_previous_list()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ReplaceOffersAsync(id, new[] { new PrivateShopCraftOffer(1, 10), new PrivateShopCraftOffer(2, 20) });
        var second = new[] { new PrivateShopCraftOffer(3, 30) };
        await repo.ReplaceOffersAsync(id, second); 
        Assert.Equal(second, await repo.GetOffersAsync(id));
    }

    [Fact]
    public async Task Replace_with_empty_list_clears_the_offers()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ReplaceOffersAsync(id, new[] { new PrivateShopCraftOffer(9, 90) });
        await repo.ReplaceOffersAsync(id, Array.Empty<PrivateShopCraftOffer>());
        Assert.Empty(await repo.GetOffersAsync(id));
    }

    [Fact]
    public async Task First_replace_with_empty_list_reads_back_empty()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.ReplaceOffersAsync(id, Array.Empty<PrivateShopCraftOffer>()); 
        Assert.Empty(await repo.GetOffersAsync(id));
    }

    [Fact]
    public async Task Duplicate_craft_ids_and_order_are_preserved()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var id = NewCharacterId();
        var offers = new[]
        {
            new PrivateShopCraftOffer(500, 3),
            new PrivateShopCraftOffer(400, 2),
            new PrivateShopCraftOffer(500, 3),
            new PrivateShopCraftOffer(500, 1),
        };
        await repo.ReplaceOffersAsync(id, offers);
        Assert.Equal(offers, await repo.GetOffersAsync(id));
    }

    [Fact]
    public async Task Extreme_ushort_ids_and_u64_prices_round_trip_bit_for_bit()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var id = NewCharacterId();
        var offers = new[]
        {
            new PrivateShopCraftOffer(0, 0),
            new PrivateShopCraftOffer(ushort.MaxValue, (ulong)long.MaxValue),
            new PrivateShopCraftOffer(2, (ulong)long.MaxValue + 1), 
            new PrivateShopCraftOffer(3, ulong.MaxValue),
        };
        await repo.ReplaceOffersAsync(id, offers);
        Assert.Equal(offers, await repo.GetOffersAsync(id));
    }

    [Fact]
    public async Task Character_id_above_int_max_round_trips()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        
        var id = 3_000_000_000u + (uint)Random.Shared.Next(0, 1_000_000_000);
        var offers = new[] { new PrivateShopCraftOffer(77, 770) };
        await repo.ReplaceOffersAsync(id, offers);
        Assert.Equal(offers, await repo.GetOffersAsync(id));
    }

    [Fact]
    public async Task Characters_are_isolated_from_each_other()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        var offersA = new[] { new PrivateShopCraftOffer(10, 100) };
        var offersB = new[] { new PrivateShopCraftOffer(20, 200), new PrivateShopCraftOffer(21, 210) };
        await repo.ReplaceOffersAsync(a, offersA);
        await repo.ReplaceOffersAsync(b, offersB);
        var replacedA = new[] { new PrivateShopCraftOffer(11, 110) };
        await repo.ReplaceOffersAsync(a, replacedA); 
        Assert.Equal(offersB, await repo.GetOffersAsync(b));
        Assert.Equal(replacedA, await repo.GetOffersAsync(a));
    }

    [Fact]
    public async Task Concurrent_first_replaces_both_succeed_and_last_writer_wins()
    {
        if (!_fx.Available) return;
        var repo = new PrivateShopCraftRepository(_fx.Db!);
        var id = NewCharacterId();
        var one = new[] { new PrivateShopCraftOffer(1, 1) };
        var two = new[] { new PrivateShopCraftOffer(2, 2), new PrivateShopCraftOffer(3, 3) };
        
        await Task.WhenAll(repo.ReplaceOffersAsync(id, one), repo.ReplaceOffersAsync(id, two));
        var stored = await repo.GetOffersAsync(id);
        Assert.True(stored.SequenceEqual(one) || stored.SequenceEqual(two));
    }
}
