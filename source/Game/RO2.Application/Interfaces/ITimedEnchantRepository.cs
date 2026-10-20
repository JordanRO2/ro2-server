namespace RO2.Application.Interfaces;

/// <summary>One worn item whose timed enchant is still running: its item id, the enchant id and the
/// seconds left.</summary>
public readonly record struct RunningEnchant(ulong ItemId, ushort EnchantId, ushort Seconds);

/// <summary>
/// The timed-enchant timer's view of the shared inventory items: the worn items of a character whose
/// enchant is still counting down, and a compare-and-set that advances one item's remaining seconds without
/// touching any other field (so a concurrent restart or rewrite between the read and the write is not undone).
/// </summary>
public interface ITimedEnchantRepository
{
 /// <summary>The character's worn items (StorageType 1) that carry a running timed enchant.</summary>
    Task<IReadOnlyList<RunningEnchant>> GetRunningWornAsync(uint characterId, CancellationToken ct = default);

 /// <summary>
 /// Sets the item's remaining seconds to <paramref name="newSeconds"/> only while it still carries
 /// <paramref name="expectedEnchantId"/> with <paramref name="expectedSeconds"/> left; true when it was
 /// applied (exactly one row matched), false when something changed the row in between.
 /// </summary>
    Task<bool> TryAdvanceTimerAsync(ulong itemId, ushort expectedEnchantId, ushort expectedSeconds, ushort newSeconds, CancellationToken ct = default);
}
