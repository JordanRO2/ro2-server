using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Quick-slot bar use-case flows over <see cref="IQuickSlotRepository"/>. Each persists one change and returns the
/// exception it swallowed (or null); the host logs it at Error and STILL sends the ack, so a failed write never
/// stalls the client's quick-slot dialog.
/// </summary>
public static class QuickSlotFlows
{
    /// <summary>Places <paramref name="entry"/> (replacing whatever held its page and slot). Returns the write's exception, or null.</summary>
    public static async Task<Exception?> SetAsync(IQuickSlotRepository repo, CharacterQuickSlot entry)
    {
        try { await repo.SetAsync(entry); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>Clears the entry at (page, slot). Returns the write's exception, or null.</summary>
    public static async Task<Exception?> ClearAsync(IQuickSlotRepository repo, uint characterId, byte page, byte slot)
    {
        try { await repo.ClearAsync(characterId, page, slot); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>Swaps the two positions. Returns the write's exception, or null.</summary>
    public static async Task<Exception?> SwapAsync(IQuickSlotRepository repo, uint characterId, byte pageA, byte slotA, byte pageB, byte slotB)
    {
        try { await repo.SwapAsync(characterId, pageA, slotA, pageB, slotB); return null; }
        catch (Exception ex) { return ex; }
    }
}
