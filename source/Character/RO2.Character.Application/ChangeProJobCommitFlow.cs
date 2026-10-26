using RO2.Characters.Domain;
using RO2.Inventory.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// The persist step of a profession change (RMI 1494): stores the already-applied profession (persist-first,
/// rolling back the in-memory character on failure) and consumes the change item. The decision lives in
/// <see cref="ProJobRules"/>; the serializer prep, the guide-quest swap, the Khara drop and the wire stay in the host.
/// </summary>
public static class ChangeProJobCommitFlow
{
    /// <summary>The swallowed save error (null on success) and whether the change item was removed (vs decremented).</summary>
    public readonly record struct CommitResult(Exception? SaveError, bool Removed);

    /// <summary>
    /// Persists the new profession (and healed job) for a character whose appearance has already been updated. On a
    /// save failure it restores <paramref name="previousAppearance"/> and <paramref name="jobBefore"/> and returns
    /// the error with nothing consumed. On success it consumes one <paramref name="item"/>.
    /// </summary>
    /// <param name="c">The character, already carrying the new profession in its appearance.</param>
    /// <param name="healedJob">The combat-class-healed job to persist, or null when it was not healed.</param>
    /// <param name="jobBefore">The job to restore on a save failure.</param>
    /// <param name="previousAppearance">The appearance JSON to restore on a save failure.</param>
    /// <param name="item">The profession-change item to consume.</param>
    /// <param name="repo">The character repository.</param>
    /// <param name="inventory">The inventory repository.</param>
    /// <param name="ct">A cancellation token for the persist.</param>
    public static async Task<CommitResult> CommitAsync(Character c, byte? healedJob, byte jobBefore,
        string? previousAppearance, InventoryItem item, ICharacterRepository repo, IInventoryRepository inventory,
        CancellationToken ct = default)
    {
        try
        {
            await repo.UpdateAppearanceAndJobAsync(c.Id, c.AppearanceJson, healedJob, ct);
        }
        catch (Exception ex)
        {
            c.AppearanceJson = previousAppearance;
            c.Job = jobBefore;
            return new CommitResult(ex, false);
        }

        int left = item.Count - 1;
        if (left <= 0)
        {
            await inventory.RemoveItemAsync(item.Id);
            return new CommitResult(null, true);
        }

        item.Count = left;
        await inventory.UpdateItemAsync(item);
        return new CommitResult(null, false);
    }
}
