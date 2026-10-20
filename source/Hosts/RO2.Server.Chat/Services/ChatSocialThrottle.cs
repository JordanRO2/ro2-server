using RO2.Network.Throttling;

namespace RO2.Server.Chat.Services;

/// <summary>
/// Per-character rate limit for the messenger social requests (friend and block add, remove and list).
/// </summary>
/// <remarks>
/// Each of these requests triggers a database lookup or a list read or write, so the limiter bounds the
/// database work a single connection can drive. The budget is shared across all six social opcodes for a
/// character, and is set well above normal social-action rates. A refused request is dropped without a
/// database call and without a reply. Time is a monotonic-clock millisecond value, which keeps the limiter
/// deterministic under test. Registered as a singleton; its state must outlive the per-request scope.
/// </remarks>
public sealed class ChatSocialThrottle
{
 /// <summary>Social requests admitted per character inside one <see cref="WindowMs"/> window.</summary>
    public const int Limit = 20;

 /// <summary>The window length in milliseconds.</summary>
    public const long WindowMs = 10_000;

    private readonly SlidingWindowRateLimiter<uint> _perCharacter = new(Limit, WindowMs);

 /// <summary>Characters currently tracked (diagnostics and tests).</summary>
    public int TrackedCharacters => _perCharacter.TrackedKeys;

 /// <summary>
 /// True, and the request is counted, when <paramref name="characterId"/> may run another social
 /// request now; false when it has used its <see cref="Limit"/> requests inside the last
 /// <see cref="WindowMs"/> ms.
 /// </summary>
    public bool TryAct(uint characterId) => _perCharacter.TryAcquire(characterId);

 /// <summary><see cref="TryAct(uint)"/> against an explicit monotonic-clock time, for deterministic tests.</summary>
    public bool TryAct(uint characterId, long nowMs) => _perCharacter.TryAcquire(characterId, nowMs);
}
