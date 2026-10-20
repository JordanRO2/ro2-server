using System.Security.Cryptography;

namespace RO2.Application.Interfaces;

/// <summary>A consumed one-shot token: the identity the issuer bound to it.</summary>
public sealed record OneShotToken(
    string Value, string Purpose, uint AccountId, ulong CharacterUid, DateTime IssuedUtc, DateTime ExpiryUtc);

/// <summary>Provider-neutral constants and the token generator for <see cref="IOneShotTokenStore"/>.</summary>
public static class OneShotTokens
{
 /// <summary>Purpose of a token issued for the Chat/messenger hand-off (1516 -> 23001).</summary>
    public const string PurposeMessenger = "messenger";

 /// <summary>Purpose of a token issued for the zone hand-off (12005 -> zone auth).</summary>
    public const string PurposeZone = "zone";

 /// <summary>Random token width in bytes (128 bits), shown as 32 hex characters.</summary>
    public const int RandomTokenBytes = 16;

 /// <summary>A fresh unguessable token: <see cref="RandomTokenBytes"/> from the system CSPRNG, as hex.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(RandomTokenBytes));
}

/// <summary>
/// SP0-TOKEN: a shared, cross-process, one-shot token store. One server issues
/// <c>{token, purpose, accountId, charUid, expiry}</c> when it answers a hand-off request (Zone on 1516 for
/// the messenger) and the server on the other end consumes it exactly once (Chat on 23001), so a second
/// consume of the same token, from any process, returns null. Expiry is enforced in the consume itself
/// (<c>Expiry &gt; now</c>), so an expired token is never redeemable; a provider's index/TTL is only the
/// garbage collector. A wrong purpose or account does not burn the token (both are part of the match).
/// </summary>
public interface IOneShotTokenStore
{
 /// <summary>
 /// Store a token bound to (<paramref name="accountId"/>, <paramref name="characterUid"/>) for
 /// <paramref name="purpose"/>, valid for <paramref name="lifetime"/>. Returns false if that token already
 /// exists (a caller-supplied collision; <see cref="OneShotTokens.NewToken"/> never collides in practice).
 /// </summary>
    Task<bool> IssueAsync(string token, string purpose, uint accountId, ulong characterUid, TimeSpan lifetime, CancellationToken ct = default);

 /// <summary>Issue a fresh random token and return it.</summary>
    Task<string> IssueNewAsync(string purpose, uint accountId, ulong characterUid, TimeSpan lifetime, CancellationToken ct = default);

 /// <summary>
 /// Redeem <paramref name="token"/> exactly once: atomically find-and-delete the unexpired record issued for
 /// <paramref name="purpose"/> (and, when given, to <paramref name="accountId"/>). Returns the bound
 /// identity, or null if there is none, it expired, it was already consumed, or purpose/account differ.
 /// </summary>
    Task<OneShotToken?> ConsumeAsync(string token, string purpose, uint? accountId = null, CancellationToken ct = default);

 /// <summary>
 /// Create any provider index the store needs (e.g. the Mongo TTL index), off the startup path. Idempotent;
 /// returns false if the database refused, so a down database cannot crash boot (the consume filter keeps
 /// the store correct without it).
 /// </summary>
    Task<bool> EnsureIndexesAsync(CancellationToken ct = default);
}
