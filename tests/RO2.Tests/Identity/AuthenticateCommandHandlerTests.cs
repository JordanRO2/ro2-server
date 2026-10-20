using RO2.BuildingBlocks.Bus;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Application.Commands;
using RO2.Identity.Domain.Account;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// <see cref="AuthenticateCommandHandler"/>: the failure reason the Login gateway turns into the 12502 code
/// (wrong password 1004, active ban 1005, no such account 1003). An unknown username is always 1003 now —
/// auto-registration was removed, so a login attempt never creates an account.
/// </summary>
public sealed class AuthenticateCommandHandlerTests
{
    private const string Username = "tester";
    private const string Password = "s3cret";
    private static readonly DateTime Now = new(2026, 10, 11, 12, 00, 00, DateTimeKind.Utc);

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string plaintext) => "H:" + plaintext;
        public bool Verify(string plaintext, string hash) => hash == "H:" + plaintext;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    /// <summary>An in-memory account store keyed by username.</summary>
    private sealed class FakeAccounts : IAccountRepository
    {
        public readonly Dictionary<string, Account> ByName = new(StringComparer.Ordinal);
        private uint _next = 100;

        public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
            => Task.FromResult(ByName.Values.FirstOrDefault(a => a.Id == id));

        public Task AddAsync(Account aggregate, CancellationToken cancellationToken = default)
        {
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

    private static (AuthenticateCommandHandler Handler, FakeAccounts Accounts) Create()
    {
        var accounts = new FakeAccounts();
        return (new AuthenticateCommandHandler(accounts, new FakeHasher(), new FixedClock(Now), new InMemoryEventBus()), accounts);
    }

    private static Account Existing(BanPolicy ban) => Account.Rehydrate(
        AccountId.From(42), Username, "H:" + Password, secondPasswordHash: null,
        Email.None, ban, accessLevel: 0, createdAt: Now.AddDays(-10), lastLogin: null);

    [Fact]
    public async Task WrongPassword_ReportsWrongPassword()
    {
        var (handler, accounts) = Create();
        accounts.ByName[Username] = Existing(BanPolicy.NotBanned);

        var result = await handler.HandleAsync(new AuthenticateCommand(Username, "nope"));

        Assert.False(result.Succeeded);
        Assert.Equal(0u, result.AccountId);
        Assert.Equal(Username, result.Username);
        Assert.Equal(AuthenticateFailure.WrongPassword, result.Failure);
    }

    [Fact]
    public async Task ActiveBan_ReportsBanned_NotWrongPassword()
    {
        var (handler, accounts) = Create();
        accounts.ByName[Username] = Existing(new BanPolicy(isBanned: true, Now.AddDays(1), banReason: "test"));

        var result = await handler.HandleAsync(new AuthenticateCommand(Username, Password));

        Assert.False(result.Succeeded);
        Assert.Equal(0u, result.AccountId);
        Assert.Equal(AuthenticateFailure.Banned, result.Failure);
    }

    [Fact]
    public async Task ExpiredBan_LogsIn()
    {
        var (handler, accounts) = Create();
        accounts.ByName[Username] = Existing(new BanPolicy(isBanned: true, Now.AddDays(-1), banReason: "test"));

        var result = await handler.HandleAsync(new AuthenticateCommand(Username, Password));

        Assert.True(result.Succeeded);
        Assert.Equal(AuthenticateFailure.None, result.Failure);
    }

    [Fact]
    public async Task UnknownWellFormedName_IsNotRegistered_AndReportsUnknownAccount()
    {
        var (handler, accounts) = Create();

        var result = await handler.HandleAsync(new AuthenticateCommand("juarayya", "pw"));

        // Auto-registration was removed: even a well-formed unknown name is answered 1003, not created.
        Assert.False(result.Succeeded);
        Assert.Equal(0u, result.AccountId);
        Assert.Equal(AuthenticateFailure.UnknownAccount, result.Failure);
        Assert.Empty(accounts.ByName);
    }

    // Names answered with 1003: CR/LF, a leading space. Plus the names
    // Account.Register would throw on (blank) and one past Credentials.MaxUsernameLength.
    [Theory]
    [InlineData("demonky1979\r\n")]
    [InlineData(" kevin232")]
    [InlineData("")]
    [InlineData("tab\tname")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234567890")]
    public async Task UnknownMalformedName_IsNotRegistered_AndReportsUnknownAccount(string name)
    {
        var (handler, accounts) = Create();

        var result = await handler.HandleAsync(new AuthenticateCommand(name, "pw"));

        Assert.False(result.Succeeded);
        Assert.Equal(0u, result.AccountId);
        Assert.Equal(name, result.Username);
        Assert.Equal(AuthenticateFailure.UnknownAccount, result.Failure);
        Assert.Empty(accounts.ByName);
    }

    [Fact]
    public async Task UnknownNameWithEmptyPassword_IsNotRegistered_AndDoesNotThrow()
    {
        var (handler, accounts) = Create();

        var result = await handler.HandleAsync(new AuthenticateCommand("newname", ""));

        Assert.Equal(AuthenticateFailure.UnknownAccount, result.Failure);
        Assert.Empty(accounts.ByName);
    }

    [Fact]
    public void MaxLengthName_IsRegistrable()
    {
        Assert.True(AuthenticateCommandHandler.IsRegistrableUsername(new string('a', Credentials.MaxUsernameLength)));
        Assert.False(AuthenticateCommandHandler.IsRegistrableUsername(new string('a', Credentials.MaxUsernameLength + 1)));
    }
}
