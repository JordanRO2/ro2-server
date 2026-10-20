namespace RO2.Server.Zone.Services;

/// <summary>
/// The owner of an instance dungeon: the party the character is in when it enters, otherwise the character itself.
/// </summary>
/// <remarks>
/// Instances are keyed by the party context at the time of entry, so a party's instances stay with the party
/// across channel hand-offs.
/// </remarks>
public readonly record struct DungeonInstanceOwner(bool IsParty, uint Id)
{
 /// <summary>The owner an entry or a reset by <paramref name="characterId"/> resolves to.</summary>
    public static DungeonInstanceOwner For(PartyState? party, uint characterId)
        => party is not null ? new DungeonInstanceOwner(true, party.PartyId) : new DungeonInstanceOwner(false, characterId);

    public override string ToString() => IsParty ? $"party {Id}" : $"character {Id}";
}

/// <summary>The result of <see cref="DungeonInstanceRegistry.Enter"/>.</summary>
/// <param name="IsNewInstance">True when the entry opened a new instance.</param>
/// <param name="CountsForCharacter">True when the entry counts toward the character's entry count for the dungeon.</param>
/// <param name="InstanceId">The instance entered. Unique for the life of the process.</param>
public readonly record struct DungeonInstanceEntry(bool IsNewInstance, bool CountsForCharacter, long InstanceId);

/// <summary>
/// Tracks the instance dungeons each owner has open: the DungeonInfo row, the map, and the characters counted in
/// each one. Instances close when every counted character has released them, when a finder run's boss is killed
/// and the cleared lifetime passes, or when the instance has been empty for the empty lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The server runs one copy of each map, so an instance is the bookkeeping that decides whether an entry opens a
/// new instance, counts as a new entry, or re-enters an open one, and what a reset releases.
/// </para>
/// <para>
/// Entering an open instance by a regular entry does not count again. A dungeon finder join always opens a new
/// instance and always counts.
/// </para>
/// <para>
/// The registry is in memory and process-wide, through <see cref="Shared"/>.
/// </para>
/// </remarks>
public sealed class DungeonInstanceRegistry
{
 /// <summary>How long a cleared finder instance stays open after its boss dies.</summary>
    public static readonly TimeSpan ClearedInstanceLifetime = TimeSpan.FromMinutes(10);

 /// <summary>How long an instance stays open once no counted character is on its map.</summary>
    public static readonly TimeSpan EmptyInstanceLifetime = TimeSpan.FromMinutes(10);

 /// <summary>The registry the handlers share, on the system clock.</summary>
    public static DungeonInstanceRegistry Shared { get; } = new();

    private sealed class Instance(long id, byte dungeonId, uint mapId)
    {
        public long Id { get; } = id;
        public byte DungeonId { get; } = dungeonId;
        public uint MapId { get; } = mapId;
        public HashSet<uint> CountedCharacters { get; } = [];
 /// <summary>When the finder run's boss died; null while the instance has no cleared clock.</summary>
        public DateTimeOffset? ClearedAt { get; set; }
 /// <summary>When the instance became empty; null while a counted character is on its map, or before anyone loaded it.</summary>
        public DateTimeOffset? VacatedAt { get; set; }
    }

    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<DungeonInstanceOwner, SortedDictionary<byte, Instance>> _open = new();
    private readonly Dictionary<long, (DungeonInstanceOwner Owner, Instance Instance)> _byId = new();
 /// <summary>Each in-world character's last loaded map, removed when it leaves the world.</summary>
    private readonly Dictionary<uint, uint> _loadedMap = new();
    private long _lastId;

 /// <summary>A registry on the system clock.</summary>
    public DungeonInstanceRegistry() : this(TimeProvider.System) { }

 /// <summary>A registry on <paramref name="clock"/>, which times the cleared and empty lifetimes.</summary>
    public DungeonInstanceRegistry(TimeProvider clock)
    {
        _clock = clock;
    }

 /// <summary>
 /// True when an entry of <paramref name="characterId"/> into <paramref name="dungeonId"/> under
 /// <paramref name="owner"/> would count toward the entry count now: the owner has no open instance of the dungeon,
 /// or has one the character is not counted in.
 /// </summary>
    public bool WouldCount(DungeonInstanceOwner owner, byte dungeonId, uint characterId)
    {
        lock (_lock)
        {
            var byDungeon = OpenLocked(owner);
            return !(byDungeon is not null
                     && byDungeon.TryGetValue(dungeonId, out var instance)
                     && instance.CountedCharacters.Contains(characterId));
        }
    }

