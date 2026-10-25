using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1655 (ReqRanking): replies with the requesting character's own entry on a ranking board.
/// </summary>
/// <remarks>
/// The request has the same two-byte form as RMI 1654: ranking type and filter. The reply describes the
/// requester. A ranked character gets its row from the published board, with its global rank or its rank
/// within the class filter. An unranked character gets rank 0 and the running score for the current period.
/// </remarks>
public class RankingHandler : ZoneRmiHandlerBase<RankingHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRanking;
    public override string Name => "Ranking";
    public RankingHandler(IServiceScopeFactory s, ILogger<RankingHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        byte rankingType = span.Length >= 1 ? span[0] : (byte)0;
        byte filter = span.Length >= 2 ? span[1] : (byte)0;

        
        
        var character = player.Character;
        if (character is null)
        {
            try { character = await services.GetRequiredService<ICharacterRepository>().GetByIdAsync(player.CharacterId); }
            catch (Exception ex) { Logger.LogWarning(ex, "{Name}: character row could not be read for the 2965", player.Name); }
        }

        var self = await RankingService.GetSelfAsync(services, player, character, rankingType, filter, Logger, ct);
        Logger.LogDebug("{Name} requests their own line on ranking type {Type} (filter {Filter}): rank {Rank}, score {Score}",
            player.Name, rankingType, filter, self.Rank, self.Score);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsRanking, RankingPackets.Build2965(self), ct);
    }
}
