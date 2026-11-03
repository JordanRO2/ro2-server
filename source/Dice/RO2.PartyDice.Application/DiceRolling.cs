using RO2.PartyDice.Domain;

namespace RO2.PartyDice.Application;

/// <summary>Where the dice are actually rolled. The Domain takes the rolled value and holds no random source.</summary>
public static class DiceRolling
{
    /// <summary>
    /// Rolls a participant's choice (<c>Random.Shared</c> unless <paramref name="rng"/> is given) and records the
    /// answer in <paramref name="round"/>. An unknown choice is ignored and records nothing, so the participant can
    /// still answer.
    /// </summary>
    public static IReadOnlyList<DiceEffect> Answer(DiceRound round, uint sessionId, ulong gid, byte choice, Random? rng = null)
    {
        var rolled = PartyDiceRolls.TryRoll(choice, rng ?? Random.Shared, out var roll);
        if (!rolled && choice != PartyDiceRolls.ChoicePass) return [];
        return round.Answer(sessionId, gid, rolled, roll);
    }

    /// <summary>
    /// Rolls the party-chat dice command's number in [<paramref name="min"/>, <paramref name="max"/>]. False, with no
    /// roll, for a range that has no defined answer.
    /// </summary>
    public static bool TryRollChat(ushort min, ushort max, out ushort roll, Random? rng = null)
    {
        roll = 0;
        if (!PartyDiceRolls.IsChatRangeAnswerable(min, max)) return false;
        roll = (ushort)(rng ?? Random.Shared).Next(min, max + 1);
        return true;
    }
}
