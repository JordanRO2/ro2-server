using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Decides the answer to a guild invite request (1429): the 2565 result code, and the end of the rejoin
/// cooldown when it applies. Kept separate from the handler so the decision is a pure function of the
/// facts the handler looked up (<see cref="InviteFacts"/>).
/// </summary>
/// <remarks>
/// Checks run from the inviter to the invitee: guild, right, name, online, guild, cooldown, pending invite.
/// Code 60 is never produced, because the server does not place characters in the Colosseum.
/// </remarks>
internal static class GuildInviteRules
{
    /// <summary>Result code for a sent invite. The invitee receives the invite popup (2566).</summary>
    public const ushort Sent = 0;

    /// <summary>The invitee already belongs to a guild.</summary>
    public const ushort InviteeInGuild = 1;

    /// <summary>The invitee is not online.</summary>
    public const ushort InviteeOffline = 3;

    /// <summary>The inviter is not in a guild.</summary>
    public const ushort InviterNoGuild = 7;

    /// <summary>
    /// The inviter's guild title does not hold the invite permission (<see cref="GuildService.PermissionInvite"/>).
    /// </summary>
    public const ushort InviterNoRight = 8;

    /// <summary>The invitee already holds an unanswered invite younger than <see cref="InviteLifetime"/>.</summary>
    public const ushort AlreadyInvited = 11;

    /// <summary>
    /// No character (deleted ones excluded) carries the typed name. The lookup ignores case
    /// (see <see cref="NameExistsAsync"/>).
    /// </summary>
    public const ushort NoSuchName = 54;

    /// <summary>
    /// The invitee left a guild less than <see cref="RejoinCooldown"/> ago. The only code whose 16-byte tail
    /// carries a date: the end of the cooldown (see <c>GuildPackets.BuildAnsGuildInviteMember</c>).
    /// </summary>
    public const ushort RejoinCooldownActive = 73;

    /// <summary>Config_Def type 5: seconds to wait for an answer to a guild invite.</summary>
    public const ushort ConfigInviteLifetimeSeconds = 5;

    /// <summary>Config_Def type 85: hours a character must wait to rejoin a guild after leaving.</summary>
    public const ushort ConfigRejoinCooldownHours = 85;

    /// <summary>The facts the handler looked up for one invite request, in the order <see cref="Decide"/> reads them.</summary>
    /// <param name="InviterInGuild">The inviter is a member of a guild.</param>
    /// <param name="InviterMayInvite">The inviter's title holds the invite permission.</param>
    /// <param name="NameExists">A character, online or not, carries the typed name (any case).</param>
    /// <param name="InviteeOnline">That character is in the world now.</param>
    /// <param name="InviteeInGuild">The online invitee is already in a guild.</param>
    /// <param name="RejoinAtUtc">The end of the invitee's rejoin cooldown, when one is still running.</param>
    /// <param name="InviteePending">The invitee holds an invite younger than the lifetime.</param>
    internal readonly record struct InviteFacts(
        bool InviterInGuild,
        bool InviterMayInvite,
        bool NameExists,
        bool InviteeOnline,
        bool InviteeInGuild,
        DateTime? RejoinAtUtc,
        bool InviteePending);

    /// <summary>The 2565 code for <paramref name="f"/>, and the cooldown end that goes with code 73.</summary>
    internal static (ushort Code, DateTime? RejoinAtUtc) Decide(in InviteFacts f)
    {
        if (!f.InviterInGuild) return (InviterNoGuild, null);
        if (!f.InviterMayInvite) return (InviterNoRight, null);
        if (!f.NameExists) return (NoSuchName, null);
        if (!f.InviteeOnline) return (InviteeOffline, null);
        if (f.InviteeInGuild) return (InviteeInGuild, null);
        if (f.RejoinAtUtc is { } end) return (RejoinCooldownActive, end);
        if (f.InviteePending) return (AlreadyInvited, null);
        return (Sent, null);
    }

    /// <summary>
    /// How long a guild leave keeps the leaver from rejoining: Config_Def type 85, in hours. Zero (no cooldown)
    /// when the row is absent or not a number.
    /// </summary>
    /// <remarks>
    /// Only a leave request (1432) starts the cooldown; see <c>GuildDeparture.LeftUtc</c>.
    /// </remarks>
    internal static TimeSpan RejoinCooldown(IReadOnlyList<ConfigDef> config)
        => ConfigNumber(config, ConfigRejoinCooldownHours) is { } hours ? TimeSpan.FromHours(hours) : TimeSpan.Zero;

    /// <summary>
    /// How long an unanswered invite blocks another one: Config_Def type 5, in seconds (300 by default). Zero
    /// (never blocks) when the row is absent or not a number.
    /// </summary>
    internal static TimeSpan InviteLifetime(IReadOnlyList<ConfigDef> config)
        => ConfigNumber(config, ConfigInviteLifetimeSeconds) is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    /// <summary>The end of a rejoin cooldown that started at <paramref name="leftUtc"/>, or null when there
    /// is none or it is over at <paramref name="nowUtc"/>.</summary>
    internal static DateTime? RejoinAtUtc(DateTime? leftUtc, TimeSpan cooldown, DateTime nowUtc)
    {
        if (leftUtc is not { } left || cooldown <= TimeSpan.Zero) return null;
        var end = left + cooldown;
        return end > nowUtc ? end : null;
    }

    /// <summary>Whether <paramref name="invite"/> still blocks another invite at <paramref name="nowUtc"/>.</summary>
    internal static bool IsPending(GuildPendingInvite? invite, TimeSpan lifetime, DateTime nowUtc)
        => invite != null && nowUtc - invite.CreatedUtc < lifetime;

    /// <summary>
    /// Whether a character that is not deleted carries <paramref name="name"/>, compared case-insensitively.
    /// Used instead of the exact-match lookup, which would reject case variants of a name.
    /// </summary>
    internal static Task<bool> NameExistsAsync(ICharacterRepository characters, string name)
        => characters.NameExistsIgnoreCaseAsync(name);

    private static double? ConfigNumber(IReadOnlyList<ConfigDef> config, ushort type)
    {
        var row = config.FirstOrDefault(c => c.type == type);
        return row != null && double.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v : null;
    }
}
