using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decisions of the departure sequence that precedes a cross-map warp: which of the player's effects a warp ends, which
/// it keeps, whether a kept effect has lines to send, and which messages mark the departure itself. Each is pure: the host
/// takes the snapshot of the player's effects, removes the dropped effects, sends every line and frame, and keeps the original
/// order (drops, ride, Illusion Pyramid, departure frames, kept-effect lines, then the map-load burst).
/// </summary>
/// <remarks>
/// The helpers are generic over the host's effect record so the kept effects stay the host's own objects, read when each
/// line is built exactly as before.
/// </remarks>
public static class WarpDepartureUseCase
{
 /// <summary>The two departure messages, in send order: the departure marker and the self despawn (<see cref="WorldEntryPlan.Departure"/>).</summary>
    public static IReadOnlyList<WorldEntryStep> DepartureSteps => WorldEntryPlan.Departure;

 /// <summary>
 /// The ids of the effects in <paramref name="active"/> that a warp ends, each once, in the order they appear (the order
 /// they were applied).
 /// </summary>
    public static List<uint> DroppedEffectIds<T>(IEnumerable<T> active, Func<T, uint> effectId, Func<uint, bool> endsOnWarp)
    {
        var dropped = new List<uint>();
        foreach (var e in active)
        {
            var id = effectId(e);
            if (endsOnWarp(id) && !dropped.Contains(id))
                dropped.Add(id);
        }
        return dropped;
    }

 /// <summary>The effects in <paramref name="active"/> that a warp keeps, in order. An id the effect table does not end is kept.</summary>
    public static List<T> KeptEffects<T>(IEnumerable<T> active, Func<T, uint> effectId, Func<uint, bool> endsOnWarp)
    {
        var kept = new List<T>();
        foreach (var e in active)
        {
            if (!endsOnWarp(effectId(e)))
                kept.Add(e);
        }
        return kept;
    }

 /// <summary>True when a kept effect with <paramref name="traitLineCount"/> trait-change lines sends a run (its lines, then one sheet).</summary>
    public static bool SendsKeptRun(int traitLineCount) => traitLineCount > 0;

 /// <summary>True when at least one of the <paramref name="kept"/> effects sends a run. Stops at the first.</summary>
    public static bool HasKeptRun<T>(IEnumerable<T> kept, Func<T, int> traitLineCount)
    {
        foreach (var effect in kept)
        {
            if (SendsKeptRun(traitLineCount(effect)))
                return true;
        }
        return false;
    }
}
