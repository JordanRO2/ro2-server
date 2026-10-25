using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1646 (ReqRegRevivalMercenary): registers an NPC as a revival mercenary.</summary>
/// <remarks>
/// Request: one u32 actor handle, the target of the skill that triggers the registration.
/// Reply: RMI 2949 (AnsRegRevivalMercenary), a u16 result, a u8 count and a u8 maximum. A target that is
/// not an NPC gets the refusal result. This server has no revival-mercenary system yet, so an NPC target
/// is answered with the refusal as well.
/// </remarks>
public class RegRevivalMercenaryHandler : ZoneRmiHandlerBase<RegRevivalMercenaryHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRegRevivalMercenary;
    public override string Name => "RegRevivalMercenary";

 /// <summary>Result code returned when the target is not an NPC.</summary>
    private const ushort RefusedResult = 24330;

    public RegRevivalMercenaryHandler(IServiceScopeFactory s, ILogger<RegRevivalMercenaryHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 4)
        {
            Logger.LogWarning("{Name}: short 1646 body ({Len} B, retail is 4)", player.Name, payload.Length);
            return;
        }
        var targetHandle = new PacketReader(payload.Span).ReadUInt32();

        var npc = services.GetRequiredService<NpcStateManager>().GetNpc(targetHandle);
        Logger.LogInformation("{Name}: revival-mercenary registration on {Handle:X8} ({What}); no mercenary system, refusing",
            player.Name, targetHandle, npc is null ? "not a live NPC" : $"NPC {npc.NpcId}");

        await session.SendRmiAsync(ZoneS2COpcodes.AnsRegRevivalMercenary, writer =>
        {
            writer.WriteUInt16(RefusedResult); 
            writer.WriteByte(0);               
            writer.WriteByte(0);               
        });
    }
}
