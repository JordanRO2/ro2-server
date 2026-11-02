using RO2.GameData.Tables;
using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>What an answer to a guild invite (1430) does.</summary>
public enum InviteAnswerOutcome
{
    /// <summary>The character joins the guild.</summary>
    Join,

    /// <summary>The answer is refused or a decline: the 2568 carries <see cref="InviteAnswerDecision.Code"/>.</summary>
    Refuse,
}

/// <summary>The decision for one answer: what happens, the 2568 code, and whether the inviter gets the 2567.</summary>
public readonly record struct InviteAnswerDecision(InviteAnswerOutcome Outcome, ushort Code, bool NotifyInviter);

/// <summary>
/// The decisions of the invitee's answer to a guild invite (1430). A thin decide: the host takes the invite from the
/// registry, matches the guild GUID and the 2566 token against the answer, and owns the join, the token echo and the
/// order of the sends.
/// </summary>
public static class GuildInviteAnswerUseCase
{
    /// <summary>Result 0: the answer was taken. An accept joins the guild; a decline is reported to the inviter.</summary>
    public const ushort AnswerTaken = 0;

    /// <summary>Result 1: the accepting character is already a member of a guild.</summary>
    public const ushort AlreadyInAGuild = 1;

    /// <summary>Result 12: no valid invite matches the answer, including an answer given after the invite has expired.</summary>
    public const ushort NoInvite = 12;

    /// <summary>
    /// The outcome of an answer. <paramref name="validInvite"/> is true when the invitee held an unexpired invite from a
    /// registered guild and the answer matched its GUID and token.
    /// </summary>
    public static InviteAnswerDecision Decide(bool accepted, bool validInvite, bool alreadyInGuild)
        => accepted && validInvite && !alreadyInGuild
            ? new InviteAnswerDecision(InviteAnswerOutcome.Join, AnswerTaken, NotifyInviter: true)
            : new InviteAnswerDecision(InviteAnswerOutcome.Refuse, RefusalCode(accepted, validInvite),
                NotifyInviter: !accepted && validInvite);

    /// <summary>
    /// The 2568 result code for an answer that did not join: <see cref="AnswerTaken"/> for a decline of a valid
    /// invite, <see cref="AlreadyInAGuild"/> for an accept by a guild member, and <see cref="NoInvite"/> when no valid
    /// invite matches.
    /// </summary>
    public static ushort RefusalCode(bool accepted, bool validInvite)
        => !validInvite ? NoInvite : accepted ? AlreadyInAGuild : AnswerTaken;

    /// <summary>
    /// How long an invite can be answered: the invite lifetime config row. Null (no limit) when the row is absent or
    /// not a positive number.
    /// </summary>
    public static TimeSpan? AnswerLifetime(IReadOnlyList<ConfigDef> config)
    {
        var row = config.FirstOrDefault(c => c.type == GuildInviteRules.ConfigInviteLifetimeSeconds);
        return row != null && double.TryParse(row.Value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>
    /// True when <paramref name="invite"/> is past <paramref name="lifetime"/> at <paramref name="nowUtc"/>. Never
    /// expires when <paramref name="lifetime"/> is null; expired at exactly its lifetime.
    /// </summary>
    public static bool IsExpired(GuildPendingInvite invite, TimeSpan? lifetime, DateTime nowUtc)
        => lifetime is { } l && nowUtc - invite.CreatedUtc >= l;
}
