using RO2.Companions.Application;
using RO2.Companions.Domain;
using RO2.GameData.Tables;
using RO2.Inventory.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>B11 S14/S15: the reins grant, vehicle list and riding extension use cases over fake repositories.</summary>
public class VehicleUseCaseTests
{
    private const double Now = 46223.5720;

    private static readonly RidingDurationGroup[] Groups = { new() { Item_ID = 2, RidingDuration_ID = 1 }, new() { Item_ID = 1, RidingDuration_ID = 1 } };
    private static readonly RidingInfo[] Vehicles = { new() { ID = 100, RidingDuration_GroupID = 1 } };
    private static readonly ItemCashInfo[] Cash = { new() { Item_ID = 2, Duration_Type = 1, Duration_Time = 14 }, new() { Item_ID = 1, Duration_Type = 0 } };

    private sealed class FakeRepo : IVehicleRepository
    {
        public (uint Char, uint Riding, uint? Days, double Now)? Granted;
        public Exception? Throw;
        public Task<IReadOnlyList<OwnedVehicle>> GetAsync(uint c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OwnedVehicle>?> TryExtendTimedAsync(uint c, uint r, uint d, double n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OwnedVehicle>> GrantAsync(uint c, uint r, uint? d, double n, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            Granted = (c, r, d, n);
            return Task.FromResult<IReadOnlyList<OwnedVehicle>>(VehicleRules.Grant(new List<OwnedVehicle>(), r, d, n));
        }
        public Task<IReadOnlyList<OwnedVehicle>> LoadAndCutToWholeSecondsAsync(uint c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OwnedVehicle>>(new[] { new OwnedVehicle { RidingId = 7, ExpiryOle = c } });
    }

    [Fact]
    public async Task GrantReins_resolves_then_grants_with_the_passed_clock()
    {
        var repo = new FakeRepo();
        var r = await new GrantReinsUseCase(repo).GrantAsync(5, 2, Groups, Vehicles, Cash, Now);

        Assert.Equal(GrantReinsStatus.Granted, r.Status);
        Assert.Equal((5u, 100u, (uint?)14, Now), repo.Granted);
        Assert.Equal(100u, r.RidingId);
        Assert.Equal(14u, r.Days);
        Assert.Equal((double)(float)(Now + 14), Assert.Single(r.Vehicles!).ExpiryOle);
    }

    [Fact]
    public async Task GrantReins_permanent_reins_grants_with_null_days()
    {
        var repo = new FakeRepo();
        var r = await new GrantReinsUseCase(repo).GrantAsync(5, 1, Groups, Vehicles, Cash, Now);

        Assert.Equal(GrantReinsStatus.Granted, r.Status);
        Assert.Null(r.Days);
        Assert.Equal(0.0, Assert.Single(r.Vehicles!).ExpiryOle);
    }

    [Fact]
    public async Task GrantReins_unresolved_item_stores_nothing()
    {
        var repo = new FakeRepo();
        var r = await new GrantReinsUseCase(repo).GrantAsync(5, 99, Groups, Vehicles, Cash, Now);

        Assert.Equal(GrantReinsStatus.Unresolved, r.Status);
        Assert.Null(repo.Granted);
        Assert.Null(r.Vehicles);
    }

    [Fact]
    public async Task GrantReins_store_failure_comes_back_as_a_result_with_the_resolved_vehicle()
    {
        var boom = new InvalidOperationException("db");
        var r = await new GrantReinsUseCase(new FakeRepo { Throw = boom }).GrantAsync(5, 2, Groups, Vehicles, Cash, Now);

        Assert.Equal(GrantReinsStatus.StoreFailed, r.Status);
        Assert.Same(boom, r.Error);
        Assert.Equal(100u, r.RidingId);
        Assert.Equal(14u, r.Days);
    }

    [Fact]
    public async Task GrantReins_cancellation_still_throws()
        => await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GrantReinsUseCase(new FakeRepo { Throw = new OperationCanceledException() }).GrantAsync(5, 2, Groups, Vehicles, Cash, Now));

    [Fact]
    public async Task ListVehicles_returns_the_repositorys_cut_list()
    {
        var list = await new ListVehiclesUseCase(new FakeRepo()).LoadAndCutAsync(46000);
        Assert.Equal(46000.0, Assert.Single(list).ExpiryOle);
    }

    // ---- S15: ExtendRidingUseCase ----

    private static readonly DurationExtendGroup[] ExtendGroups = { new() { Item_ID = 501, DurationExtend_ID = 1 } };
    private static readonly ItemCashInfo[] ExtendCash =
    {
        new() { Item_ID = 11, DurationExtend_GroupID = 1 },
        new() { Item_ID = 501, Duration_Type = 1, Duration_Time = 7 },
    };
    private static readonly RidingInfo[] ExtendRidings = { new() { ID = 100, RidingDuration_GroupID = 1 } };
    private static readonly RidingDurationGroup[] ExtendReins = { new() { Item_ID = 11, RidingDuration_ID = 1 } };

    private sealed class ExtendRepo(List<string> log) : IVehicleRepository
    {
        public Exception? Throw;
        public bool NotOwned;
        public (uint Char, uint Riding, uint Days, double Now)? Extended;
        public Task<IReadOnlyList<OwnedVehicle>> GetAsync(uint c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OwnedVehicle>> GrantAsync(uint c, uint r, uint? d, double n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OwnedVehicle>> LoadAndCutToWholeSecondsAsync(uint c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OwnedVehicle>?> TryExtendTimedAsync(uint c, uint r, uint d, double n, CancellationToken ct = default)
        {
            log.Add("extend");
            if (Throw is not null) throw Throw;
            if (NotOwned) return Task.FromResult<IReadOnlyList<OwnedVehicle>?>(null);
            Extended = (c, r, d, n);
            return Task.FromResult<IReadOnlyList<OwnedVehicle>?>(new[] { new OwnedVehicle { RidingId = r, ExpiryOle = n + d } });
        }
    }

    private sealed class TicketBag(List<string> log, InventoryItem? row) : IInventoryRepository
    {
        public bool Removed;
        public InventoryItem? Updated;
        public Task<InventoryItem?> GetByIdAsync(ulong id) { log.Add("get"); return Task.FromResult(row); }
        public Task RemoveItemAsync(ulong itemId) { log.Add("remove"); Removed = true; return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) { log.Add("update"); Updated = item; return Task.CompletedTask; }
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) => throw new NotSupportedException();
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => throw new NotSupportedException();
        public Task AddItemAsync(InventoryItem item) => throw new NotSupportedException();
        public Task<ulong> GetNextIdAsync() => throw new NotSupportedException();
        public Task<int> RepairSlotsAsync(uint characterId) => throw new NotSupportedException();
    }

    private static InventoryItem Ticket(int count = 1, uint item = 501, uint owner = 5, byte storage = 0)
        => new() { Id = 77, CharacterId = owner, ItemId = item, Count = count, StorageType = storage };

    private static Task<ExtendRidingResult> Extend(ExtendRepo repo, TicketBag bag)
        => new ExtendRidingUseCase(repo, bag).ExtendAsync(5, 100, 77, ExtendGroups, ExtendCash, ExtendRidings, ExtendReins, Now);

    [Fact]
    public async Task ExtendRiding_extends_then_consumes_the_last_ticket_in_that_order()
    {
        var log = new List<string>();
        var repo = new ExtendRepo(log);
        var bag = new TicketBag(log, Ticket(count: 1));
        var r = await Extend(repo, bag);

        Assert.Equal(ExtendRidingStatus.Extended, r.Status);
        Assert.Equal((5u, 100u, 7u, Now), repo.Extended);
        Assert.Equal(Now + 7, r.ExpiryOle);
        Assert.Equal(7u, r.Days);
        Assert.Equal(0, r.Remaining);
        Assert.True(bag.Removed);
        Assert.Equal(new[] { "get", "extend", "remove" }, log);
    }

    [Fact]
    public async Task ExtendRiding_a_stack_is_decremented_not_removed()
    {
        var log = new List<string>();
        var bag = new TicketBag(log, Ticket(count: 3));
        var r = await Extend(new ExtendRepo(log), bag);

        Assert.Equal(ExtendRidingStatus.Extended, r.Status);
        Assert.Equal(2, r.Remaining);
        Assert.Equal(2, bag.Updated!.Count);
        Assert.False(bag.Removed);
        Assert.Same(bag.Updated, r.Ticket);
    }

    [Theory]
    [InlineData(false, 0u, 5u)]   // no row
    [InlineData(true, 0u, 6u)]    // another character's item
    [InlineData(true, 1u, 5u)]    // equipped (storage 1)
    [InlineData(true, 2u, 5u)]    // bank (storage 2)
    [InlineData(true, 0u, 5u, 0)] // empty stack
    public async Task ExtendRiding_a_ticket_the_character_does_not_hold_in_a_bag_touches_nothing(bool row, uint storage, uint owner, int count = 1)
    {
        var log = new List<string>();
        var bag = new TicketBag(log, row ? Ticket(count, 501, owner, (byte)storage) : null);
        var r = await Extend(new ExtendRepo(log), bag);

        Assert.Equal(ExtendRidingStatus.TicketNotHeld, r.Status);
        Assert.Equal(new[] { "get" }, log);
    }

    [Fact]
    public async Task ExtendRiding_an_item_the_tables_do_not_accept_stores_and_consumes_nothing()
    {
        var log = new List<string>();
        var r = await Extend(new ExtendRepo(log), new TicketBag(log, Ticket(item: 999)));

        Assert.Equal(ExtendRidingStatus.NotExtendable, r.Status);
        Assert.Equal(999u, r.Ticket!.ItemId);
        Assert.Equal(new[] { "get" }, log);
    }

    [Fact]
    public async Task ExtendRiding_a_vehicle_that_is_not_owned_timed_does_not_consume()
    {
        var log = new List<string>();
        var r = await Extend(new ExtendRepo(log) { NotOwned = true }, new TicketBag(log, Ticket()));

        Assert.Equal(ExtendRidingStatus.NotOwnedTimed, r.Status);
        Assert.Equal(new[] { "get", "extend" }, log);
    }

    [Fact]
    public async Task ExtendRiding_store_failure_comes_back_as_a_result_and_does_not_consume()
    {
        var log = new List<string>();
        var boom = new InvalidOperationException("db");
        var r = await Extend(new ExtendRepo(log) { Throw = boom }, new TicketBag(log, Ticket()));

        Assert.Equal(ExtendRidingStatus.StoreFailed, r.Status);
        Assert.Same(boom, r.Error);
        Assert.Equal(new[] { "get", "extend" }, log);
    }

    [Fact]
    public async Task ExtendRiding_cancellation_still_throws()
    {
        var log = new List<string>();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Extend(new ExtendRepo(log) { Throw = new OperationCanceledException() }, new TicketBag(log, Ticket())));
    }
}
