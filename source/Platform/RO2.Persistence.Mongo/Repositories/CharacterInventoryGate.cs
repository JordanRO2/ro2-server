using System.Collections.Concurrent;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// SP0-INV: a per-character, process-wide async gate for inventory mutations. The inventory is persisted
/// in Mongo and every handler works read-modify-write against it (load rows, decide, write), so two
/// concurrent mutations of one character interleave at every await and can claim the same free slot or
/// both spend the same stack. Running the whole sequence inside <see cref="RunAsync{T}"/> serialises it per
/// character; different characters never wait on each other.
/// </summary>
/// <remarks>
/// NOT re-entrant: a body that holds the gate for a character must not call back into anything that takes the
/// gate for the same character (the repository's own <c>AddItemAsync</c> does, for an unplaced item), or it
/// deadlocks. The gate is per process; across processes the unique slot index
/// (<c>InventoryRepository.EnsureIndexesAsync</c>) is the backstop.
/// </remarks>
public static class CharacterInventoryGate
{
    private static readonly ConcurrentDictionary<uint, SemaphoreSlim> s_gates = new();

 /// <summary>Runs <paramref name="body"/> while holding <paramref name="characterId"/>'s gate.</summary>
    public static async Task<T> RunAsync<T>(uint characterId, Func<Task<T>> body)
    {
        var gate = s_gates.GetOrAdd(characterId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
