namespace RO2.Application.Interfaces;

/// <summary>
/// Which zone channel each player is on, shared by the Login, Zone and Chat servers through the database:
/// the channel an account selected (Login 12005, Zone 1421) and the channel label of the character it
/// entered the world with (Zone 1417). The Chat server reads the labels for the location of online friends
/// in 23505.
/// </summary>
public interface IChannelPresenceStore
{
 /// <summary>Records the channel <paramref name="accountId"/> selected (12005) or moved to (1421).</summary>
    Task SetAccountChannelAsync(uint accountId, uint channelHandle, CancellationToken ct = default);

 /// <summary>The channel handle last recorded for <paramref name="accountId"/>, or null.</summary>
    Task<uint?> GetAccountChannelAsync(uint accountId, CancellationToken ct = default);

 /// <summary>Records the channel <paramref name="characterId"/> entered the world on (1417).</summary>
    Task SetCharacterLocationAsync(uint characterId, uint channelHandle, string location, CancellationToken ct = default);

 /// <summary>The recorded channel labels of <paramref name="characterIds"/>, by character id. A character
 /// with no record, or an empty label, is left out.</summary>
    Task<IReadOnlyDictionary<uint, string>> GetLocationsAsync(IReadOnlyCollection<uint> characterIds, CancellationToken ct = default);
}
