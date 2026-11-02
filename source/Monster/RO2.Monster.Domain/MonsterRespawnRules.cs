namespace RO2.Monster.Domain;

/// <summary>Pure respawn timing: how long a corpse waits and when a dead monster is ready to come back. The reset itself
/// is <see cref="MonsterVitals.Revive"/>; the host owns the registry, the fresh handle and the 2317 announce.</summary>
public static class MonsterRespawnRules
{
    /// <summary>How long a monster stays dead before it is ready to respawn.</summary>
    public static readonly TimeSpan RespawnDelay = TimeSpan.FromSeconds(30);

    /// <summary>Whether <paramref name="vitals"/> is a dead monster whose death stamp is at least <see cref="RespawnDelay"/>
    /// old at <paramref name="nowUtc"/>. A dead monster with no stamp is never ready.</summary>
    public static bool IsRespawnReady(MonsterVitals vitals, DateTime nowUtc) =>
        vitals.IsDead && vitals.DeathTime.HasValue && nowUtc - vitals.DeathTime.Value >= RespawnDelay;
}
