using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using Base1596 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallMyInfoHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1596 ReqGameMallMyInfo. Requests the account's cash mall balance, answered with 2868 AnsGameMallMyInfo.</summary>
/// <remarks>
/// The balance is read from <see cref="CashMallStore"/> for the player's account. The response body is built
/// by <see cref="CashMallPackets.Build2868"/>. A session with no player, or a failed read, receives a zero balance.
/// </remarks>
public class ReqGameMallMyInfoHandler : Base1596
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGameMallMyInfo;
    public override string Name => "ReqGameMallMyInfo";
    public ReqGameMallMyInfoHandler(IServiceScopeFactory s, ILogger<ReqGameMallMyInfoHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        long balance = 0;
        var pm = services.GetRequiredService<PlayerStateManager>();
        if (pm.TryGetPlayer(session.SessionId, out var player) && player != null && player.AccountId != 0)
        {
            try
            {
                balance = await services.GetRequiredService<ICashMallRepository>().GetBalanceAsync(player.AccountId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Cash mall balance of account {Account} could not be read; answering 0", player.AccountId);
            }
        }
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGameMallMyInfo, CashMallPackets.Build2868(balance), ct);
    }
}
