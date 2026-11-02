namespace RO2.Guilds.Application;

/// <summary>What follows a successful leave, once the 2572 is sent.</summary>
public enum LeaveAftermath
{
    /// <summary>The guild has no member left: it is disbanded and its emblem removed.</summary>
    Disband,

    /// <summary>Members remain: the master gets the contribution (2730) and they get the 2571 notice.</summary>
    NotifyRemaining,
}

/// <summary>
/// The decisions of a member leaving a guild (1432). A thin decide: the host removes the member under the guild's
/// gate, records the departure, sends the skill-bar refresh BEFORE the 2572, and runs the aftermath. The disband on
/// the last leave is a plain emptiness check, not <c>GuildDisbandRules.DisbandRefusal</c>, which is the master's
/// 1427 precondition and would refuse a leaver.
/// </summary>
public static class GuildLeaveUseCase
{
    /// <summary>Result 0: the member left.</summary>
    public const ushort ResultOk = 0;

    /// <summary>Result 1: the character is not in a guild.</summary>
    public const ushort ResultNoGuild = 1;

    /// <summary>Result 2: the guild master cannot leave the guild.</summary>
    public const ushort ResultMasterCannotLeave = 2;

    /// <summary>The refusal before the removal: no guild, then the master. 0 lets the removal run.</summary>
    public static ushort Check(bool inGuild, bool isMaster)
        => !inGuild ? ResultNoGuild : isMaster ? ResultMasterCannotLeave : ResultOk;

    /// <summary>The final code: a removal that found nobody (already gone) is "no guild".</summary>
    public static ushort Settle(bool removed) => removed ? ResultOk : ResultNoGuild;

    /// <summary>The last member leaving takes the guild with it.</summary>
    public static LeaveAftermath Aftermath(int remainingMembers)
        => remainingMembers == 0 ? LeaveAftermath.Disband : LeaveAftermath.NotifyRemaining;
}
