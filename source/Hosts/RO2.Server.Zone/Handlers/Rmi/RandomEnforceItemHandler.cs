using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1663 (ReqRandomEnforceItem): rerolls the random stats of an equipment item.</summary>
public class RandomEnforceItemHandler : ZoneRmiHandlerBase<RandomEnforceItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRandomEnforceItem;
    public override string Name => "RandomEnforceItem";
    public RandomEnforceItemHandler(IServiceScopeFactory s, ILogger<RandomEnforceItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 20) return;
        var reader = new PacketReader(payload.Span);
        var equipItemId = reader.ReadUInt32();
        var catalystId = reader.ReadUInt32();
        var npcId = reader.ReadUInt32();
        var optionType = reader.ReadUInt32();
        var flags = reader.ReadUInt32();

        Logger.LogDebug("{Name} random enforces item {Id} with catalyst {CId} at NPC {Npc}",
            player.Name, equipItemId, catalystId, npcId);

        
        await Task.CompletedTask;
    }
}
