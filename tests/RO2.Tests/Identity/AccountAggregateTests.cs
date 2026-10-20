using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Account;
using RO2.Identity.Domain.Events;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// Unit tests for the Account aggregate: authentication, automatic registration of unknown users, bans, and the domain events raised.
/// </summary>
/// <remarks>
/// Crypto and time are supplied as fakes.
/// </remarks>
public sealed class AccountAggregateTests
{
    private const string Username = "tester";
    private const string Password = "s3cret";
    private static readonly AccountId Id = AccountId.From(42);
    private static readonly DateTime Now = new(2026, 06, 22, 12, 00, 00, DateTimeKind.Utc);

 /// <summary>Deterministic password hasher for tests: the hash is a fixed prefix plus the plaintext.</summary>
    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string plaintext) => "H:" + plaintext;
        public bool Verify(string plaintext, string hash) => hash == "H:" + plaintext;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private static T SingleEvent<T>(Account account) where T : IDomainEvent
        => Assert.Single(account.DomainEvents.OfType<T>());

    
    [Fact]
    public void Register_UnknownUser_CreatesAccount_AndRaisesAccountRegistered()
    {
        var clock = new FixedClock(Now);

        var account = Account.Register(Id, Username, Password, new FakeHasher(), clock);

        Assert.Equal(Id, account.Id);
        Assert.Equal(Username, account.Username);
        Assert.Equal("H:" + Password, account.PasswordHash);
        Assert.Equal(Now, account.CreatedAt);
        Assert.Null(account.LastLogin);
        Assert.False(account.HasSecondPassword);

        var registered = SingleEvent<AccountRegistered>(account);
        Assert.Equal(Id, registered.AccountId);
        Assert.Equal(Username, registered.Username);
        Assert.Equal(Now, registered.OccurredOn);
        Assert.NotEqual(Guid.Empty, registered.EventId);
    }

    [Fact]
    public void Register_ThenMarkAuthenticatedAfterRegister_Succeeds_AsAutoRegistered()
    {
        var clock = new FixedClock(Now);
        var account = Account.Register(Id, Username, Password, new FakeHasher(), clock);

        var result = account.MarkAuthenticatedAfterRegister(clock);

        Assert.True(result.Succeeded);
        Assert.True(result.WasAutoRegistered);
        Assert.Equal(Now, account.LastLogin);

        var authed = SingleEvent<AccountAuthenticated>(account);
        Assert.True(authed.WasAutoRegistered);
        Assert.Equal(Id, authed.AccountId);
    }

    [Fact]
    public void Register_EmptyPassword_Throws_DomainException()
    {
        var clock = new FixedClock(Now);
        Assert.Throws<DomainException>(
            () => Account.Register(Id, Username, "", new FakeHasher(), clock));
    }

    
    [Fact]
    public void Authenticate_CorrectCredentials_Succeeds_AndRaisesAccountAuthenticated()
    {
        var account = NotBannedAccount();
        var clock = new FixedClock(Now);

        var result = account.Authenticate(Password, new FakeHasher(), clock);

        Assert.True(result.Succeeded);
        Assert.False(result.WasAutoRegistered);
        Assert.Null(result.FailureReason);
        Assert.Equal(Now, account.LastLogin);

        var authed = SingleEvent<AccountAuthenticated>(account);
        Assert.Equal(Id, authed.AccountId);
        Assert.Equal(Username, authed.Username);
        Assert.False(authed.WasAutoRegistered);
        Assert.Equal(Now, authed.OccurredOn);
        
        Assert.Empty(account.DomainEvents.OfType<AuthenticationFailed>());
    }

    [Fact]
    public void Authenticate_CorrectCredentials_WithPinSet_ReportsHasSecondPassword()
    {
        var account = NotBannedAccount();
        account.SetSecondPassword("1234", new FakeHasher());

        var result = account.Authenticate(Password, new FakeHasher(), new FixedClock(Now));

        Assert.True(result.Succeeded);
        Assert.True(result.HasSecondPassword);
        Assert.True(SingleEvent<AccountAuthenticated>(account).HasSecondPassword);
    }

    
    [Fact]
    public void Authenticate_WrongPassword_Fails_RaisesAuthenticationFailed_NoThrow()
    {
        var account = NotBannedAccount();
        var clock = new FixedClock(Now);

        
        var result = account.Authenticate("wrong-password", new FakeHasher(), clock);

        Assert.False(result.Succeeded);
        Assert.Equal(AuthenticationFailureReason.WrongPassword, result.FailureReason);
        Assert.Null(account.LastLogin);

        var failed = SingleEvent<AuthenticationFailed>(account);
        Assert.Equal(AuthenticationFailureReason.WrongPassword, failed.Reason);
        Assert.Equal(Id, failed.AccountId);
        Assert.Equal(Now, failed.OccurredOn);
        
        Assert.Empty(account.DomainEvents.OfType<AccountAuthenticated>());
    }

    
    [Fact]
    public void Authenticate_BannedAccount_Rejected_RaisesAuthenticationFailed_Banned()
    {
        
        var account = BannedAccount(banExpires: null);
        var clock = new FixedClock(Now);

        var result = account.Authenticate(Password, new FakeHasher(), clock);

        Assert.False(result.Succeeded);
        Assert.Equal(AuthenticationFailureReason.Banned, result.FailureReason);
        Assert.Null(account.LastLogin);

        var failed = SingleEvent<AuthenticationFailed>(account);
        Assert.Equal(AuthenticationFailureReason.Banned, failed.Reason);
        Assert.Empty(account.DomainEvents.OfType<AccountAuthenticated>());
    }

    [Fact]
    public void Authenticate_BanWithFutureExpiry_StillRejected()
    {
        var account = BannedAccount(banExpires: Now.AddDays(1));

        var result = account.Authenticate(Password, new FakeHasher(), new FixedClock(Now));

        Assert.False(result.Succeeded);
        Assert.Equal(AuthenticationFailureReason.Banned, result.FailureReason);
    }

    [Fact]
    public void Authenticate_BanWithPastExpiry_TreatedAsNotBanned_Succeeds()
    {
        
        var account = BannedAccount(banExpires: Now.AddDays(-1));

        var result = account.Authenticate(Password, new FakeHasher(), new FixedClock(Now));

        Assert.True(result.Succeeded);
        Assert.Single(account.DomainEvents.OfType<AccountAuthenticated>());
    }

    [Fact]
    public void Authenticate_BannedAccount_WrongPassword_FailsAsWrongPassword_BanNotEvaluated()
    {
        
        var account = BannedAccount(banExpires: null);

        var result = account.Authenticate("wrong-password", new FakeHasher(), new FixedClock(Now));

        Assert.False(result.Succeeded);
        Assert.Equal(AuthenticationFailureReason.WrongPassword, result.FailureReason);
    }

    
    private static Account NotBannedAccount() => Account.Rehydrate(
        Id, Username, "H:" + Password, secondPasswordHash: null,
        Email.None, BanPolicy.NotBanned, accessLevel: 0, createdAt: Now.AddDays(-10), lastLogin: null);

    private static Account BannedAccount(DateTime? banExpires) => Account.Rehydrate(
        Id, Username, "H:" + Password, secondPasswordHash: null,
        Email.None, new BanPolicy(isBanned: true, banExpires, banReason: "test"),
        accessLevel: 0, createdAt: Now.AddDays(-10), lastLogin: null);
}
