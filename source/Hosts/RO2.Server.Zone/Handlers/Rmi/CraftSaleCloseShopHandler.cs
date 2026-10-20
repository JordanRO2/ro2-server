using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1449 ReqCraftSaleCloseShop: closes the player's craft sale shop and notifies nearby players.</summary>
/// <remarks>
/// The closer gets RMI 2604 with a u16 result (<see cref="ResultSuccess"/>). Nearby players get RMI 2605 in a
/// compact-length envelope (msgTag 58414): <c>[u32 actor handle][u32 actor handle][u8 0][f32 X][f32 Y][f32 Z]</c>,
/// where the actor handle is the closer's entity id (low byte 0x11 = player) and the position is the closer's.
/// </remarks>
public class CraftSaleCloseShopHandler : ZoneRmiHandlerBase<CraftSaleCloseShopHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleCloseShop;
    public override string Name => "CraftSaleCloseShop";

 /// <summary>Result of the 2604 acknowledgement: success.</summary>
    private const ushort ResultSuccess = 0;

 /// <summary>msgTag of the 2605 close notification (0xE42E).</summary>
    private const ushort MsgTagNfyCloseShop = 58414;

 /// <summary>Byte after the two actor handles in the 2605 body; always 0.</summary>
    private const byte NotifyZeroByte = 0;

    public CraftSaleCloseShopHandler(IServiceScopeFactory s, ILogger<CraftSaleCloseShopHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} closes craft sale shop", player.Name);
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleCloseShop, writer =>
        {
            writer.WriteUInt16(ResultSuccess);
        });

        
        
        
        
        
        
        
        
        var closeHandle = player.EntityId;
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnNfyCraftSaleCloseShop, writer =>
        {
            writer.WriteCompactBABody(p =>
            {
                p.WriteUInt16(MsgTagNfyCloseShop);
                p.WriteUInt32(closeHandle);   // actor handle
                p.WriteUInt32(closeHandle);   // actor handle, repeated
                p.WriteByte(NotifyZeroByte);
                p.WriteSingle(player.X);
                p.WriteSingle(player.Y);
                p.WriteSingle(player.Z);
            });
        });
    }
}
