using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1558 ReqPartyMatchingFindDungeonCancel - leave the dungeon finder queue</summary>
public class PartyMatchingFindDungeonCancelHandler : ZoneRmiHandlerBase<PartyMatchingFindDungeonCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingFindDungeonCancel;
    public override string Name => "PartyMatchingFindDungeonCancel";

    public PartyMatchingFindDungeonCancelHandler(IServiceScopeFactory s, ILogger<PartyMatchingFindDungeonCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        var data = services.GetRequiredService<GameDataStore>();
        var queue = DungeonFinderQueue.Shared;
        var left = queue.Leave(player);
        
        
        
        var round = DungeonFinderPartyRound.Shared.Cancel(player,
            DungeonFinderPartyRound.SeatCheckFor(PlayerManager, services.GetRequiredService<PartyService>()));
        await queue.PruneAndPushAsync(PlayerManager, DungeonFinderQueue.RowsOf(data), SendToSessionAsync);
        Logger.LogDebug("Session {Id}: PartyMatchingFindDungeonCancel (was queued: {Queued})", session.SessionId, left is not null);

        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingFindDungeonCancel, new byte[] { 0, 0 });
        await DungeonFinderPartyRound.DeliverAsync(round.Sends, Broadcast.GetSession);

        if (left is not null)
            await queue.PushStateAsync(left.ListIds, player, DungeonFinderQueue.RowsOf(data), SendToSessionAsync);
    }

    private async Task SendToSessionAsync(uint sessionId, byte[] body)
    {
        if (Broadcast.GetSession(sessionId) is { } target)
            await target.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingInProgressInfo, body);
    }
}
