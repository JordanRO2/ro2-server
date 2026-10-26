using RO2.Combats.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="IRoller"/> with the process RNG, reproducing the old CombatService.Rolls exactly.</summary>
internal sealed class SharedRoller : IRoller
{
    /// <summary>A shared instance (the roll is stateless over Random.Shared).</summary>
    public static readonly SharedRoller Instance = new();

    // pct > 0 && draw: a zero-or-negative chance short-circuits and consumes no RNG, as the old Rolls did.
    public bool Roll(double pct) => pct > 0 && Random.Shared.NextDouble() * 100.0 < pct;
}
