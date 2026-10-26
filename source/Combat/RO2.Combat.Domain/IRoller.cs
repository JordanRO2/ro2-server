namespace RO2.Combats.Domain;

/// <summary>
/// A percentage roll for combat outcomes. The host backs it with the shared RNG; the Domain never owns the RNG,
/// and the call order of the rolls is part of the wire contract (crit before parry, parry only if crit missed).
/// </summary>
public interface IRoller
{
    /// <summary>
    /// True when a <paramref name="pct"/>-percent event lands. A non-positive pct returns false WITHOUT drawing
    /// (the host's <c>pct &gt; 0 &amp;&amp; draw</c> short-circuit), so a zero-chance outcome consumes no RNG.
    /// </summary>
    bool Roll(double pct);
}
