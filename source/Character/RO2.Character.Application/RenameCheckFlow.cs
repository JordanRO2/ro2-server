using RO2.Characters.Domain;
using RO2.Inventory.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// The shared rename availability check (RMI 1564 can-rename and the 1563 rename): the name rules, then the
/// scroll in the bag, then whether another live character already holds the name. The name rules run first, so a
/// duplicate request is refused with the name code whatever state the scroll is in. The host maps the refusal to
/// its wire code and keeps the parse and the reply.
/// </summary>
public static class RenameCheckFlow
{
    /// <summary>The refusal (None when the name may be used), the scroll to consume on success, and a log reason.</summary>
    public readonly record struct RenameCheckResult(RenameNameRefusal Refusal, InventoryItem? Scroll, string? Reason);

    /// <summary>
    /// Checks <paramref name="newName"/> for the character, requiring a valid name-change scroll in the bag and a
    /// free name. Returns the scroll with <see cref="RenameNameRefusal.None"/> on success, or the refusal with no
    /// scroll. The check-then-act race against the rename itself is intentional and preserved by the caller.
    /// </summary>
    /// <param name="currentName">The character's current name (a request to keep it is refused as TakenOrShort).</param>
    /// <param name="characterId">The requesting character (the scroll must belong to it).</param>
    /// <param name="scrollUid">The rename scroll's uid; 0 means none.</param>
    /// <param name="newName">The requested name.</param>
    /// <param name="nameFilter">The prohibited-name rows.</param>
    /// <param name="data">Game data, to confirm the scroll's item type.</param>
    /// <param name="inventory">The inventory repository.</param>
    /// <param name="repo">The character repository, for the uniqueness check.</param>
    public static async Task<RenameCheckResult> CheckAsync(string currentName, uint characterId, ulong scrollUid,
        string newName, INameFilter nameFilter, ICharacterGameData data, IInventoryRepository inventory,
        ICharacterRepository repo)
    {
        var (nameRefusal, nameReason) = NameRules.CheckRename(newName, currentName, nameFilter.Rows);
        if (nameRefusal != RenameNameRefusal.None)
            return new RenameCheckResult(nameRefusal, null, nameReason);

        var scroll = scrollUid == 0 ? null : await inventory.GetByIdAsync(scrollUid);
        if (scroll is null || scroll.CharacterId != characterId || scroll.StorageType != 0 || scroll.Count <= 0)
            return new RenameCheckResult(RenameNameRefusal.TakenOrShort, null, "no such scroll in the bag");
        if (!data.IsNameChangeScroll(scroll.ItemId))
            return new RenameCheckResult(RenameNameRefusal.TakenOrShort, null, $"item {scroll.ItemId} is not a name change scroll");

        if (await repo.CharacterNameExistsAsync(newName))
            return new RenameCheckResult(RenameNameRefusal.TakenOrShort, null, "already taken");

        return new RenameCheckResult(RenameNameRefusal.None, scroll, null);
    }
}
