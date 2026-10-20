using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1376 ReqQuestExecute: executes a quest objective action. The request is logged; no reply is sent.</summary>
public class QuestExecuteHandler : ZoneRmiHandlerBase<QuestExecuteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqQuestExecute;
    public override string Name => "QuestExecute";
    public QuestExecuteHandler(IServiceScopeFactory s, ILogger<QuestExecuteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (payload.Length < 24) return Task.CompletedTask;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var arg0 = reader.ReadUInt32();
        var arg1 = reader.ReadByte();
        var arg2 = reader.ReadByte();
        var arg3 = reader.ReadUInt32();
        var arg4 = reader.ReadUInt32();

        Logger.LogInformation("{Name} sent ReqQuestExecute ({A0}, {A1}, {A2}, {A3}, {A4}); no reply modelled",
            player.Name, arg0, arg1, arg2, arg3, arg4);

        
        
        
        
        
        return Task.CompletedTask;
    }
}
