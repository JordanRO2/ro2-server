using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Allocates bonus stat points (RMI 1493 ReqUseBonusStatPoint): server-authoritative and all-or-nothing. The
/// request is priced against the character's unspent pool and, only if it all fits, written and SAVED first; the
/// host then mirrors it to the runtime and re-pushes the sheet. A failed save refuses the request with nothing
/// changed. The pure pricing lives in <see cref="BonusStatRules"/>; the wire and the sends stay in the host.
/// </summary>
public static class BonusStatAllocationFlow
{
    /// <summary>
    /// The outcome of an allocation: the priced <paramref name="Result"/>, whether the character could be priced
    /// at all (<paramref name="Evaluated"/>), whether it was persisted and should now be mirrored
    /// (<paramref name="Applied"/>), the cost, the unspent pool before it, and any swallowed save error.
    /// </summary>
    public readonly record struct BonusAllocationResult(
        BonusStatOutcome Result, bool Evaluated, bool Applied, int TotalCost, int Unspent, Exception? SaveError);

    /// <summary>
    /// Mirrors the live level onto the character, prices the request, and on acceptance writes and saves the new
    /// allocation (rolling back on a save failure). Returns what happened; the host maps the result to the 2680
    /// code and, when Applied, mirrors the primaries and re-pushes the 2595/2498 sheet.
    /// </summary>
    /// <param name="character">The player's in-memory character, mutated in place on acceptance.</param>
    /// <param name="liveLevel">The runtime level; mirrored onto the snapshot when non-zero and different.</param>
    /// <param name="request">The parsed allocation vector.</param>
    /// <param name="data">Game data, for the class base primaries and the per-point cost.</param>
    /// <param name="repo">The character repository, for the persist.</param>
    public static async Task<BonusAllocationResult> ApplyAsync(Character? character, byte liveLevel,
        BonusRequest request, ICharacterGameData data, ICharacterRepository repo)
    {
        // The snapshot's Level is the login one: a level-up moves only the runtime level, so the points earned
        // this session (BonusStatPointsEarned below) and the 2498 sheet would otherwise read the old level.
        if (character is not null && liveLevel != 0 && character.Level != liveLevel)
            character.Level = liveLevel;

        if (character is null || data.ClassBasePrimaries(character.Job) is not { } cb)
            return new BonusAllocationResult(BonusStatOutcome.BadState, Evaluated: false, Applied: false, 0, 0, null);

        int unspent = CharacterStats.BonusStatPointsEarned(character.Level) - BonusStatRules.BonusOf(character).Sum();
        var plan = BonusStatRules.Price(request, BonusStatRules.BonusOf(character), unspent,
            bonusNow => data.BonusStatCost(bonusNow));

        if (plan.Outcome == BonusStatOutcome.Ok && plan.TotalCost > 0)
        {
            // Persist first, then mirror (the ChangeProJobID order): the allocation is written and saved before
            // the runtime copy or any packet moves. A failed save puts the character back and refuses the request,
            // so the client can never be ahead of the database.
            var saveError = await PersistAsync(character, cb, plan.Bonus, repo.SaveCharacterAsync);
            return saveError is not null
                ? new BonusAllocationResult(BonusStatOutcome.BadState, true, false, plan.TotalCost, unspent, saveError)
                : new BonusAllocationResult(BonusStatOutcome.Ok, true, true, plan.TotalCost, unspent, null);
        }

        return new BonusAllocationResult(plan.Outcome, true, false, plan.TotalCost, unspent, null);
    }

    /// <summary>
    /// Writes the priced allocation (bonuses and effective primaries = class base + bonus) onto
    /// <paramref name="c"/> and saves it before anything else moves. Returns null when the save went through; when
    /// it throws, the character is put back exactly as it was and the exception is returned.
    /// </summary>
    /// <param name="c">The character to write and save.</param>
    /// <param name="cb">The class base primaries to add the bonuses to.</param>
    /// <param name="bonus">The six new bonus values (STR, AGI, INT, WIS, VIT, LUK).</param>
    /// <param name="save">The save step (the character repository's save).</param>
    public static async Task<Exception?> PersistAsync(Character c,
        (int str, int agi, int intel, int wis, int vit, int luk) cb, int[] bonus, Func<Character, Task> save)
    {
        var oldBonus = BonusStatRules.BonusOf(c);
        var oldPrimaries = (c.Str, c.Agi, c.Int, c.Wis, c.Vit, c.Luk);
        c.BonusStr = bonus[0]; c.BonusAgi = bonus[1]; c.BonusInt = bonus[2];
        c.BonusWis = bonus[3]; c.BonusVit = bonus[4]; c.BonusLuk = bonus[5];
        c.Str = cb.str + c.BonusStr; c.Agi = cb.agi + c.BonusAgi;
        c.Int = cb.intel + c.BonusInt; c.Wis = cb.wis + c.BonusWis;
        c.Vit = cb.vit + c.BonusVit; c.Luk = cb.luk + c.BonusLuk;
        try
        {
            await save(c);
            return null;
        }
        catch (Exception ex)
        {
            c.BonusStr = oldBonus[0]; c.BonusAgi = oldBonus[1]; c.BonusInt = oldBonus[2];
            c.BonusWis = oldBonus[3]; c.BonusVit = oldBonus[4]; c.BonusLuk = oldBonus[5];
            (c.Str, c.Agi, c.Int, c.Wis, c.Vit, c.Luk) = oldPrimaries;
            return ex;
        }
    }
}
