using RO2.Characters.Domain;
using RO2.Inventory.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// The rename transaction (RMI 1563), once the name and scroll have passed the host's check: persist the new
/// name, mirror it onto the live session, then consume the scroll. The host keeps the wire — the parse, the
/// check, and the 2487/2315/2329/2813 replies built from what this returns.
/// </summary>
public static class RenameCharacterFlow
{
    /// <summary>Whether the rename persisted, and the scroll count left after the consume.</summary>
    public readonly record struct RenameResult(bool Renamed, int Remaining);

    /// <summary>
    /// Persists the rename (guarded on <paramref name="oldName"/>); on success mirrors the name onto the session
    /// and consumes one <paramref name="scroll"/>. A failed persist returns <c>Renamed=false</c> with nothing
    /// changed. The check-then-rename race and the scroll spend are preserved exactly as the handler had them.
    /// </summary>
    /// <param name="characterId">The character being renamed.</param>
    /// <param name="oldName">The current name, used to guard the persist.</param>
    /// <param name="newName">The already-checked new name.</param>
    /// <param name="scroll">The name-change scroll to consume.</param>
    /// <param name="gateway">The live session, mirrored to the new name.</param>
    /// <param name="repo">The character repository.</param>
    /// <param name="inventory">The inventory repository, for the scroll consume.</param>
    /// <param name="ct">A cancellation token for the persist.</param>
    public static async Task<RenameResult> ApplyAsync(uint characterId, string oldName, string newName,
        InventoryItem scroll, IPlayerSessionGateway gateway, ICharacterRepository repo, IInventoryRepository inventory,
        CancellationToken ct = default)
    {
        if (!await repo.RenameAsync(characterId, oldName, newName, ct))
            return new RenameResult(false, 0);

        gateway.Name = newName;
        if (gateway.Snapshot is { } live) live.Name = newName;

        int remaining = scroll.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(scroll.Id); remaining = 0; }
        else { scroll.Count = remaining; await inventory.UpdateItemAsync(scroll); }

        return new RenameResult(true, remaining);
    }
}
