using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1566 (NfyRandomDungeon_Enter): enters a random dungeon.</summary>
public class RandomDungeonEnterHandler : ZoneRmiHandlerBase<RandomDungeonEnterHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.NfyRandomDungeon_Enter;
    public override string Name => "RandomDungeonEnter";
    public RandomDungeonEnterHandler(IServiceScopeFactory s, ILogger<RandomDungeonEnterHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} enters random dungeon", player.Name);

        
        await session.SendRmiAsync(ZoneS2COpcodes.NfyJoinDungeonIsRandom, writer =>
        {
            writer.WriteUInt16(0); 
        });
    }
}
