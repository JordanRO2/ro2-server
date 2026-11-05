using RO2.Domain.Entities;
using RO2.Inventory.Domain;
using RO2.Tests.Economy;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>INV-H7: ItemUseHandler reserves one unit with AdjustCountAsync(-1) before any effect and refunds it with +1.</summary>
public class ItemUseReserveTests
{
    private const uint Char = 7;

    private static (FakeInventoryRepository Repo, ulong Id) Stack(int count)
    {
        var repo = new FakeInventoryRepository(new RepoScheduler());
        var id = repo.Seed(new InventoryItem { CharacterId = Char, ItemId = 60000208, Count = count, Slot = 0 });
        return (repo, id);
    }

    [Fact]
    public async Task Two_concurrent_uses_of_a_two_stack_consume_exactly_two()
    {
        var (repo, id) = Stack(2);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => repo.AdjustCountAsync(id, Char, -1)));
        Assert.Equal(2, results.Count(r => r is not null));   // only two of four reserves win
        Assert.Equal(0, repo.Rows.Single().Count);
    }

    [Fact]
    public async Task A_reserve_followed_by_a_failing_effect_is_refunded()
    {
        var (repo, id) = Stack(2);
        Assert.Equal(1, await repo.AdjustCountAsync(id, Char, -1));
        Assert.Equal(2, await repo.AdjustCountAsync(id, Char, 1));
        Assert.Equal(2, repo.Rows.Single().Count);
    }
}
