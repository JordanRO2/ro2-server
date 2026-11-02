namespace RO2.Guilds.Application;

/// <summary>What the name gate decides for a rename, taken under the host's name gate.</summary>
public enum RenameDecision
{
    /// <summary>Take the new name, reserve the old one and run the payment.</summary>
    Rename,

    /// <summary>The guild was disbanded before the call.</summary>
    NotRegistered,

    /// <summary>A registered or reserved guild, the renamed one included, already has the name.</summary>
    NameTaken,
}

/// <summary>
/// The decisions of a guild rename. A thin decide: the host keeps the name gate, the held-name reservation, the
/// payment, the restore on a failed payment, persistence and the wire.
/// </summary>
public static class GuildRenameUseCase
{
    /// <summary>The name-gate verdict: registration is checked before the name.</summary>
    public static RenameDecision Decide(bool guildRegistered, bool nameTaken)
        => !guildRegistered ? RenameDecision.NotRegistered
         : nameTaken ? RenameDecision.NameTaken
         : RenameDecision.Rename;

    /// <summary>After a failed payment the old name comes back only if the guild still carries the attempted one.</summary>
    public static bool ShouldRestoreName(string currentName, string attemptedName) => currentName == attemptedName;
}
