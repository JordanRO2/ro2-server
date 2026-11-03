using RO2.Guilds.Application;
using RO2.Parties.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Host adapter of <see cref="IGuildNameLookup"/> over the guild directory.</summary>
public sealed class GuildNameLookup(IGuildDirectory guilds) : IGuildNameLookup
{
    public string? GuildNameOf(uint characterId) => guilds.GetByCharId(characterId)?.Name;
}

/// <summary>Host adapter of the party <see cref="RO2.Parties.Application.IClock"/> over the host clock.</summary>
public sealed class PartyClock(IClock clock) : RO2.Parties.Application.IClock
{
    public DateTime UtcNow => clock.UtcNow;
}
