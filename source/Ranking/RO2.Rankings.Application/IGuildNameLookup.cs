namespace RO2.Rankings.Application;

/// <summary>Outbound port: the guild a ranked character belongs to. Owned by the Guilds side, implemented by the host.</summary>
public interface IGuildNameLookup
{
 /// <summary>The guild name of each character in <paramref name="charIds"/>; empty string when it has none.</summary>
    Task<IReadOnlyDictionary<uint, string>> GuildNamesAsync(IReadOnlyCollection<uint> charIds, CancellationToken ct);
}
