using System.Collections.Concurrent;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Manages runtime state for all connected players.
/// Thread-safe singleton service.
/// </summary>
public class PlayerStateManager
{
 /// <summary>Actor type tag for a player character, in the low byte of its handle.</summary>
    public const byte PcActorTag = 0x11;

 /// <summary>Width in bits of the actor type tag in the low byte of a handle; the counter sits above it.</summary>
    private const int ActorHandleTagBits = 8;

 /// <summary>Default radius, in world units, for <see cref="GetNearbyPlayers"/>.</summary>
    private const float DefaultNearbyRange = 100f;

 /// <summary>
 /// Builds the actor handle the client uses to identify this player.
 /// </summary>
 /// <remarks>
 /// The low byte is an actor type tag (0x11 for player characters), not a counter. The upper bits hold the session
 /// counter. The client resolves the attacking actor from the handle in the attack notice, so a handle that does not
 /// carry the player tag would play no attack animation. The NPC scheme uses the same layout with a different tag.
 /// </remarks>
    public static uint BuildActorHandle(uint sessionId) => (sessionId << ActorHandleTagBits) | PcActorTag;

    private readonly ConcurrentDictionary<uint, PlayerState> _players = new();

    // Receives live persistent-field changes of registered players for real-time write-behind.
    private readonly IDirtySink _persistSink;

    public PlayerStateManager(IDirtySink persistSink)
    {
        _persistSink = persistSink;
    }

    public bool TryGetPlayer(uint sessionId, out PlayerState? player)
    {
        return _players.TryGetValue(sessionId, out player);
    }

    public PlayerState? GetPlayer(uint sessionId)
    {
        _players.TryGetValue(sessionId, out var player);
        return player;
    }

    public void AddPlayer(uint sessionId, PlayerState player)
    {
        // From here on a change to a persistent field marks the player for persistence. Callers load the stored row
        // into the player before registering it, so the load itself does not echo back.
        player.PersistSink = _persistSink;
        _players[sessionId] = player;
    }

    public bool RemovePlayer(uint sessionId, out PlayerState? player)
    {
        var removed = _players.TryRemove(sessionId, out player);
        if (removed && player is not null)
        {
            // The player left the world: drop its write-behind mark and detach the sink so a late setter cannot
            // re-enqueue it. The final durable write is the caller's explicit flush.
            _persistSink.Forget(player.CharacterId);
            player.PersistSink = null;
            CharacterLeftWorld(player.CharacterId);
        }
        return removed;
    }

 /// <summary>
 /// Clears the per-character state that ends when a character leaves the world: the dungeon registry's last loaded map
 /// and the play clock of the title system. This is the single exit path for logout, character change, socket close
 /// and channel moves, and it runs before the related saves. Skipped while another session still holds the character.
 /// </summary>
    private void CharacterLeftWorld(uint characterId)
    {
        foreach (var other in _players.Values)
        {
            if (other.CharacterId == characterId)
                return;
        }
        DungeonInstanceRegistry.Shared.ForgetLoadedMap(characterId);
        KharaTitleClock.Pause(characterId, DateTime.UtcNow);
    }

    public IEnumerable<PlayerState> GetPlayersOnMap(uint mapId)
    {
        return _players.Values.Where(p => p.MapId == mapId);
    }

    public IEnumerable<PlayerState> GetNearbyPlayers(PlayerState source, float range = DefaultNearbyRange)
    {
        return _players.Values
            .Where(p => p.MapId == source.MapId && p.SessionId != source.SessionId)
            .Where(p =>
            {
                var dx = p.X - source.X;
                var dy = p.Y - source.Y;
                var dz = p.Z - source.Z;
                return (dx * dx + dy * dy + dz * dz) <= range * range;
            });
    }

    public IEnumerable<PlayerState> GetAllPlayers()
    {
        return _players.Values;
    }

    public PlayerState? GetPlayerByName(string name)
    {
        return _players.Values.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public PlayerState? GetPlayerByCharacterId(uint characterId)
    {
        return _players.Values.FirstOrDefault(p => p.CharacterId == characterId);
    }

    public int PlayerCount => _players.Count;
}
