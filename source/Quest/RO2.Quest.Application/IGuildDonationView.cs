using RO2.Guilds.Domain;

namespace RO2.Quests.Application;

/// <summary>
/// A member's guild and the share of the quest-prize experience they donate to it, behind a port so the prize use
/// case does not reach the live guild registry singleton. The donation itself is credited through
/// <c>RO2.Guilds.Application.ICreditGuildExp</c>.
/// </summary>
public interface IGuildDonationView
{
    /// <summary>
    /// The character's guild and donation percent, or null when the character has no guild. A zero percent means
    /// the member donates nothing.
    /// </summary>
    GuildDonationTarget? DonationTargetOf(uint charId);
}

/// <summary>The guild a member donates to and their donation percent (the guild bribe rate).</summary>
public readonly record struct GuildDonationTarget(Guild Guild, byte Percent);
