using RO2.BuildingBlocks.Bus;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Application.Commands;
using RO2.Identity.Domain.Account;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// The S-H9 store scenarios, written once against <see cref="IAccountRepository"/> so the Postgres and the Mongo
/// provider are held to the same contract: case-insensitive username uniqueness and lost-update safety
/// between a PIN set and a login.
/// </summary>
internal static class AccountStoreHardeningScenarios
{
    internal sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string plaintext) => "H:" + plaintext;
        public bool Verify(string plaintext, string hash) => hash == "H:" + plaintext;
    }

    private sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    internal static AuthenticateCommandHandler Handler(IAccountRepository repo)
        => new(repo, new FakeHasher(), new SystemClock(), new InMemoryEventBus());

    /// <summary>
    /// A username differing only in case is the same account: its registration is refused as a
    /// <see cref="DomainException"/>, and lookups find the original row from either spelling.
    /// </summary>
    internal static async Task CaseVariantUsernameIsTheSameAccount(IAccountRepository repo, string username)
    {
        var id = await repo.NextIdAsync();
        await repo.AddAsync(Account.Register(id, username, "pw", new FakeHasher(), new SystemClock()));

        var variant = username.ToUpperInvariant();
        Assert.NotEqual(username, variant);
        Assert.True(await repo.UsernameExistsAsync(variant));
        Assert.Equal(id, (await repo.GetByUsernameAsync(variant))!.Id);

        await Assert.ThrowsAsync<DomainException>(async () =>
            await repo.AddAsync(Account.Register(await repo.NextIdAsync(), variant, "pw2", new FakeHasher(), new SystemClock())));
    }

    /// <summary>
    /// A login and a PIN set that both loaded the account before either saved: both writes persist. Done first
    /// deterministically (the stale copy saves last, which a whole-row replace would turn into a lost PIN), then
    /// as truly concurrent handlers.
    /// </summary>
    internal static async Task SetSecondPasswordAndAuthenticateBothPersist(IAccountRepository repo, string usernamePrefix)
    {
        var hasher = new FakeHasher();
        var clock = new SystemClock();

        // Deterministic interleaving.
        var username = usernamePrefix + "_det";
        var id = await repo.NextIdAsync();
        await repo.AddAsync(Account.Register(id, username, "pw", hasher, clock));
        var staleLogin = (await repo.GetByIdAsync(id))!;
        var pinSetter = (await repo.GetByIdAsync(id))!;

        pinSetter.SetSecondPassword("1234", hasher);
        await repo.UpdateAsync(pinSetter);
        Assert.True(staleLogin.Authenticate("pw", hasher, clock).Succeeded);
        await repo.UpdateAsync(staleLogin);   // loaded before the PIN existed

        var reloaded = (await repo.GetByIdAsync(id))!;
        Assert.Equal("H:1234", reloaded.SecondPasswordHash);
        Assert.NotNull(reloaded.LastLogin);

        // Concurrent handlers, repeated.
        var setPin = new SetSecondPasswordCommandHandler(repo, hasher);
        var login = Handler(repo);
        for (var i = 0; i < 10; i++)
        {
            var name = $"{usernamePrefix}_c{i}";
            var accountId = await repo.NextIdAsync();
            await repo.AddAsync(Account.Register(accountId, name, "pw", hasher, clock));

            await Task.WhenAll(
                Task.Run(() => setPin.HandleAsync(new SetSecondPasswordCommand(accountId.Value, "4321"))),
                Task.Run(() => login.HandleAsync(new AuthenticateCommand(name, "pw"))));

            var after = (await repo.GetByIdAsync(accountId))!;
            Assert.Equal("H:4321", after.SecondPasswordHash);
            Assert.NotNull(after.LastLogin);
        }
    }
}
