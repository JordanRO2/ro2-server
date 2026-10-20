using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1344 ReqBankPutGold - Deposit gold to bank</summary>
public class BankPutGoldHandler : ZoneRmiHandlerBase<BankPutGoldHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBankPutGold;
    public override string Name => "BankPutGold";
    public BankPutGoldHandler(IServiceScopeFactory s, ILogger<BankPutGoldHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await Task.CompletedTask;
    }
}
