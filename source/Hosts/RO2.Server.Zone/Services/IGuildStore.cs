namespace RO2.Server.Zone.Services;

/// <summary>
/// Persistence seam for the <see cref="Guild"/> aggregate: loads all guilds at startup, saves a whole guild
/// document, and deletes it. The guild service depends on this interface, not on a concrete repository.
/// </summary>
/// <remarks>
/// Guild id allocation is not part of this interface; it is in-memory process state held by the guild service.
/// </remarks>
public interface IGuildStore
{
 /// <summary>Loads every stored guild, once at startup.</summary>
    Task<IReadOnlyList<Guild>> LoadAllAsync(CancellationToken ct = default);

 /// <summary>
 /// Writes the whole guild (create or replace) as one document, so a crash never leaves a half-written guild.
 /// Throws on a write failure.
 /// </summary>
    Task SaveAsync(Guild guild, CancellationToken ct = default);

 /// <summary>Deletes a disbanded guild's document, with its vault and roster.</summary>
    Task DeleteAsync(uint guildId, CancellationToken ct = default);
}
