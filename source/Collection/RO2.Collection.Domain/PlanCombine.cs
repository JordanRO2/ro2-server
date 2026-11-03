using RO2.GameData.Tables;

namespace RO2.Collections.Domain;

/// <summary>
/// The pure needs math of the two combine handlers (1622 monster DNA, 1626 pet egg): how many of each source piece a number of combines takes,
/// with a repeated piece id grouped. Checking the bag, adding the result before consuming and every refusal stay in the Zone host.
/// </summary>
public static class PlanCombine
{
    /// <summary>Quantity of the catalyst or binding protein that <paramref name="times"/> combines take: the row count, at least 1, per combine.</summary>
    public static int ConsumableNeed(int times, byte perCombine) => times * Math.Max(1, (int)perCombine);

    /// <summary>The pieces that <paramref name="times"/> combines of a pet egg take, grouped by item id.</summary>
    public static List<(uint Id, int Need)> PieceNeeds(PetInfo pet, int times) =>
        Group(times,
            (pet.ItemID_Piece1, pet.Num_Piece1), (pet.ItemID_Piece2, pet.Num_Piece2),
            (pet.ItemID_Piece3, pet.Num_Piece3), (pet.ItemID_Piece4, pet.Num_Piece4));

    /// <summary>The pieces that <paramref name="times"/> combines of a monster DNA take, grouped by item id.</summary>
    public static List<(uint Id, int Need)> PieceNeeds(CollectMonster recipe, int times) =>
        Group(times,
            (recipe.ItemID_Piece1, recipe.Num_Piece1), (recipe.ItemID_Piece2, recipe.Num_Piece2),
            (recipe.ItemID_Piece3, recipe.Num_Piece3), (recipe.ItemID_Piece4, recipe.Num_Piece4));

    private static List<(uint Id, int Need)> Group(int times, params (uint Id, byte Num)[] pieces) =>
        pieces
            .Select(p => (p.Id, Need: times * (int)p.Num))
            .Where(p => p.Id != 0 && p.Need > 0)
            .GroupBy(p => p.Id)
            .Select(g => (Id: g.Key, Need: g.Sum(p => p.Need)))
            .ToList();
}
