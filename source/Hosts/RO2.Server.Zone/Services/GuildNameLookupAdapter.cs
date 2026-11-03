using RO2.Guilds.Domain;
using RO2.Rankings.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IGuildNameLookup"/> with the in-memory <see cref="GuildService"/>, initialized only when there is someone to look up.</summary>
internal sealed class GuildNameLookupAdapter(IGuildRepository guilds) : IGuildNameLookup
{
    public async Task<IReadOnlyDictionary<uint, string>> GuildNamesAsync(IReadOnlyCollection<uint> charIds, CancellationToken ct)
    {
        if (charIds.Count > 0)
            await GuildService.Instance.InitAsync(guilds);
        return charIds.ToDictionary(id => id, id => GuildService.Instance.GetByCharId(id)?.Name ?? string.Empty);
    }
}
