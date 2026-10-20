using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1610 ReqCardEnforceProbability (IDA: ByteArray) - query card enhancement chance</summary>
public class CardEnforceProbabilityHandler : ZoneRmiHandlerBase<CardEnforceProbabilityHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCardEnforceProbability;
    public override string Name => "CardEnforceProbability";
    public CardEnforceProbabilityHandler(IServiceScopeFactory s, ILogger<CardEnforceProbabilityHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 13) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(8); 

        var cardId = reader.ReadUInt32();
        var enhanceLevel = reader.Remaining >= 1 ? reader.ReadByte() : (byte)0;

        Logger.LogDebug("{Name} queries card enforce probability for card {Id} at +{Lv}",
            player.Name, cardId, enhanceLevel);

        
        
        await Task.CompletedTask;
    }
}
