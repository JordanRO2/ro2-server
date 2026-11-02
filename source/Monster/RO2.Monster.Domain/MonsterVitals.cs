namespace RO2.Monster.Domain;

/// <summary>
/// A monster's HP and life state: current and max HP, max SP, whether it is dead and when it died. An immutable value
/// with pure transitions (each returns the next value); the host owns the lock that serialises them
/// (<c>NpcRuntimeState.HpLock</c>) and stores the result, so concurrency and lock order stay on the host.
/// </summary>
public sealed record MonsterVitals
{
    /// <summary>Current HP.</summary>
    public int Hp { get; init; }

    /// <summary>Max HP.</summary>
    public int MaxHp { get; init; }

    /// <summary>Max SP, which is also its current SP (NPCs spend no SP). 0 means it was never resolved.</summary>
    public int MaxSp { get; init; }

    /// <summary>Whether it has died and not yet respawned.</summary>
    public bool IsDead { get; init; }

    /// <summary>When it died (UTC), or null while alive.</summary>
    public DateTime? DeathTime { get; init; }

    /// <summary>Takes <paramref name="damage"/> off HP, at most the HP left. Returns the next value and the HP
    /// actually removed. Does NOT mark death: the caller banks threat first, then asks <see cref="Die"/> when
    /// <see cref="IsOutOfHp"/>.</summary>
    public (MonsterVitals Vitals, int Actual) ApplyDamage(int damage)
    {
        int actual = Math.Min(damage, Hp);
        return (this with { Hp = Hp - actual }, actual);
    }

    /// <summary>Whether HP has reached 0 (or below), the death condition after <see cref="ApplyDamage"/>.</summary>
    public bool IsOutOfHp => Hp <= 0;

    /// <summary>The death transition: HP 0, dead, stamped <paramref name="nowUtc"/>.</summary>
    public MonsterVitals Die(DateTime nowUtc) => this with { Hp = 0, IsDead = true, DeathTime = nowUtc };

    /// <summary>Raises HP by <paramref name="amount"/> up to max HP. A dead monster is left as it is, so a heal never
    /// resurrects a corpse.</summary>
    public MonsterVitals Heal(int amount) => IsDead ? this : this with { Hp = Math.Min(MaxHp, Hp + amount) };

    /// <summary>Restores HP to max, leaving the life state alone (the leash reset).</summary>
    public MonsterVitals RestoreHp() => this with { Hp = MaxHp };

    /// <summary>The respawn reset: full HP, alive, no death stamp.</summary>
    public MonsterVitals Revive() => this with { Hp = MaxHp, IsDead = false, DeathTime = null };
}
