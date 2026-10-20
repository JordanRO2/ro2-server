using RO2.Identity.Domain.Account;

namespace RO2.Identity.Infrastructure;

/// <summary>
/// BCrypt implementation of <see cref="IPasswordHasher"/>.
/// </summary>
/// <remarks>
/// The work factor must remain 11 so that stored hashes keep verifying. The same hasher is used for the account
/// password and the second password (PIN).
/// </remarks>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
 /// <summary>The BCrypt work factor. Must remain 11 to verify existing hashes.</summary>
    public const int WorkFactor = 11;

 /// <inheritdoc />
 /// <exception cref="ArgumentException">The plaintext exceeds <see cref="Credentials.MaxPasswordBytes"/> UTF-8 bytes.</exception>
    public string Hash(string plaintext)
    {
        if (!Credentials.IsPasswordLengthAcceptable(plaintext))
            throw new ArgumentException($"password exceeds {Credentials.MaxPasswordBytes} UTF-8 bytes (BCrypt truncates)", nameof(plaintext));
        return BCrypt.Net.BCrypt.HashPassword(plaintext, workFactor: WorkFactor);
    }

 /// <inheritdoc />
 /// <remarks>A plaintext longer than <see cref="Credentials.MaxPasswordBytes"/> bytes never verifies.</remarks>
    public bool Verify(string plaintext, string hash)
        => Credentials.IsPasswordLengthAcceptable(plaintext) && BCrypt.Net.BCrypt.Verify(plaintext, hash);
}
