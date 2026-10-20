using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1664 (ReqRandomEnforceItemConfirm): confirms a random enforce.</summary>
public class RandomEnforceItemConfirmHandler : ZoneRmiHandlerBase<RandomEnforceItemConfirmHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRandomEnforceItemConfirm;
    public override string Name => "RandomEnforceItemConfirm";
    public RandomEnforceItemConfirmHandler(IServiceScopeFactory s, ILogger<RandomEnforceItemConfirmHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 21) return;
        var reader = new PacketReader(payload.Span);
        var equipItemId = reader.ReadUInt32();
        var optionType = reader.ReadUInt32();
        var oldValue = reader.ReadUInt32();
        var newValue = reader.ReadUInt32();
        var npcId = reader.ReadUInt32();
        var accept = reader.ReadByte();

        Logger.LogDebug("{Name} {Action} random enforce on item {Id}: {Old} -> {New}",
            player.Name, accept == 1 ? "accepts" : "rejects", equipItemId, oldValue, newValue);

        
        await session.SendRmiAsync(2985, writer =>
        {
            writer.WriteUInt16(0); 
            writer.WriteUInt32(equipItemId);
            writer.WriteByte(accept); 
        });
    }
}
