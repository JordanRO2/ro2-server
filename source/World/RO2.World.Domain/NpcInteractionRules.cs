namespace RO2.Worlds.Domain;

/// <summary>
/// The "player is standing at the NPC" precondition of the NPC-anchored movement requests (1371 Warp, 1615
/// MultiTeleportNpc). Pure: positions in, bool out, no state, no clock. The host resolves PlayerState /
/// NpcRuntimeState into the primitives and supplies the radius.
/// </summary>
public static class NpcInteractionRules
{
    /// <summary>
    /// Whether the player is on the NPC's map and within <paramref name="radius"/> of it, measured horizontally
    /// (X, Y) as <see cref="ActorGateRules"/> does. NaN-safe: a non-finite position or a NaN radius fails the
    /// comparison and answers false. Liveness is not asked (callers gate dead players).
    /// </summary>
    public static bool InReach(uint playerMapId, float playerX, float playerY,
        uint npcMapId, float npcX, float npcY, float radius)
    {
        if (playerMapId != npcMapId) return false;
        var dx = npcX - playerX;
        var dy = npcY - playerY;
        // Written so that NaN positions or a NaN radius fail the comparison and are refused.
        return MathF.Sqrt(dx * dx + dy * dy) <= radius;
    }
}
