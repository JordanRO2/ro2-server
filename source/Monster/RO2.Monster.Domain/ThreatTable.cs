namespace RO2.Monster.Domain;

/// <summary>One row of a monster's threat table: an attacker, the name shown on the aggro meter, and the accumulated
/// threat. Threat is banked as a <see cref="long"/> so a long fight never overflows; it is clamped to
/// <see cref="uint"/> only when written to the wire.</summary>
public sealed class ThreatRow
{
    /// <summary>The attacker's actor handle (a PC handle, low byte 0x11).</summary>
    public uint AttackerHandle;
    /// <summary>The name shown on the client's aggro meter.</summary>
    public string Name = string.Empty;
    /// <summary>Accumulated threat.</summary>
    public long Threat;
}

/// <summary>What <see cref="ThreatTable.AddThreat"/> did: the target before and after, and the attacker's new total.</summary>
public readonly record struct ThreatAdded(uint OldTarget, uint NewTarget, long NewThreat, bool IsNew)
{
    /// <summary>Whether the aggro list changed shape: a new attacker joined or the target switched.</summary>
    public bool Changed => IsNew || NewTarget != OldTarget;
}

/// <summary>
/// A monster's threat table (attacker handle to <see cref="ThreatRow"/>) and its current target. The rules are
/// deterministic and free of locks, clocks and I/O; the host owns the lock that serialises them
/// (<c>NpcRuntimeState.ThreatLock</c>) and the 2630 queue that an add feeds.
/// </summary>
/// <remarks>
/// A mutable holder, not an immutable value: the target is chosen by enumeration order (ties go to the row met
/// first), and a <see cref="Dictionary{TKey,TValue}"/> reuses a removed row's slot, so copying it per transition
/// would change which row is met first after a remove. The host mutates it in place under its lock.
/// </remarks>
public sealed class ThreatTable
{
    /// <summary>A challenger takes the target only above <c>TargetSwitchNumerator / TargetSwitchDenominator</c> (110%)
    /// of the current target's threat.</summary>
    private const long TargetSwitchNumerator = 11;

    /// <inheritdoc cref="TargetSwitchNumerator"/>
    private const long TargetSwitchDenominator = 10;

    /// <summary>attackerHandle to its accumulated threat row, in <see cref="Dictionary{TKey,TValue}"/> enumeration
    /// order. Cleared on death and respawn.</summary>
    public Dictionary<uint, ThreatRow> Rows { get; } = new();

    /// <summary>The attacker the monster is currently on, or 0 when the table is empty.</summary>
    public uint AggroTargetHandle { get; set; }

    /// <summary>Banks <paramref name="amount"/> threat on an attacker (a new row, or its name refreshed when one is
    /// given) and re-selects the target. The caller guarantees a non-zero handle and a positive amount.</summary>
    public ThreatAdded AddThreat(uint attackerHandle, string? attackerName, long amount)
    {
        uint oldTarget = AggroTargetHandle;
        bool isNew;
        if (!Rows.TryGetValue(attackerHandle, out var row))
        {
            row = new ThreatRow { AttackerHandle = attackerHandle, Name = attackerName ?? string.Empty };
            Rows[attackerHandle] = row;
            isNew = true;
        }
        else
        {
            isNew = false;
            if (!string.IsNullOrEmpty(attackerName))
                row.Name = attackerName;   // keep the freshest name for the meter
        }

        row.Threat += amount;
        var newTarget = SelectAggroTarget(Rows, oldTarget);
        AggroTargetHandle = newTarget;
        return new ThreatAdded(oldTarget, newTarget, row.Threat, isNew);
    }

    /// <summary>Empties the table and drops the target. Returns whether it held anything (a target or a row).</summary>
    public bool Clear()
    {
        bool hadThreat = AggroTargetHandle != 0 || Rows.Count > 0;
        Rows.Clear();
        AggroTargetHandle = 0;
        return hadThreat;
    }

    /// <summary>Removes an attacker's row. Returns whether it was on the table. Does not touch the target.</summary>
    public bool Remove(uint attackerHandle) => Rows.Remove(attackerHandle);

    /// <summary>Points the target at the highest-threat row that <paramref name="eligible"/> accepts (every row when
    /// null), 0 when none qualifies. Ties go to the row met first. Returns the new target.</summary>
    public uint ReselectTop(Func<uint, bool>? eligible = null)
    {
        uint best = 0;
        long bestThreat = long.MinValue;
        foreach (var kv in Rows)
        {
            if (eligible is not null && !eligible(kv.Key))
                continue;
            if (kv.Value.Threat > bestThreat)
            {
                bestThreat = kv.Value.Threat;
                best = kv.Key;
            }
        }
        AggroTargetHandle = best;
        return best;
    }

    /// <summary>
    /// The monster's target after a threat change: the current target while it is on the table and no other row has
    /// more than 110% of its threat, otherwise the top row (0 for an empty table). The line is strict (a challenger
    /// at exactly 110% does not switch) and ties for the top row go to the row met first.
    /// </summary>
    public static uint SelectAggroTarget(IReadOnlyDictionary<uint, ThreatRow> table, uint currentTarget)
    {
        uint best = 0;
        long bestThreat = long.MinValue;
        foreach (var kv in table)
        {
            if (kv.Value.Threat > bestThreat)
            {
                bestThreat = kv.Value.Threat;
                best = kv.Key;
            }
        }
        if (best == 0)
            return 0;

        if (currentTarget != 0 && currentTarget != best && table.TryGetValue(currentTarget, out var current)
            && bestThreat * TargetSwitchDenominator <= current.Threat * TargetSwitchNumerator)
            return currentTarget;

        return best;
    }
}
