using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1613 ReqCutSceneEnd, sent by the client when a cutscene finishes playing.</summary>
/// <remarks>
/// The request body is a u32 cutscene id. The reply, AnsCutSceneEnd (2901), echoes the id followed by a u16
/// result of 0. Every request is answered with one reply.
/// </remarks>
public class CutSceneEndHandler : ZoneRmiHandlerBase<CutSceneEndHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCutSceneEnd;
    public override string Name => "CutSceneEnd";

    public CutSceneEndHandler(IServiceScopeFactory s, ILogger<CutSceneEndHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4) return;
        var cutSceneId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        Logger.LogDebug("{Name} ended cutscene {CutScene}", player.Name, cutSceneId);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsCutSceneEnd, writer =>
        {
            writer.WriteUInt32(cutSceneId); 
            writer.WriteUInt16(0);          
        });
    }
}
