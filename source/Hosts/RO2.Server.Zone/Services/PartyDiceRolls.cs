using RO2.Domain.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Session-independent rules of party loot dice: which corpse items go to a roll, the range of each roll choice,
/// and the winner. <see cref="PartyDiceService"/> applies them.
/// </summary>
public static class PartyDiceRolls
{
 /// <summary>Party loot mode in which items are rolled for.</summary>
    public const byte LootModeDice = 2;

 /// <summary>Roll choice for a roll in the high band (1001..2000).</summary>
    public const byte ChoiceHigh = 0;

 /// <summary>Roll choice for a roll in the low band (1..1000).</summary>
    public const byte ChoiceLow = 1;

 /// <summary>Roll choice for passing. No roll is made.</summary>
    public const byte ChoicePass = 2;

 /// <summary>Roll bands: 1..1000 for the low band and 1001..2000 for the high band.</summary>
    public const ushort LowMin = 1, LowMax = 1000, HighMin = 1001, HighMax = 2000;

 /// <summary>
 /// True when an item of <paramref name="grade"/> from a corpse in a party with this loot mode and quality goes to
 /// the dice rather than to the looter.
 /// </summary>
 /// <remarks>
 /// <para>
 /// In dice mode (2), an item goes to the dice when its grade is at or above the party's quality threshold. In
 /// other modes no item is rolled for.
 /// </para>
 /// <para>
 /// The rule is evaluated when the corpse is created, using the killer's party at that time.
 /// </para>
 /// </remarks>
    public static bool SendsToDice(byte lootMode, byte quality, byte grade)
        => lootMode == LootModeDice && quality != 0 && grade >= quality;

 /// <summary>
 /// Applies <see cref="SendsToDice(byte, byte, byte)"/> to <paramref name="party"/> and the item's grade. Returns false
 /// when there is no party or no ItemInfo row.
 /// </summary>
    public static bool SendsToDice(PartyState? party, ItemData? item)
        => party != null && item != null && SendsToDice(party.ItemRule, party.ItemQuality, item.Grade);

 /// <summary>
 /// Makes the roll for a choice. Returns false for a pass or an unknown choice. The result is a random value in the
 /// band that the choice selects.
 /// </summary>
    public static bool TryRoll(byte choice, Random rng, out ushort roll)
    {
        switch (choice)
        {
            case ChoiceHigh:
                roll = (ushort)rng.Next(HighMin, HighMax + 1);
                return true;
            case ChoiceLow:
                roll = (ushort)rng.Next(LowMin, LowMax + 1);
                return true;
            default:
                roll = 0;
                return false;
        }
    }

 /// <summary>
 /// Returns the index of the winning roll: the highest roll, and among equal rolls the one answered first. Returns
 /// -1 when there are no rolls.
 /// </summary>
    public static int PickWinner(IReadOnlyList<(ushort Roll, long AnswerOrder)> rolls)
    {
        int best = -1;
        for (int i = 0; i < rolls.Count; i++)
        {
            if (best < 0 || rolls[i].Roll > rolls[best].Roll
                || (rolls[i].Roll == rolls[best].Roll && rolls[i].AnswerOrder < rolls[best].AnswerOrder))
                best = i;
        }
        return best;
    }
}
