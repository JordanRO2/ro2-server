using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Deletes a character (RMI 1416): the ownership gate, the guild-master gate, and the delete. The host keeps the
/// wire (uid read, 2308 builder, sends) and logs with the name this returns.
/// </summary>
public static class DeleteCharacterFlow
{
    /// <summary>The result of a delete attempt.</summary>
    public enum DeleteOutcome
    {
        /// <summary>No such character, or it belongs to another account: refuse (15304).</summary>
        NotLive,

        /// <summary>The character leads a guild: refuse (15314).</summary>
        GuildMaster,

        /// <summary>The delete lost a race with a concurrent one: answer nothing.</summary>
        RaceLost,

        /// <summary>Deleted: answer 0.</summary>
        Deleted,
    }

    /// <summary>The outcome and the character's name (for the host log), or null when there was no live character.</summary>
    public readonly record struct DeleteResult(DeleteOutcome Outcome, string? Name);

    /// <summary>
    /// Reads the character, checks ownership and guild leadership (the guild service is initialised as a side
    /// effect of the guild-master check, as before), and deletes it. A lost delete race returns
    /// <see cref="DeleteOutcome.RaceLost"/> so the host sends nothing.
    /// </summary>
    /// <param name="accountId">The requesting account.</param>
    /// <param name="characterId">The character id (the low 32 bits of the slot uid).</param>
    /// <param name="repo">The character repository.</param>
    /// <param name="guilds">The guild-membership view.</param>
    public static async Task<DeleteResult> DeleteAsync(uint accountId, uint characterId,
        ICharacterRepository repo, IGuildMembershipView guilds)
    {
        var character = await repo.GetByIdAsync(characterId);
        if (character is null || character.AccountId != accountId)
            return new DeleteResult(DeleteOutcome.NotLive, character?.Name);

        if (await guilds.IsGuildMasterAsync(characterId))
            return new DeleteResult(DeleteOutcome.GuildMaster, character.Name);

        if (!await repo.DeleteCharacterAsync(characterId, accountId))
            return new DeleteResult(DeleteOutcome.RaceLost, character.Name);

        return new DeleteResult(DeleteOutcome.Deleted, character.Name);
    }
}
