namespace RO2.Combats.Application;

/// <summary>
/// The 1506 ReqSkillCastingStart precondition. A 1506 opens the cast bar and is echoed to every client on the map
/// as 2709, so a malformed one is refused silently (nothing echoed) before anything is sent: the caster must be in
/// the world and alive, and the ground point must be finite (a NaN / Inf is never forwarded — it can crash a
/// client). The skill-id level-row check and the position clamp stay with the host, which also needs the row for
/// the cast time.
/// </summary>
public static class CastBarFlow
{
    /// <summary>Why a 1506 is refused silently, or <see cref="Allowed"/>.</summary>
    public enum StartOutcome
    {
        /// <summary>The 1506 may open the cast bar.</summary>
        Allowed,

        /// <summary>The caster has not loaded the map or is dead.</summary>
        CasterNotReady,

        /// <summary>The ground point carries a non-finite coordinate (NaN / Inf), which must never be forwarded.</summary>
        NonFinitePosition,
    }

    /// <summary>
    /// Checks a 1506's caster and ground point (in the order the handler vets them): caster in the world and alive,
    /// then every coordinate finite. The host applies the skill-id level-row check and the coordinate clamp after an
    /// <see cref="StartOutcome.Allowed"/>.
    /// </summary>
    public static StartOutcome CheckStart(bool casterMapLoaded, bool casterIsDead, float x, float y, float z)
    {
        if (!casterMapLoaded || casterIsDead)
            return StartOutcome.CasterNotReady;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return StartOutcome.NonFinitePosition;
        return StartOutcome.Allowed;
    }
}
