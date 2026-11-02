using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// The guild-side write of a notice change: sets the notice under the guild's gate and writes the whole guild document
/// once. The live registry implements it in the host.
/// </summary>
public interface IGuildNoticeLedger
{
    /// <summary>Sets the guild's notice to <paramref name="notice"/> and writes the guild.</summary>
    Task SetNoticeAsync(Guild guild, string notice);
}
