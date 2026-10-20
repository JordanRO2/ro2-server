namespace RO2.Application.Interfaces;

/// <summary>
/// Per-character community-channel tab colours (the colours a player picks with 23019 and sees back in
/// the 23513 tab list at every later login). One owner keyed by its character id (the messenger uid);
/// a channel the player never recoloured is simply absent and keeps its default.
/// </summary>
public interface IChannelColorRepository
{
 /// <summary>Store <paramref name="argb"/> as <paramref name="ownerId"/>'s colour for channel
 /// <paramref name="channelIndex"/>, replacing any previous value for that channel.</summary>
    Task SetColorAsync(uint ownerId, ushort channelIndex, uint argb, CancellationToken ct = default);

 /// <summary>The owner's stored colours by channel index; empty when the player never changed one.</summary>
    Task<IReadOnlyDictionary<ushort, uint>> GetColorsAsync(uint ownerId, CancellationToken ct = default);
}
