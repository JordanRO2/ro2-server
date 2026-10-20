using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1305 MovePC. The stock client never sends it; the handler reads nothing, changes nothing and sends nothing.
/// </summary>
/// <remarks>
/// The handler stays registered so that 1305 is not logged as an unhandled opcode.
/// Client position is handled by <see cref="PlayerPosNotifyHandler"/>.
/// </remarks>
public class PlayerMovementHandler : ZoneRmiHandlerBase<PlayerMovementHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.MovePC;
    public override string Name => "PlayerMovement";

    public PlayerMovementHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<PlayerMovementHandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
        : base(scopeFactory, logger, playerManager, broadcast)
    {
    }

    protected override Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogTrace("Session {Id}: 1305 MovePC from {Name} ({Len} bytes) ignored: no stock client builds it",
            session.SessionId, player.Name, payload.Length);
        return Task.CompletedTask;
    }
}
