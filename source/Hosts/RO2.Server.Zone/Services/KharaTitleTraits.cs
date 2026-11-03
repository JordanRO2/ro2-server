using System.Linq;
using RO2.GameData;
using RO2.Characters.Domain;
using RO2.Kharas.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Stats granted by a character's worn Khara titles, folded into the same pool as its gear's trait rows.
/// </summary>
/// <remarks>
/// A worn title's Option_1 refers to a TraitInfo row. Those rows apply as gear rows do: Trait_Kind 1 is flat, and
/// Trait_Kind 2 is a percentage (Value / <c>CharacterStats.PercentTraitDivisor</c>). A title whose row is missing grants nothing.
/// </remarks>
internal static class KharaTitleTraits
{
 /// <summary>The trait rows granted by a title. Empty for id 0 or an unknown title.</summary>
    internal static IReadOnlyList<(byte Kind, byte Type, float Value)> TraitsOf(GameDataStore store, uint titleId)
        => KharaTitleTraitRows.TraitsOf(store, titleId);

 /// <summary>
 /// Adds the trait rows of the titles a character wears to <paramref name="equip"/>. Returns <paramref name="equip"/>
 /// unchanged when no worn title grants a row. Null means no gear pool is known.
 /// </summary>
    internal static EquipStats? Fold(EquipStats? equip, GameDataStore store, uint characterId)
    {
        if (KharaTitleStore.PublishedFor(characterId) is not { } worn) return equip;
        return Fold(equip, store, worn.TitleA, worn.TitleB);
    }

 /// <summary>
 /// Same as <see cref="Fold(EquipStats?, GameDataStore, uint)"/>, for an explicit pair of titles.
 /// A title id of 0 means none.
 /// </summary>
    internal static EquipStats? Fold(EquipStats? equip, GameDataStore store, uint titleA, uint titleB)
    {
        var a = TraitsOf(store, titleA);
        var b = TraitsOf(store, titleB);
        if (a.Count == 0 && b.Count == 0) return equip;

        // Title A rows then title B rows, folded onto the gear pool by the same Domain accumulation the gear uses.
        var lines = a.Concat(b).Select(t => new TraitLine(t.Kind, t.Type, t.Value));
        return EquipStats.WithLines(equip, lines);
    }
}
