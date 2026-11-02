using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// The decisions of a guild invite request (1429) around <see cref="GuildInviteRules.Decide"/>. A thin decide: the host
/// looks the facts up, owns the token dictionary and its lifetime, and sends the 2565 and the 2566 in that order.
/// </summary>
public static class GuildInviteUseCase
{
    /// <summary>
    /// Whether the name must be looked up in the character repository: only for an invitee who is not online and
    /// only for an inviter whose request can still get past the right check (a refused inviter never reaches the name).
    /// </summary>
    public static bool NeedsNameLookup(bool inviteeOnline, bool inviterMayInvite) => !inviteeOnline && inviterMayInvite;

    /// <summary>
    /// The code to answer once the invite token was offered to the registry: a sent invite whose registration was
    /// refused (another invite landed in between) becomes "already invited"; every other code is kept.
    /// </summary>
    public static ushort Settle(ushort code, bool inviteRegistered)
        => code == GuildInviteRules.Sent && !inviteRegistered ? GuildInviteRules.AlreadyInvited : code;

    /// <summary>Whether the invitee gets the 2566 popup, sent after the inviter's 2565.</summary>
    public static bool ShouldNotifyInvitee(ushort code) => code == GuildInviteRules.Sent;
}
