using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Receives notifications that a player's persistent state has changed. The player model calls
/// <see cref="Mark"/> from its property setters, so every change is captured. The sink coalesces the
/// notifications and persists the latest state in the background.
/// </summary>
public interface IDirtySink
{
 /// <summary>
 /// Records that <paramref name="player"/> has unsaved changes. Non-blocking and thread-safe; notifications
 /// coalesce, so it is safe to call at packet or tick frequency.
 /// </summary>
    void Mark(PlayerState player);

 /// <summary>
 /// Drops any pending entry for a character that has left the world, so a late change cannot re-add it to the
 /// write-behind queue.
 /// </summary>
    void Forget(uint characterId);
}
