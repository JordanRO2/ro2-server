namespace RO2.Guilds.Application;

/// <summary>
/// The decisions of the master changing one member's title (1466). A thin decide: the host runs the rank write
/// under the guild's gate (<c>GuildMembershipRules.TrySetAuth</c>), persists, and sends the 2634 and the 2644.
/// </summary>
public static class GuildSetAuthUseCase
{
    /// <summary>Result 0: the title was set.</summary>
    public const ushort ResultOk = 0;

    /// <summary>Result 1: the requester is not in a guild.</summary>
    public const ushort ResultNoGuild = 1;

    /// <summary>Result 2: the requester is not the guild master.</summary>
    public const ushort ResultNotMaster = 2;

    /// <summary>Result 3: the member is unknown or the master, or the title is the master's or not in the table.</summary>
    public const ushort ResultRefused = 3;

    /// <summary>The refusal before the write: guild, then master. 0 lets the write run.</summary>
    public static ushort Check(bool inGuild, bool isMaster)
        => !inGuild ? ResultNoGuild : !isMaster ? ResultNotMaster : ResultOk;

    /// <summary>The final code from whether the write applied.</summary>
    public static ushort Settle(bool applied) => applied ? ResultOk : ResultRefused;
}

/// <summary>
/// The decisions of handing the master title over (1470). A thin decide: the host runs
/// <c>GuildMembershipRules.SetMaster</c> under the guild's gate, persists, and sends the 2635, the 2645 and the 2591
/// updates in that order.
/// </summary>
public static class GuildMasterHandoverUseCase
{
    /// <summary>Result 0: the title was handed over.</summary>
    public const ushort ResultOk = 0;

    /// <summary>Result 7: no guild was found.</summary>
    public const ushort ResultGuildNotFound = 7;

    /// <summary>Result 13: the target is not a guild member.</summary>
    public const ushort ResultTargetNotMember = 13;

    /// <summary>Result 17: the requester lacks the guild master right.</summary>
    public const ushort ResultNotMaster = 17;

    /// <summary>The refusal before the write: guild, then master. 0 lets the handover run.</summary>
    public static ushort Precheck(bool guildFound, bool requesterIsMaster)
        => !guildFound ? ResultGuildNotFound : requesterIsMaster ? ResultOk : ResultNotMaster;

    /// <summary>The final code from whether the target was a member and took the title.</summary>
    public static ushort Settle(bool handedOver) => handedOver ? ResultOk : ResultTargetNotMember;
}
