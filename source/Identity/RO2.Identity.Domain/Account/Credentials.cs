using RO2.BuildingBlocks.Domain;

namespace RO2.Identity.Domain.Account;

/// <summary>
/// Value object holding the login key and the stored password hash. The plaintext password is never stored.
/// </summary>
/// <remarks>
/// The username must be non-empty and at most <see cref="MaxUsernameLength"/> characters; the hash must be non-empty.
/// Username uniqueness is enforced by the application layer.
/// </remarks>
public sealed class Credentials : ValueObject
{
 /// <summary>Maximum username length in characters.</summary>
    public const int MaxUsernameLength = 36;

 /// <summary>Maximum password length in UTF-8 bytes. Longer passwords are refused, because BCrypt ignores bytes past 72.</summary>
    public const int MaxPasswordBytes = 72;

 /// <summary>True when <paramref name="plaintext"/> fits BCrypt's 72-byte input in UTF-8.</summary>
    public static bool IsPasswordLengthAcceptable(string plaintext)
        => System.Text.Encoding.UTF8.GetByteCount(plaintext) <= MaxPasswordBytes;

 /// <summary>The login key.</summary>
    public string Username { get; }

 /// <summary>The stored BCrypt password hash.</summary>
    public string PasswordHash { get; }

 /// <summary>Constructs a validated <see cref="Credentials"/>.</summary>
 /// <param name="username">The login key.</param>
 /// <param name="passwordHash">The stored password hash.</param>
 /// <exception cref="DomainException">Thrown when the username is blank or too long, or the hash is blank.</exception>
    public Credentials(string username, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new DomainException("username must not be empty");
        }

        if (username.Length > MaxUsernameLength)
        {
            throw new DomainException($"username length {username.Length} exceeds {MaxUsernameLength}");
        }

        if (string.IsNullOrEmpty(passwordHash))
        {
            throw new DomainException("password hash must not be empty");
        }

        Username = username;
        PasswordHash = passwordHash;
    }

 /// <summary>Returns a copy with a different stored password hash (e.g. after a password change).</summary>
 /// <param name="newHash">The new stored password hash.</param>
 /// <returns>A new <see cref="Credentials"/> with the same username and the supplied hash.</returns>
    public Credentials WithPasswordHash(string newHash) => new(Username, newHash);

 /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Username;
        yield return PasswordHash;
    }
}
