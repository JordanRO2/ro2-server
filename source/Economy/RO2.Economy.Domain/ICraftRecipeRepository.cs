namespace RO2.Economy.Domain;

/// <summary>
/// Persistence for a character's learned crafting recipes (the Craft_ItemList ids learned with 1348
/// ReqCraftListLearn). The profession's Craft_Level-1 recipes are not stored — every character of that
/// profession has them — only the explicitly learned ones.
/// </summary>
public interface ICraftRecipeRepository
{
 /// <summary>The character's learned recipe ids (empty when it learned none).</summary>
    Task<IReadOnlyList<ushort>> GetAsync(uint characterId, CancellationToken ct = default);

 /// <summary>Stores <paramref name="recipeId"/> as learned. False when it already was (a repeated or
 /// concurrent 1348 for the same recipe), in which case nothing changed.</summary>
    Task<bool> AddAsync(uint characterId, ushort recipeId, CancellationToken ct = default);
}
