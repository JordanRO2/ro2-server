using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1537 AnsDungeonInfoConform, the player's choice in the dungeon bind popup.</summary>
/// <remarks>
/// The request body is a single u8: 0 for close and 1 for play. No reply is sent and no state is kept. Dungeon
/// binds are not modelled, so the choice is only logged.
/// </remarks>
public class DungeonInfoConfirmHandler : ZoneRmiHandlerBase<DungeonInfoConfirmHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.AnsDungeonInfoConform;
    public override string Name => "DungeonInfoConfirm";
    public DungeonInfoConfirmHandler(IServiceScopeFactory s, ILogger<DungeonInfoConfirmHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 1)
        {
            Logger.LogWarning("Session {Id}: empty AnsDungeonInfoConform", session.SessionId);
            return Task.CompletedTask;
        }

        var choice = payload.Span[0];
        Logger.LogInformation("{Name} answered the dungeon bind popup with {Choice} ({Meaning}); dungeon binds are not modelled",
            player.Name, choice, choice == 0 ? "CLOSE" : "PLAY or the auto-proceed");
        return Task.CompletedTask;
    }
}
