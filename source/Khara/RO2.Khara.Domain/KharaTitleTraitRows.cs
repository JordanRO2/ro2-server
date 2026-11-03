using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Kharas.Domain;

/// <summary>
/// The TraitInfo rows a worn Khara title grants. A worn title's Option_1..3 refer to TraitInfo rows; a title whose row
/// is missing grants nothing. The host folds the rows into the gear pool (KharaTitleTraits.Fold).
/// </summary>
public static class KharaTitleTraitRows
{

    /// <summary>Cache of the TraitInfo rows each title grants, per loaded game data store.</summary>
    private static readonly ConditionalWeakTable<GameDataStore, Dictionary<uint, List<(byte Kind, byte Type, float Value)>>>
        Indexes = new();

    /// <summary>The trait rows granted by a title. Empty for id 0 or an unknown title.</summary>
    public static IReadOnlyList<(byte Kind, byte Type, float Value)> TraitsOf(GameDataStore store, uint titleId)
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


            var first = new Dictionary<uint, KHARATitleInfo>();
            var retail = new Dictionary<uint, KHARATitleInfo>();
            foreach (var row in s.KHARATitleInfo)
            {
                first.TryAdd(row.Title_ID, row);
                if (!retail.ContainsKey(row.Title_ID)
                    && KharaCatalog.IsRetail(row.NationEnable))
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
}
