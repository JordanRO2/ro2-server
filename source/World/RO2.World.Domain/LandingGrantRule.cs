namespace RO2.Worlds.Domain;

/// <summary>
/// Pure rule of an item warp skill's landing grant: a town warp's 1561 answer leaves a grant (skill, map, time) and
/// the cast's 1509 landing may take it. The host keeps the live per-player grant and its lock, and passes the
/// stored grant and "now" in.
/// </summary>
/// <remarks>
/// A grant with skill id 0 is spent (consumed) and never matches. The take is refused when the skill or map differs,
/// when <c>nowUtc</c> is before the grant time, or when more than <see cref="Window"/> has passed; a refused take
/// leaves the grant in place.
/// </remarks>
public static class LandingGrantRule
{
 /// <summary>Time window in which a town warp's landing (1509) still moves the player.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

 /// <summary>
 /// Whether the stored grant (<paramref name="grantedSkillId"/>, <paramref name="grantedMapId"/>,
 /// <paramref name="grantedAtUtc"/>) can be consumed by a landing of <paramref name="skillId"/> on
 /// <paramref name="mapId"/> at <paramref name="nowUtc"/>.
 /// </summary>
    public static bool CanTake(uint grantedSkillId, uint grantedMapId, DateTime grantedAtUtc,
        uint skillId, uint mapId, DateTime nowUtc)
        => !(grantedSkillId == 0 || grantedSkillId != skillId || grantedMapId != mapId
             || nowUtc < grantedAtUtc || nowUtc - grantedAtUtc > Window);
}
