using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1654 (ReqRankingList): replies with the top-10 board of one ranking.
/// </summary>
/// <remarks>
/// Request: ranking type (byte) and filter (byte), where the filter packs the class selection above the
/// filter type. The reply lists the first ten rows of the board published at the last reset, with their
/// global ranks. A type or filter with no ranked character gets an empty board.
/// </remarks>
public class RankingListHandler : ZoneRmiHandlerBase<RankingListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRankingList;
    public override string Name => "RankingList";

    public RankingListHandler(IServiceScopeFactory s, ILogger<RankingListHandler> l, PlayerStateManager pm, BroadcastService bc)
        : base(s, l, pm, bc)
    {
        
        
        
        try
        {
            using var scope = s.CreateScope();
            RankingService.EnsureClockRunning(scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            l.LogWarning(ex, "Ranking clock could not be started at wiring; the next ranking request retries");
        }
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        byte rankingType = span.Length >= 1 ? span[0] : (byte)0;
        byte filter = span.Length >= 2 ? span[1] : (byte)0;

        var rows = await RankingService.GetBoardAsync(services, rankingType, filter, Logger, ct);
        Logger.LogDebug("{Name} requests the ranking board of type {Type} (filter {Filter}): {Count} row(s)",
            player.Name, rankingType, filter, rows.Count);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsRankingList, RankingPackets.Build2964(rows), ct);
    }
}
