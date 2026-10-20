namespace RO2.Identity.Domain.Account;

/// <summary>
/// Abstraction over password hashing. The domain depends on this port rather than on a crypto library.
/// </summary>
/// <remarks>
/// The production implementation uses BCrypt with work factor 11, so existing stored hashes keep verifying.
/// </remarks>
public interface IPasswordHasher
{
 /// <summary>Hashes a plaintext secret (password or PIN) for storage.</summary>
 /// <param name="plaintext">The plaintext secret to hash.</param>
 /// <returns>The hash string to persist.</returns>
    string Hash(string plaintext);

 /// <summary>Verifies a plaintext secret against a stored hash.</summary>
 /// <param name="plaintext">The plaintext secret supplied by the client.</param>
 /// <param name="hash">The stored hash to verify against.</param>
 /// <returns><see langword="true"/> if the secret matches the hash; otherwise <see langword="false"/>.</returns>
    bool Verify(string plaintext, string hash);
}
