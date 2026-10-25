using System.Linq;
using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.Characters.Domain;

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
 /// <summary>NationEnable bit that marks the Khara title row used by the service.</summary>
    private const int RetailNationBit = 4;

 /// <summary>Cache of the TraitInfo rows each title grants, per loaded game data store.</summary>
    private static readonly ConditionalWeakTable<GameDataStore, Dictionary<uint, List<(byte Kind, byte Type, float Value)>>>
        Indexes = new();

 /// <summary>The trait rows granted by a title. Empty for id 0 or an unknown title.</summary>
    internal static IReadOnlyList<(byte Kind, byte Type, float Value)> TraitsOf(GameDataStore store, uint titleId)
        => titleId != 0 && IndexOf(store).TryGetValue(titleId, out var rows)
            ? rows
            : Array.Empty<(byte, byte, float)>();

    private static Dictionary<uint, List<(byte Kind, byte Type, float Value)>> IndexOf(GameDataStore store) =>
        Indexes.GetValue(store, static s =>
        {
            var traits = new Dictionary<uint, List<(byte, byte, float)>>();
            foreach (var t in s.TraitInfo)
            {
                if (!traits.TryGetValue(t.ItemID, out var l))
                    traits[t.ItemID] = l = new List<(byte, byte, float)>(2);
                l.Add((t.Trait_Kind, t.Trait_Type, t.Value));
            }

            
            var first = new Dictionary<uint, RO2.GameData.Tables.KHARATitleInfo>();
            var retail = new Dictionary<uint, RO2.GameData.Tables.KHARATitleInfo>();
            foreach (var row in s.KHARATitleInfo)
            {
                first.TryAdd(row.Title_ID, row);
                if (!retail.ContainsKey(row.Title_ID)
                    && (row.NationEnable == 0 || (row.NationEnable & RetailNationBit) != 0))
                    retail[row.Title_ID] = row;
            }

            var byTitle = new Dictionary<uint, List<(byte, byte, float)>>();
            foreach (var (id, firstRow) in first)
            {
                var row = retail.TryGetValue(id, out var r) ? r : firstRow;
                var list = new List<(byte, byte, float)>();
                foreach (var option in new uint[] { row.Option_1, row.Option_2, row.Option_3 })
                    if (option != 0 && traits.TryGetValue(option, out var rows))
                        list.AddRange(rows);
                if (list.Count > 0) byTitle[id] = list;
            }
            return byTitle;
        });

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
