using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Tests.Economy;

/// <summary>The sell/trade rigs have no LootService: the left-bag hook runs the real engine, the gain hook is never reached.</summary>
public sealed class ForwardingQuestEvents : IQuestObjectiveEvents
{
    public Task OnItemGainedAsync(ProudSession session, PlayerState player, uint itemId, int count)
        => throw new NotSupportedException();

    public Task OnItemLeftBagAsync(ProudSession session, PlayerState player, uint itemId, IServiceProvider services, ILogger log, CancellationToken ct)
        => QuestCollectDropTicks.OnCollectItemLeftBagAsync(session, player, itemId, services, log, ct);
}
