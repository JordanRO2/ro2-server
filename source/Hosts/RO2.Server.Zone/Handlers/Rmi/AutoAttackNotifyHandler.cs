using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1358 ReqAutoAttackNotify (ByteArray) - Auto-attack state notification</summary>
public class AutoAttackNotifyHandler : ZoneRmiHandlerBase<AutoAttackNotifyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAutoAttackNotify;
    public override string Name => "AutoAttackNotify";

    public AutoAttackNotifyHandler(IServiceScopeFactory s, ILogger<AutoAttackNotifyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 8) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(4); 
        var targetEntityId = reader.ReadUInt32();

        Logger.LogDebug("{Name} auto-attack notify on {Target}", player.Name, targetEntityId);

        
        
        await Task.CompletedTask;
    }
}
