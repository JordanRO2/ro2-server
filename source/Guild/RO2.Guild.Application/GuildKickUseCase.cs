namespace RO2.Guilds.Application;

/// <summary>
/// The decisions of a guild kick (1431). A thin decide: the host finds the target and removes it under the guild's
/// gate, hands the contribution to the master BEFORE the 2571 broadcast, then runs the 2570 notice or the offline
/// mark. The 2569 code is decided here, in the order the checks run.
/// </summary>
public static class GuildKickUseCase
{
    /// <summary>Result 0: the member was removed.</summary>
    public const ushort ResultOk = 0;

    /// <summary>Result 7: the kicker is not in a guild.</summary>
    public const ushort ResultNoGuild = 7;

    /// <summary>Result 8: the kicker lacks the member-removal right, or the target is the guild master.</summary>
    public const ushort ResultNoRight = 8;

    /// <summary>Result 24: the kicker targeted themselves.</summary>
    public const ushort ResultSelf = 24;

    /// <summary>Result 10: no member of the kicker's guild has the given name.</summary>
    public const ushort ResultNoSuchMember = 10;

    /// <summary>Reason code 2 in the 2571 notice: the member was kicked. Reason 3 means the member left.</summary>
    public const byte RemoveReasonKicked = 2;

    /// <summary>The refusal before the target is looked up: guild, then right, then self-kick. 0 lets the lookup run.</summary>
    public static ushort CheckKicker(bool inGuild, bool mayKick, bool targetIsSelf)
        => !inGuild ? ResultNoGuild : !mayKick ? ResultNoRight : targetIsSelf ? ResultSelf : ResultOk;

    /// <summary>The refusal once the target lookup ran: an unknown name, then the master (never kickable). 0 lets the removal run.</summary>
    public static ushort CheckTarget(bool targetFound, bool targetIsMaster)
        => !targetFound ? ResultNoSuchMember : targetIsMaster ? ResultNoRight : ResultOk;

    /// <summary>The final code: a removal that found nobody (the member left in between) is "no such member".</summary>
    public static ushort Settle(bool removed) => removed ? ResultOk : ResultNoSuchMember;
}
