using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1312 ReqChangeCostumeFlag (ByteArray) - Toggle costume visibility</summary>
public class ChangeCostumeFlagHandler : ZoneRmiHandlerBase<ChangeCostumeFlagHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqChangeCostumeFlag;
    public override string Name => "ChangeCostumeFlag";

    public ChangeCostumeFlagHandler(IServiceScopeFactory s, ILogger<ChangeCostumeFlagHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (payload.Length < 12) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var costumeFlags = (uint)reader.ReadUInt16();

        Logger.LogDebug("{Name} changes costume flags to {Flags}", player.Name, costumeFlags);

        
        
        
        
        
        var hideFlag = (ushort)costumeFlags;
        if (player.Character is { } snapshot)
            snapshot.EquipHideFlag = hideFlag;

        
        
        
        var hideBody = ShopItemPackets.BuildChangeHideFlag2380(player.EntityId, hideFlag);
        await session.SendRmiAsync(ZoneS2COpcodes.OnChangeEquipItemHideFlag,
            writer => writer.WriteBytes(hideBody));

        
        
        
        
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeEquipItemHideFlag,
            writer => writer.WriteBytes(hideBody));

        
        
        try
        {
            await services.GetRequiredService<ICharacterRepository>()
                .UpdateEquipHideFlagAsync(player.CharacterId, hideFlag);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Name}: could not persist equip hide flag {Flag}", player.Name, hideFlag);
        }

        
        
        
        
        
        
        
        
        
        
        
    }
}
