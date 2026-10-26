using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The visibility decisions of the spawn path, over the pure <see cref="SpawnVisibilityRules"/> and
/// <see cref="NpcVisibilityRules"/>: which observers a player entering or leaving the view is shown to, which messages show it,
/// which NPCs a map load announces and whether a view-data request is answered. Each is pure: the host reads the live registry,
/// builds every body (the 2504, 2315, 2497, 2591 and NPC records included), applies the effects and sends in its own original
/// order.
/// </summary>
public static class SpawnVisibilityUseCase
{
 /// <summary>The audience of a player entering or leaving the view of the others on its map (see <see cref="ViewAudience"/>).</summary>
    public static ViewAudience AudienceOf(uint mapId, uint sessionId) => SpawnVisibilityRules.AudienceOf(mapId, sessionId);

 /// <summary>
 /// The members of <paramref name="onMap"/> that observe the subject, in their original order. The host takes the snapshot
 /// before any await, so a player who enters while the exchange runs is not part of it.
 /// </summary>
    public static IEnumerable<T> Observers<T>(ViewAudience audience, IEnumerable<T> onMap, Func<T, uint> mapIdOf, Func<T, uint> sessionIdOf)
        => onMap.Where(p => audience.Includes(mapIdOf(p), sessionIdOf(p)));

 /// <summary>The messages that show one player to one observer, in send order (see <see cref="SpawnVisibilityRules.StepsFor"/>).</summary>
    public static IReadOnlyList<SpawnViewStep> ExchangeSteps(bool sendGuildViewInfo) => SpawnVisibilityRules.StepsFor(sendGuildViewInfo);

 /// <summary>What the map-load announcement does with one present spawn (see <see cref="NpcVisibilityRules.ClassifyAnnouncement"/>).</summary>
    public static NpcAnnouncement ClassifyAnnouncement(bool isDead, bool hasClientTemplate)
        => NpcVisibilityRules.ClassifyAnnouncement(isDead, hasClientTemplate);

 /// <summary>
 /// Decides a view-data request. <paramref name="hasClientTemplate"/> is consulted only for a known NPC, so the host reads the
 /// catalog only then.
 /// </summary>
    public static ViewDataReply DecideViewData(bool isKnownNpc, Func<bool> hasClientTemplate)
        => NpcVisibilityRules.ReplyFor(isKnownNpc, isKnownNpc && hasClientTemplate());
}
