using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// The fan-out of one RMI body to a guild's online members, behind a port so the guild flows do not reach sessions
/// or the broadcast service. Offline members are skipped; they receive their state at next login.
/// </summary>
public interface IGuildNotifier
{
    /// <summary>
    /// Sends <paramref name="body"/> under <paramref name="opcode"/> to every online member of
    /// <paramref name="guild"/>, optionally skipping <paramref name="exceptCharId"/>.
    /// </summary>
    Task BroadcastAsync(Guild guild, ushort opcode, byte[] body, uint? exceptCharId = null);
}
