namespace RO2.Worlds.Domain;

/// <summary>
/// Pure area-of-interest rule: whether an actor is close enough to a viewer to be shown. Measured from the retail
/// corpus as an axis-aligned square on X and Y only (Z ignored), half-width 60 world units on standard maps and 200
/// on the wide map types (WoE and the open field). Enter and leave use the same half-width; there is no hysteresis.
/// </summary>
public static class AoiRules
{
    private const float StandardHalfWidth = 60f;
    private const float WideHalfWidth = 200f;

    /// <summary>
    /// The view half-width for a map of the given Map_Type: 200 on the wide types (4 WoE, 5 open field), 60 otherwise.
    /// </summary>
    // shortcut: a handful of specific dungeon maps (410202/410203) and ~22 individual NPC assets also use 200 in the
    // corpus; key those off Map_Type too once they matter. Over/under-including a few actors in those maps only.
    public static float HalfWidth(byte mapType) => mapType is 4 or 5 ? WideHalfWidth : StandardHalfWidth;

    /// <summary>
    /// True when the actor at (<paramref name="actorX"/>, <paramref name="actorY"/>) is within the viewer's square of
    /// half-width <paramref name="half"/> centred on (<paramref name="viewerX"/>, <paramref name="viewerY"/>). Strict
    /// less-than, matching the observed edge (the largest in-view distance in the corpus was 59.67 against a 60 bound).
    /// </summary>
    public static bool InView(float viewerX, float viewerY, float actorX, float actorY, float half)
        => MathF.Abs(actorX - viewerX) < half && MathF.Abs(actorY - viewerY) < half;
}
