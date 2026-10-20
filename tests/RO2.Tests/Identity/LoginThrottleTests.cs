using System.Net;
using RO2.BuildingBlocks.Bus;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Application.Commands;
using RO2.Identity.Domain.Account;
using RO2.Identity.Infrastructure;
using RO2.Server.Login.Services;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// S-H3: the Login-layer throttle in front of the Identity authenticate command. A counting hasher stands in for
/// BCrypt so each test can assert the refused attempts never reached it. Time is the limiter's own
/// <c>nowMs</c>, so nothing sleeps.
/// </summary>
public sealed class LoginThrottleTests
{
    private const string Good = "s3cret";
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private sealed class CountingHasher : IPasswordHasher
    {
        public int Hashes;
        public int Verifies;
        public string Hash(string plaintext) { Hashes++; return "H:" + plaintext; }
        public bool Verify(string plaintext, string hash) { Verifies++; return hash == "H:" + plaintext; }
        public int Calls => Hashes + Verifies;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FakeAccounts : IAccountRepository
    {
        public readonly Dictionary<string, Account> ByName = new(StringComparer.Ordinal);
        public int Adds;
        private uint _next = 100;

        public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
            => Task.FromResult(ByName.Values.FirstOrDefault(a => a.Id == id));
        public Task AddAsync(Account aggregate, CancellationToken cancellationToken = default)
        {
            Adds++;
            ByName[aggregate.Username] = aggregate;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Account aggregate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Account?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult(ByName.TryGetValue(username, out var a) ? a : null);
        public Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult(ByName.ContainsKey(username));
        public Task<AccountId> NextIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(AccountId.From(_next++));
    }

    private sealed class Rig
    {
        public readonly CountingHasher Hasher = new();
        public readonly FakeAccounts Accounts = new();
        public readonly LoginThrottle Throttle = new();
        public readonly AuthenticateCommandHandler Handler;

        public Rig() => Handler = new AuthenticateCommandHandler(Accounts, Hasher, new FixedClock(), new InMemoryEventBus());

        public void Seed(string username, string password = Good)
            => Accounts.ByName[username] = Account.Rehydrate(
                AccountId.From((uint)(1000 + Accounts.ByName.Count)), username, "H:" + password, null,
                Email.None, BanPolicy.NotBanned, 0, Now.AddDays(-10), null);

        public Task<AuthenticateResult> Login(string ip, string username, string password, long nowMs)
            => Throttle.AuthenticateAsync(ip, new AuthenticateCommand(username, password), Handler, nowMs);
    }

    [Fact]
    public async Task KFailedLoginsFromOneIp_NextIsRefused_WithoutTheHasher()
    {
        var rig = new Rig();
        for (var i = 0; i <= LoginThrottle.MaxAttemptsPerIp; i++) rig.Seed("user" + i);

        for (var i = 0; i < LoginThrottle.MaxAttemptsPerIp; i++)
        {
            var r = await rig.Login("10.0.0.1", "user" + i, "wrong", 1000 + i);
            Assert.Equal(AuthenticateFailure.WrongPassword, r.Failure);
        }
        Assert.Equal(LoginThrottle.MaxAttemptsPerIp, rig.Hasher.Verifies);

        // Even the right password is refused once the IP budget is spent, and the hasher is not called.
        var refused = await rig.Login("10.0.0.1", "user" + LoginThrottle.MaxAttemptsPerIp, Good, 2000);
        Assert.False(refused.Succeeded);
        Assert.Equal(AuthenticateFailure.WrongPassword, refused.Failure);
        Assert.Equal(0u, refused.AccountId);
        Assert.Equal(LoginThrottle.MaxAttemptsPerIp, rig.Hasher.Verifies);

        // The window expires: the same IP is served again.
        var later = await rig.Login("10.0.0.1", "user" + LoginThrottle.MaxAttemptsPerIp, Good, 1000 + LoginThrottle.IpWindowMs);
        Assert.True(later.Succeeded);
    }

    [Fact]
    public async Task GoodLoginFromAnotherIp_IsUnaffectedByAnIpsLockout()
    {
        var rig = new Rig();
        rig.Seed("victim");
        for (var i = 0; i < LoginThrottle.MaxAttemptsPerIp; i++) rig.Seed("bot" + i);
        for (var i = 0; i < LoginThrottle.MaxAttemptsPerIp; i++)
            await rig.Login("6.6.6.6", "bot" + i, "wrong", 100 + i);
        Assert.False((await rig.Login("6.6.6.6", "bot0", Good, 200)).Succeeded);

        var ok = await rig.Login("7.7.7.7", "victim", Good, 300);

        Assert.True(ok.Succeeded);
    }

    [Fact]
    public async Task AccountSpreadOverManyIps_IsLockedAfterItsOwnBudget_AndClearedBySuccess()
    {
        var rig = new Rig();
        rig.Seed("target");

        for (var i = 0; i < LoginThrottle.MaxAttemptsPerAccount; i++)
            await rig.Login("20.0.0." + i, "target", "wrong", 10 + i);
        var verifies = rig.Hasher.Verifies;
        Assert.Equal(LoginThrottle.MaxAttemptsPerAccount, verifies);

        var refused = await rig.Login("20.0.0.99", "TARGET", Good, 50); // case does not dodge the lock
        Assert.False(refused.Succeeded);
        Assert.Equal(verifies, rig.Hasher.Verifies);

        // After the window a success is admitted and clears the account's counter.
        Assert.True((await rig.Login("20.0.1.1", "target", Good, 10 + LoginThrottle.AccountWindowMs)).Succeeded);
        for (var i = 0; i < LoginThrottle.MaxAttemptsPerAccount - 1; i++)
            await rig.Login("20.0.2." + i, "target", "wrong", 10 + LoginThrottle.AccountWindowMs + 1 + i);
        Assert.True((await rig.Login("20.0.3.1", "target", Good, 10 + LoginThrottle.AccountWindowMs + 100)).Succeeded);
    }

    [Fact]
    public async Task PasswordOver72Bytes_IsRefused_NotTruncated()
    {
        var rig = new Rig();
        rig.Seed("longpw", new string('a', 72));

        var seventyThree = new string('a', 73);   // 73 ASCII bytes
        var multiByte = new string('é', 37); // 37 chars but 74 UTF-8 bytes

        // An existing account whose first 72 bytes match must NOT log in with a longer password.
        var r1 = await rig.Login("50.0.0.1", "longpw", seventyThree, 1);
        Assert.False(r1.Succeeded);
        Assert.Equal(AuthenticateFailure.WrongPassword, r1.Failure);

        // Nor can a new account be registered with one.
        var r2 = await rig.Login("50.0.0.1", "fresh1", seventyThree, 2);
        var r3 = await rig.Login("50.0.0.1", "fresh2", multiByte, 3);
        Assert.False(r2.Succeeded);
        Assert.False(r3.Succeeded);
        Assert.Equal(0, rig.Accounts.Adds);
        Assert.Equal(0, rig.Hasher.Calls);

        // Exactly 72 bytes is still fine.
        Assert.True((await rig.Login("50.0.0.1", "longpw", new string('a', 72), 4)).Succeeded);
    }

    [Fact]
    public void BCryptHasher_RefusesPasswordsOver72Bytes()
    {
        var hasher = new BCryptPasswordHasher();
        var hash = hasher.Hash(new string('a', 72));

        Assert.True(hasher.Verify(new string('a', 72), hash));
        Assert.False(hasher.Verify(new string('a', 73), hash)); // would match if BCrypt truncated
        Assert.Throws<ArgumentException>(() => hasher.Hash(new string('a', 73)));
        Assert.Throws<ArgumentException>(() => hasher.Hash(new string('€', 25))); // 75 bytes
    }

    [Fact]
    public void KeyFor_GroupsIpv6By64_AndUnmapsIpv4()
    {
        Assert.Equal(LoginThrottle.KeyFor(IPAddress.Parse("2001:db8:1:2::1")), LoginThrottle.KeyFor(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.NotEqual(LoginThrottle.KeyFor(IPAddress.Parse("2001:db8:1:2::1")), LoginThrottle.KeyFor(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("1.2.3.4", LoginThrottle.KeyFor(IPAddress.Parse("::ffff:1.2.3.4")));
        Assert.Equal("unknown", LoginThrottle.KeyFor(null));
    }
}
