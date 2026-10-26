using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Creates a character (RMI 1415). The name check is separate from the create so the host can run it BEFORE the
/// appearance parse (a short payload can throw in the parse, and a rejected name must never reach it). The host
/// keeps the wire (appearance parse, JSON, the 2309 reply).
/// </summary>
public static class CreateCharacterFlow
{
    /// <summary>The result of the create step (the name was already accepted).</summary>
    public enum CreateOutcome
    {
        /// <summary>The repository did not create the character (duplicate name, slot limit, or an error); result 1.</summary>
        Failed,

        /// <summary>The character was created and its race saved; result 0.</summary>
        Created,
    }

    /// <summary>The outcome, the new id when created, and the repository error string when it failed.</summary>
    public readonly record struct CreateResult(CreateOutcome Outcome, uint Id, string? Error);

    /// <summary>Validates the name against format and the prohibited list; a non-None refusal is answered as result 1.</summary>
    public static (NameRefusal refusal, string? reason) CheckName(string? name, INameFilter filter)
        => NameRules.Check(name, filter.Rows);

    /// <summary>
    /// Seeds the class stats, creates the character, and (on success) stores its race with a second save. Returns
    /// <see cref="CreateOutcome.Failed"/> with the repository's error when the character could not be created.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="name">The already-validated name.</param>
    /// <param name="job">The class.</param>
    /// <param name="gender">The gender.</param>
    /// <param name="appearanceJson">The serialized appearance.</param>
    /// <param name="race">The race, stored after creation.</param>
    /// <param name="data">Game data, for the seed stats.</param>
    /// <param name="repo">The character repository.</param>
    public static async Task<CreateResult> CreateAsync(uint accountId, string name, byte job, byte gender,
        string appearanceJson, byte race, ICharacterGameData data, ICharacterRepository repo)
    {
        var seed = data.SeedStats(job);
        var (character, error) = await repo.CreateCharacterAsync(accountId, name, job, gender, appearanceJson, seed);
        if (character is null)
            return new CreateResult(CreateOutcome.Failed, 0, error);

        // A second, non-atomic write: if it throws the character already exists and no 2309 is sent (no catch here).
        character.Race = race;
        await repo.SaveCharacterAsync(character);
        return new CreateResult(CreateOutcome.Created, character.Id, null);
    }
}
