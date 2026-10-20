using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1594 ReqGameMallPurchaseItemList - request cash shop purchase history</summary>
/// <remarks>
/// <para>
/// REQUEST: one raw byte (<c>U8</c>, NONE family).
/// </para>
/// <para>
/// ANSWER 2874 AnsGameMallPurchaseItemList (<see cref="CashMallPackets.Build2874"/>): the u8 is the request's byte
/// echoed back and the blob lists the account's purchases newest first, up to 200. The history
/// is the one 1598 ReqGameMallPurchase writes (<see cref="CashMallStore"/>, per account); an account that never
/// bought anything gets the 9-byte empty form.
/// </para>
/// </remarks>
public class GameMallPurchaseItemListHandler : ZoneRmiHandlerBase<GameMallPurchaseItemListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallPurchaseItemList;
    public override string Name => "GameMallPurchaseItemList";
    public GameMallPurchaseItemListHandler(IServiceScopeFactory s, ILogger<GameMallPurchaseItemListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // The request's only byte, echoed back at 2874 body+2.
        byte flag = payload.Length >= 1 ? payload.Span[0] : (byte)0;

        IReadOnlyList<CashMallPurchase> history = Array.Empty<CashMallPurchase>();
        if (player.AccountId != 0)
        {
            try
            {
                history = await services.GetRequiredService<ICashMallRepository>().GetPurchaseHistoryAsync(player.AccountId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The client waits for the list: it still goes out, empty.
                Logger.LogWarning(ex, "{Name}: purchase history of account {Account} could not be read; listing none",
                    player.Name, player.AccountId);
            }
        }

        Logger.LogDebug("{Name} requests game mall purchase history (flag {Flag}): {Count} purchase(s)",
            player.Name, flag, history.Count);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallPurchaseItemList,
            CashMallPackets.Build2874(flag, CashMallPackets.HistoryNewestFirst(history)), ct);
    }
}
