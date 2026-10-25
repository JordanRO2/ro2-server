using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1409 ReqPCProJobChange: job change request. The server has no profession system and broadcasts the current job.</summary>
public class ProJobChangeHandler : ZoneRmiHandlerBase<ProJobChangeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCProJobChange;
    public override string Name => "ProJobChange";

    public ProJobChangeHandler(IServiceScopeFactory s, ILogger<ProJobChangeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (payload.Length < 10) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(6);
        var senderField = reader.ReadUInt32();

        
        
        
        
        
        
        
        
        var character = player.Character;
        (byte proJobLevel, byte proJobId) = character is not null ? CharacterSerializer.WireProJob(character) : default;
        Logger.LogInformation("{Name} requests a pro-job change (sender field {Field:X8}); no profession system, announcing {Id}/{Level}",
            player.Name, senderField, proJobId, proJobLevel);

        var body = QuestProgressPackets.BuildChangeProJob2682(player.EntityId, proJobId, proJobLevel);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyChangeProJob, writer => writer.WriteBytes(body));
    }
}
