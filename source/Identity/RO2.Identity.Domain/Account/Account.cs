using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Events;

namespace RO2.Identity.Domain.Account;

/// <summary>
/// The account aggregate root. Owns the credentials, the second-password (PIN) hash, the ban policy and the
/// authentication rules, and raises domain events for registration and authentication.
/// </summary>
/// <remarks>Account ids are allocated by the application layer and supplied to <see cref="Register"/>.</remarks>
public sealed class Account : AggregateRoot<AccountId>
{
    private Account(
        AccountId id,
        Credentials credentials,
        string? secondPasswordHash,
        Email email,
        BanPolicy ban,
        byte accessLevel,
        DateTime createdAt,
        DateTime? lastLogin)
    {
        Id = id;
        _credentials = credentials;
        _secondPasswordHash = secondPasswordHash;
        _email = email;
        _ban = ban;
        _accessLevel = accessLevel;
        CreatedAt = createdAt;
        LastLogin = lastLogin;
    }

    private Credentials _credentials;
    private string? _secondPasswordHash;
    private Email _email;
    private BanPolicy _ban;
    private byte _accessLevel;

 /// <summary>The login key.</summary>
    public string Username => _credentials.Username;

 /// <summary>The BCrypt hash of the password.</summary>
    public string PasswordHash => _credentials.PasswordHash;

 /// <summary>The PIN hash, or <see langword="null"/> when no PIN is set.</summary>
    public string? SecondPasswordHash => _secondPasswordHash;

 /// <summary>The account email.</summary>
    public Email Email => _email;

 /// <summary>The ban policy.</summary>
    public BanPolicy Ban => _ban;

 /// <summary>The access level.</summary>
    public byte AccessLevel => _accessLevel;

 /// <summary>When the account was created.</summary>
    public DateTime CreatedAt { get; }

 /// <summary>When the account last logged in successfully, or <see langword="null"/>.</summary>
    public DateTime? LastLogin { get; private set; }

 /// <summary>True when a PIN is set.</summary>
    public bool HasSecondPassword => !string.IsNullOrEmpty(_secondPasswordHash);

 /// <summary>Creates a new account with a hashed password and raises <see cref="AccountRegistered"/>.</summary>
 /// <param name="id">The account id, allocated by the application layer.</param>
 /// <param name="username">The login key.</param>
 /// <param name="plaintextPassword">The plaintext password.</param>
 /// <param name="hasher">The password hasher.</param>
 /// <param name="clock">The clock for the creation time and event timestamp.</param>
 /// <returns>The new account, not yet persisted.</returns>
 /// <exception cref="DomainException">Thrown when the password is empty or the credentials are invalid.</exception>
    public static Account Register(
        AccountId id,
        string username,
        string plaintextPassword,
        IPasswordHasher hasher,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrEmpty(plaintextPassword))
        {
            throw new DomainException("password must not be empty");
        }

        var credentials = new Credentials(username, hasher.Hash(plaintextPassword));
        var now = clock.UtcNow;

        var account = new Account(
            id,
            credentials,
            secondPasswordHash: null,
            email: Email.None,
            ban: BanPolicy.NotBanned,
            accessLevel: 0,
            createdAt: now,
            lastLogin: null);

        account.Raise(new AccountRegistered(Guid.NewGuid(), now, id, username));
        return account;
    }

 /// <summary>Rebuilds an account from persisted state. Raises no events.</summary>
 /// <param name="id">The account id.</param>
 /// <param name="username">The login key.</param>
 /// <param name="passwordHash">The stored password hash.</param>
 /// <param name="secondPasswordHash">The stored PIN hash, or <see langword="null"/>.</param>
 /// <param name="email">The stored email.</param>
 /// <param name="ban">The stored ban policy.</param>
 /// <param name="accessLevel">The stored access level.</param>
 /// <param name="createdAt">When the account was created.</param>
 /// <param name="lastLogin">When the account last logged in, or <see langword="null"/>.</param>
 /// <returns>The rehydrated <see cref="Account"/>.</returns>
    public static Account Rehydrate(
        AccountId id,
        string username,
        string passwordHash,
        string? secondPasswordHash,
        Email email,
        BanPolicy ban,
        byte accessLevel,
        DateTime createdAt,
        DateTime? lastLogin)
    {
        return new Account(
            id,
            new Credentials(username, passwordHash),
            secondPasswordHash,
            email,
            ban,
            accessLevel,
            createdAt,
            lastLogin);
    }

 /// <summary>
 /// Verifies the password and the ban state. On success the account is marked as logged in and
 /// <see cref="AccountAuthenticated"/> is raised; otherwise <see cref="AuthenticationFailed"/> is raised with the reason.
 /// </summary>
 /// <param name="plaintextPassword">The plaintext password.</param>
 /// <param name="hasher">The password hasher.</param>
 /// <param name="clock">The clock for ban evaluation and event timestamps.</param>
 /// <returns>The authentication result.</returns>
    public AuthenticationResult Authenticate(string plaintextPassword, IPasswordHasher hasher, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.UtcNow;

        if (!hasher.Verify(plaintextPassword, _credentials.PasswordHash))
        {
            Raise(new AuthenticationFailed(
                Guid.NewGuid(), now, Id, Username, AuthenticationFailureReason.WrongPassword));
            return AuthenticationResult.Failure(AuthenticationFailureReason.WrongPassword);
        }

        if (_ban.IsActiveAt(now))
        {
            Raise(new AuthenticationFailed(
                Guid.NewGuid(), now, Id, Username, AuthenticationFailureReason.Banned));
            return AuthenticationResult.Failure(AuthenticationFailureReason.Banned);
        }

        return MarkAuthenticated(now, wasAutoRegistered: false);
    }

 /// <summary>Marks a freshly registered account as authenticated, without re-verifying the password.</summary>
 /// <param name="clock">The clock for the last-login time and event timestamp.</param>
 /// <returns>A successful <see cref="AuthenticationResult"/>.</returns>
    public AuthenticationResult MarkAuthenticatedAfterRegister(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return MarkAuthenticated(clock.UtcNow, wasAutoRegistered: true);
    }

    private AuthenticationResult MarkAuthenticated(DateTime now, bool wasAutoRegistered)
    {
        LastLogin = now;
        Raise(new AccountAuthenticated(
            Guid.NewGuid(), now, Id, Username, HasSecondPassword, wasAutoRegistered));
        return AuthenticationResult.Success(wasAutoRegistered, HasSecondPassword);
    }

 /// <summary>Sets or replaces the second password (PIN). The PIN is stored as a hash.</summary>
 /// <param name="pin">The PIN.</param>
 /// <param name="hasher">The password hasher.</param>
 /// <exception cref="DomainException">Thrown when the PIN is empty.</exception>
    public void SetSecondPassword(string pin, IPasswordHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        if (string.IsNullOrEmpty(pin))
        {
            throw new DomainException("PIN must not be empty");
        }

        _secondPasswordHash = hasher.Hash(pin);
    }

 /// <summary>Checks a submitted PIN against the stored PIN hash.</summary>
 /// <param name="pin">The submitted PIN.</param>
 /// <param name="hasher">The password hasher.</param>
 /// <returns><see langword="true"/> when a PIN is set and matches; otherwise <see langword="false"/>.</returns>
    public bool VerifySecondPassword(string pin, IPasswordHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        if (string.IsNullOrEmpty(_secondPasswordHash) || string.IsNullOrEmpty(pin))
        {
            return false;
        }

        return hasher.Verify(pin, _secondPasswordHash);
    }
}
