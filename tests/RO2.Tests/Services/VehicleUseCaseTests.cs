using RO2.Companions.Application;
using RO2.Companions.Domain;
using RO2.GameData.Tables;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>B11 S14: the reins grant and vehicle list use cases over a fake <see cref="IVehicleRepository"/>.</summary>
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
}
