using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1345 ReqBankGetGold - Withdraw gold from bank</summary>
public class BankGetGoldHandler : ZoneRmiHandlerBase<BankGetGoldHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqBankGetGold;
    public override string Name => "BankGetGold";
    public BankGetGoldHandler(IServiceScopeFactory s, ILogger<BankGetGoldHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await Task.CompletedTask;
    }
}