 /// <summary>
 /// Enters <paramref name="characterId"/> into the owner's instance of <paramref name="dungeonId"/>, opening it on
 /// <paramref name="mapId"/> when the owner has none. A re-entry stops the instance's empty clock.
 /// </summary>
    public DungeonInstanceEntry Enter(DungeonInstanceOwner owner, byte dungeonId, uint mapId, uint characterId)
    {
        lock (_lock)
        {
            var byDungeon = OpenLocked(owner) ?? (_open[owner] = new SortedDictionary<byte, Instance>());
            var isNew = false;
            if (!byDungeon.TryGetValue(dungeonId, out var instance))
            {
                instance = OpenNewLocked(owner, byDungeon, dungeonId, mapId);
                isNew = true;
            }
            var counts = instance.CountedCharacters.Add(characterId);
            instance.VacatedAt = null;
            return new DungeonInstanceEntry(isNew, counts, instance.Id);
        }
    }

 /// <summary>
 /// A dungeon finder join: opens a new instance of <paramref name="dungeonId"/> for the owner on
 /// <paramref name="mapId"/>, replacing an open one, and enters the character into it. Always a new, counted entry.
 /// </summary>
    public DungeonInstanceEntry EnterFinder(DungeonInstanceOwner owner, byte dungeonId, uint mapId, uint characterId)
    {
        lock (_lock)
        {
            var byDungeon = OpenLocked(owner) ?? (_open[owner] = new SortedDictionary<byte, Instance>());
            if (byDungeon.Remove(dungeonId, out var replaced))
                _byId.Remove(replaced.Id);
            var instance = OpenNewLocked(owner, byDungeon, dungeonId, mapId);
            instance.CountedCharacters.Add(characterId);
            return new DungeonInstanceEntry(true, true, instance.Id);
        }
    }

