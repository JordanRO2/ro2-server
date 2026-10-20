using System.Collections.Concurrent;

namespace RO2.Server.Zone.Services;

/// <summary>
/// In-memory riding-pet state: which vehicle each connected session is riding. The mount handlers use it to broadcast
/// ride and drop notifications with consistent values, and to tell a late observer about an already mounted player.
/// </summary>
/// <remarks>
/// Implemented as a static class, so it does not depend on the DI container. The state is transient and is not persisted.
/// Entries are keyed by session id and removed on dismount. A session that disconnects while mounted leaves its entry in
/// place until the session id is reused.
/// </remarks>
public static class MountService
{
 /// <summary>A player's active riding-pet mount.</summary>
 /// <remarks>
 /// <see cref="PetId"/> holds the riding skill id and <see cref="PetActorHandle"/> holds the vehicle's data id. The names are
 /// kept because the mount handler binds to them.
 /// </remarks>
    public sealed class MountState
    {
 /// <summary>The rider's actor handle.</summary>
        public uint RiderHandle { get; init; }

 /// <summary>Riding skill id of the mount.</summary>
        public uint PetId { get; init; }

 /// <summary>Vehicle data id of the mount.</summary>
        public uint PetActorHandle { get; init; }

 /// <summary>Always true for a stored entry.</summary>
        public bool Mounted { get; init; } = true;
    }

    
    private static readonly ConcurrentDictionary<uint, MountState> _mounts = new();

 /// <summary>
 /// Records the mount for a session, replacing any earlier one, and returns the stored state.
 /// </summary>
 /// <param name="petId">Riding skill id (see <see cref="MountState"/>).</param>
 /// <param name="petActorHandle">Vehicle data id (see <see cref="MountState"/>).</param>
    public static MountState Mount(uint sessionId, uint riderHandle, uint petId, uint petActorHandle)
    {
        var st = new MountState
        {
            RiderHandle = riderHandle,
            PetId = petId,
            PetActorHandle = petActorHandle,
        };
        _mounts[sessionId] = st;
        return st;
    }

 /// <summary>True when the session is riding a mount.</summary>
    public static bool IsMounted(uint sessionId) => _mounts.ContainsKey(sessionId);

 /// <summary>Returns the session's current mount, or null when it is not mounted.</summary>
    public static MountState? Get(uint sessionId)
        => _mounts.TryGetValue(sessionId, out var st) ? st : null;

 /// <summary>Removes and returns the session's mount, or null when it was not mounted.</summary>
    public static MountState? Dismount(uint sessionId)
        => _mounts.TryRemove(sessionId, out var st) ? st : null;

 /// <summary>
 /// Snapshot of every mounted player. Intended for spawn-time updates to a newly arrived observer.
 /// </summary>
    public static IReadOnlyCollection<MountState> Snapshot() => _mounts.Values.ToArray();
}
