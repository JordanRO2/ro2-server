using RO2.Inventory.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Decides a review-another-player request (RMI 1696): whether the target allows reviews and, if so, its worn
/// gear. The host builds the 3033 reply from what this returns.
/// </summary>
public static class ReviewPcFlow
{
    /// <summary>Whether the review is allowed and, when it is, the target's equipment.</summary>
    public readonly record struct ReviewDecision(bool Allowed, IReadOnlyList<InventoryItem> Worn);

    /// <summary>
    /// Returns <see cref="ReviewDecision.Allowed"/> false (and no gear) when the target refuses reviews; otherwise
    /// true with its equipment loaded.
    /// </summary>
    /// <param name="targetCharacterId">The character being reviewed.</param>
    /// <param name="view">The target's online settings.</param>
    /// <param name="inventory">The inventory repository, for the worn gear.</param>
    public static async Task<ReviewDecision> DecideAsync(uint targetCharacterId, IOnlineCharacterView view,
        IInventoryRepository inventory)
    {
        if (!view.IsReviewAllowed(targetCharacterId))
            return new ReviewDecision(false, []);

        var worn = await inventory.GetEquipmentAsync(targetCharacterId);
        return new ReviewDecision(true, worn);
    }
}
