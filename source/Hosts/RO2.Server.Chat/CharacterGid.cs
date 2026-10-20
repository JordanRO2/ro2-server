namespace RO2.Server.Chat;

/// <summary>
/// The 64-bit character GID, rebuilt on the Chat server from a 32-bit <c>Character.Id</c>.
/// </summary>
/// <remarks>
/// The client keys its messenger maps by the full GID and matches them against the sender uid of chat lines.
/// A 32-bit id therefore never matches, so the GID must be rebuilt here.
/// The high dword is taken from the session's own <see cref="MessengerUid"/> rather than from a hardcoded value,
/// because the Zone issues one shared high dword for all characters. If the Zone ever issues per-character GIDs,
/// this must read the stored GID instead.
/// </remarks>
public static class CharacterGid
{
    private const ulong HighDwordMask = 0xFFFFFFFF00000000UL;

    /// <summary>
    /// Returns the GID of <paramref name="characterId"/>: the high dword of <paramref name="knownGid"/>, a GID issued by
    /// this server, combined with the character id.
    /// </summary>
    public static ulong Of(ulong knownGid, uint characterId) => (knownGid & HighDwordMask) | characterId;

    /// <summary>Returns the character id inside a GID: its low dword, the key used by every Chat store.</summary>
    public static uint CharacterIdOf(ulong gid) => (uint)gid;
}
