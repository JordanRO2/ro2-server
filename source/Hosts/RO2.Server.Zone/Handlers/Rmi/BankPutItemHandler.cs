using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1342 ReqBankPutItem - Deposit item to bank</summary>
public class BankPutItemHandler : ZoneRmiHandlerBase<BankPutItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBankPutItem;
    public override string Name => "BankPutItem";
    public BankPutItemHandler(IServiceScopeFactory s, ILogger<BankPutItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await Task.CompletedTask;
    }
}
