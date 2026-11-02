using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Inbound seam of the Quest context's fail engine: what combat tells a player's quests when a kill or a death can fail
/// one, so the loot and death paths never name <see cref="QuestFailTriggers"/>.
/// </summary>
/// <remarks>
/// A host interface, not an Application port: the engine sends the 2434 on the caller's session and mutates the login's
/// fail and kill-count registries, so it is session/wire-coupled (the Combat S22/S25 precedent). Separate from
/// <see cref="IQuestObjectiveEvents"/> because that adapter depends on LootService, which is a caller of this seam.
/// </remarks>
public interface IQuestFailEvents
{
 /// <summary>The player killed <paramref name="npcId"/>: fails each held Fail_Type 2 quest whose count it completes.</summary>
    Task OnNpcKilledAsync(ProudSession session, PlayerState player, uint npcId, ILogger? log, CancellationToken ct = default);

 /// <summary>The player died: fails every held Fail_Type 26 quest.</summary>
    Task OnPlayerDiedAsync(ProudSession session, PlayerState player, ILogger? log, CancellationToken ct = default);
}

/// <summary>The host adapter: forwards to the existing engine, unchanged.</summary>
public sealed class QuestFailEvents : IQuestFailEvents
{
    public Task OnNpcKilledAsync(ProudSession session, PlayerState player, uint npcId, ILogger? log, CancellationToken ct = default)
        => QuestFailTriggers.OnNpcKilledAsync(session, player, npcId, log, ct);

    public Task OnPlayerDiedAsync(ProudSession session, PlayerState player, ILogger? log, CancellationToken ct = default)
        => QuestFailTriggers.OnPlayerDiedAsync(session, player, log, ct);
}