 /// <summary>
 /// Starts the cleared lifetime of instance <paramref name="instanceId"/>, after its finder run's boss has died. Once
 /// the lifetime has passed the instance closes. Has no effect on an instance that is already closed or already
 /// has a cleared clock.
 /// </summary>
    public void MarkCleared(long instanceId)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(instanceId, out var open) && open.Instance.ClearedAt is null)
                open.Instance.ClearedAt = _clock.GetUtcNow();
        }
    }

 /// <summary>
 /// Records the character's arrival on <paramref name="mapId"/>. Stops the empty clock of the instances on that map
 /// that the character counts in, and starts the empty clock of instances on the map it came from that now have no
 /// counted character on them. Returns the previous map, or null when there was none.
 /// </summary>
    public uint? RecordLoadedMap(uint characterId, uint mapId)
    {
        lock (_lock)
        {
            var previous = _loadedMap.TryGetValue(characterId, out var p) ? p : (uint?)null;
            _loadedMap[characterId] = mapId;
            foreach (var instance in OpenInstancesLocked())
            {
                if (instance.MapId == mapId && instance.CountedCharacters.Contains(characterId))
                    instance.VacatedAt = null;
            }
            if (previous is not { } left || left == mapId)
                return null;
            VacateLocked(left, characterId);
            return left;
        }
    }

 /// <summary>
 /// The character left the world: forgets its last loaded map and starts the empty clock of the open instances on
 /// that map that it counts in and that have no counted character left. Returns the map it was on, or null.
 /// </summary>
    public uint? ForgetLoadedMap(uint characterId)
    {
        lock (_lock)
        {
            if (!_loadedMap.Remove(characterId, out var left))
                return null;
            VacateLocked(left, characterId);
            return left;
        }
    }

 /// <summary>When instance <paramref name="instanceId"/> became empty, or null when it is not empty or is closed.</summary>
    public DateTimeOffset? VacantSince(long instanceId)
    {
        lock (_lock)
        {
            return IsOpenLocked(instanceId) ? _byId[instanceId].Instance.VacatedAt : null;
        }
    }

 /// <summary>
 /// Handles a reset request by <paramref name="characterId"/>. Releases the character's binding to each open instance
 /// of <paramref name="owner"/> it is counted in and returns those dungeon ids in ascending order. An instance closes
 /// once no counted character is bound to it. Nothing is released when the owner has no open instance, or when
 /// <paramref name="currentMapId"/> is the map of one of them.
 /// </summary>
    public IReadOnlyList<byte> Reset(DungeonInstanceOwner owner, uint currentMapId, uint characterId)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(owner, out var byDungeon))
                return [];
            
            
            foreach (var instance in byDungeon.Values)
            {
                if (instance.MapId == currentMapId)
                    return [];
            }
            var open = OpenLocked(owner);
            if (open is null)
                return [];
            var reset = new List<byte>();
            List<byte>? closed = null;
            foreach (var (dungeonId, instance) in open) 
            {
                if (!instance.CountedCharacters.Remove(characterId))
                    continue;
                reset.Add(dungeonId);
                if (instance.CountedCharacters.Count == 0)
                    (closed ??= []).Add(dungeonId);
            }
            if (closed is not null)
            {
                foreach (var dungeonId in closed)
                {
                    _byId.Remove(open[dungeonId].Id);
                    open.Remove(dungeonId);
                }
                if (open.Count == 0)
                    _open.Remove(owner);
            }
            return reset;
        }
    }

 /// <summary>
 /// True while instance <paramref name="instanceId"/> is open and <paramref name="characterId"/> is still counted in
 /// it. A finder run ends on this.
 /// </summary>
    public bool IsBound(long instanceId, uint characterId)
    {
        lock (_lock)
        {
            return IsOpenLocked(instanceId) && _byId[instanceId].Instance.CountedCharacters.Contains(characterId);
        }
    }

 /// <summary>True while instance <paramref name="instanceId"/> is open.</summary>
    public bool IsOpen(long instanceId)
    {
        lock (_lock)
        {
            return IsOpenLocked(instanceId);
        }
    }

    private bool IsOpenLocked(long instanceId)
    {
        if (!_byId.TryGetValue(instanceId, out var open))
            return false;
        OpenLocked(open.Owner);
        return _byId.ContainsKey(instanceId);
    }

 /// <summary>The dungeon ids <paramref name="owner"/> has open, in ascending order.</summary>
    public IReadOnlyList<byte> OpenDungeonIds(DungeonInstanceOwner owner)
    {
        lock (_lock)
        {
            return OpenLocked(owner)?.Keys.ToArray() ?? [];
        }
    }

 /// <summary>
 /// The owner's open instances, after closing those whose cleared or empty lifetime has passed. Null when none
 /// remain.
 /// </summary>
    private SortedDictionary<byte, Instance>? OpenLocked(DungeonInstanceOwner owner)
    {
        if (!_open.TryGetValue(owner, out var byDungeon))
            return null;
        var now = _clock.GetUtcNow();
        List<byte>? expired = null;
        foreach (var (dungeonId, instance) in byDungeon)
        {
            if ((instance.ClearedAt is { } cleared && now - cleared >= ClearedInstanceLifetime)
                || (instance.VacatedAt is { } vacated && now - vacated >= EmptyInstanceLifetime))
                (expired ??= []).Add(dungeonId);
        }
        if (expired is not null)
        {
            foreach (var dungeonId in expired)
            {
                _byId.Remove(byDungeon[dungeonId].Id);
                byDungeon.Remove(dungeonId);
            }
        }
        if (byDungeon.Count > 0)
            return byDungeon;
        _open.Remove(owner);
        return null;
    }

 /// <summary>Every open instance of every owner, after closing the expired ones.</summary>
    private List<Instance> OpenInstancesLocked()
    {
        var all = new List<Instance>();
        foreach (var owner in _open.Keys.ToArray())
        {
            if (OpenLocked(owner) is { } byDungeon)
                all.AddRange(byDungeon.Values);
        }
        return all;
    }

 /// <summary>
 /// The character has left <paramref name="mapId"/>: each open instance on that map that counts the character, and
 /// has no other counted character on the map, starts its empty clock unless it is already running.
 /// </summary>
    private void VacateLocked(uint mapId, uint characterId)
    {
        var now = _clock.GetUtcNow();
        foreach (var instance in OpenInstancesLocked())
        {
            if (instance.MapId != mapId || instance.VacatedAt is not null
                || !instance.CountedCharacters.Contains(characterId))
                continue;
            var someoneInside = false;
            foreach (var counted in instance.CountedCharacters)
            {
                if (counted != characterId && _loadedMap.TryGetValue(counted, out var on) && on == mapId)
                {
                    someoneInside = true;
                    break;
                }
            }
            if (!someoneInside)
                instance.VacatedAt = now;
        }
    }

    private Instance OpenNewLocked(DungeonInstanceOwner owner, SortedDictionary<byte, Instance> byDungeon, byte dungeonId,
        uint mapId)
    {
        var instance = new Instance(++_lastId, dungeonId, mapId);
        byDungeon[dungeonId] = instance;
        _byId[instance.Id] = (owner, instance);
        return instance;
    }
}
