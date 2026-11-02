using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Inbound seam of the Quest context's progress engines: what the other contexts (loot, shops, mail, minigames, gathering)
/// tell a player's quests when the bag changes, so they never name the engines themselves.
/// </summary>
/// <remarks>
/// A host interface, not an Application port: both engines send the progress notifies on the caller's session and mutate
/// the player's live progress registries, so they are session/wire-coupled (the Combat S22/S25 precedent).
/// </remarks>
public interface IQuestObjectiveEvents
{
 /// <summary>
 /// <paramref name="count"/> of item <paramref name="itemId"/> has just entered <paramref name="player"/>'s bag: advances the
 /// held collect quests, sends the 2424 tick and the 2427 close. Called before the item's own pickup notification.
 /// </summary>
    Task OnItemGainedAsync(ProudSession session, PlayerState player, uint itemId, int count);

 /// <summary>
 /// Item <paramref name="itemId"/> has just left the bag (destroyed, sold or offered, already written): sends the 2428
 /// of every held collect or deliver quest naming it. <paramref name="services"/> is the caller's message scope.
 /// </summary>
    Task OnItemLeftBagAsync(ProudSession session, PlayerState player, uint itemId, IServiceProvider services, ILogger log, CancellationToken ct);
}

/// <summary>The host adapter: forwards to the existing engines, unchanged.</summary>
public sealed class QuestObjectiveEvents : IQuestObjectiveEvents
{
    private readonly LootService _loot;

    public QuestObjectiveEvents(LootService loot) => _loot = loot;

    public Task OnItemGainedAsync(ProudSession session, PlayerState player, uint itemId, int count)
        => _loot.AdvanceCollectQuestsAsync(session, player, itemId, count);

    public Task OnItemLeftBagAsync(ProudSession session, PlayerState player, uint itemId, IServiceProvider services, ILogger log, CancellationToken ct)
        => QuestCollectDropTicks.OnCollectItemLeftBagAsync(session, player, itemId, services, log, ct);
}
