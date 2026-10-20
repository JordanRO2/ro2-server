using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Process-wide guild emblem registry, persisted to the <c>guild_emblems</c> collection, kept separate from
/// the guild document because that aggregate is written whole by ReplaceOne and would drop any field it does
/// not declare. Loaded once from storage and held in memory; the index reads are synchronous. Readers (2612,
/// 2559, 2591 and the PC-record guild block) and writers call <see cref="InitAsync"/> first, because nothing
/// else loads the registry after a restart; before the load <see cref="IndexOf"/> answers 0.
/// </summary>
public interface IGuildEmblemRepository
{
 /// <summary>Load every stored emblem exactly once. Safe to call on every message.</summary>
    Task InitAsync();

 /// <summary>The guild's registered emblem, or null when it has none. The all-zero GUID never has one.</summary>
    GuildEmblem? Get(ReadOnlySpan<byte> guid16);

 /// <summary>The guild's current emblem index, 0 when it has none — the one value 2612, 2614, 2559 and
 /// 2591 must agree on. Synchronous read of the loaded cache.</summary>
    ushort IndexOf(ReadOnlySpan<byte> guid16);

 /// <summary>Store a new image for the guild and advance its index by one. The index skips 0 on wrap,
 /// because 0 is the "no emblem" value.</summary>
    Task<GuildEmblem> SetAsync(byte[] guid16, byte[] image);

 /// <summary>Forget a disbanded guild's emblem.</summary>
    Task RemoveAsync(byte[] guid16);
}
