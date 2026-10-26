namespace RO2.Worlds.Domain;

/// <summary>
/// One message of the spawn exchange that shows a player to another (the pair OnPC_In 2504, OnPCViewData 2315, the move speed
/// 2497 and, when the guild registry loaded, the guild view info 2591). The step names the message; the host owns its opcode
/// and builds its body.
/// </summary>
public enum SpawnViewStep
{
    /// <summary>The remote-player record (OnPC_In 2504).</summary>
    PcIn,
    /// <summary>The remote-player view record (OnPCViewData 2315).</summary>
    PcViewData,
    /// <summary>The actor move speed (OnChangeActorMoveSpeed 2497).</summary>
    MoveSpeed,
    /// <summary>The guild view info (NfyPCViewInfoUpdate 2591); only sent when the guild data is available.</summary>
    GuildViewInfo,
}

/// <summary>
/// Who sees a player enter or leave the view: every player on <see cref="MapId"/> except the subject's own session
/// (<see cref="ExcludedSessionId"/>).
/// </summary>
/// <param name="MapId">The map the subject is on.</param>
/// <param name="ExcludedSessionId">The subject's own session, which is never one of its observers.</param>
public readonly record struct ViewAudience(uint MapId, uint ExcludedSessionId)
{
 /// <summary>True when the player on <paramref name="mapId"/> with <paramref name="sessionId"/> observes the subject.</summary>
    public bool Includes(uint mapId, uint sessionId) => mapId == MapId && sessionId != ExcludedSessionId;
}

/// <summary>
/// Pure rule for the visibility of a player entering or leaving the view of the others on its map: the audience (players on
/// the map minus the subject) and the ordered messages that show one player to another. The same audience sees the subject
/// leave (OnPC_Out 2505) as saw it enter.
/// </summary>
public static class SpawnVisibilityRules
{
    private static readonly SpawnViewStep[] WithoutGuild = [SpawnViewStep.PcIn, SpawnViewStep.PcViewData, SpawnViewStep.MoveSpeed];

    private static readonly SpawnViewStep[] WithGuild =
        [SpawnViewStep.PcIn, SpawnViewStep.PcViewData, SpawnViewStep.MoveSpeed, SpawnViewStep.GuildViewInfo];

 /// <summary>The audience of a subject on <paramref name="mapId"/> with <paramref name="sessionId"/>, for both entering and leaving.</summary>
    public static ViewAudience AudienceOf(uint mapId, uint sessionId) => new(mapId, sessionId);

 /// <summary>
 /// The messages that show one player to one observer, in send order: the record, its view record and the move speed, then
 /// the guild view info when <paramref name="sendGuildViewInfo"/> (the guild data is available).
 /// </summary>
    public static IReadOnlyList<SpawnViewStep> StepsFor(bool sendGuildViewInfo) => sendGuildViewInfo ? WithGuild : WithoutGuild;
}
