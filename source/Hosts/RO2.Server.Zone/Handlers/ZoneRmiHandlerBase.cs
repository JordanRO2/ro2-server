using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers;

/// <summary>
/// Base class for Zone server RMI handlers. Provides access to player state and scoped services.
/// </summary>
public abstract class ZoneRmiHandlerBase<THandler> : IZoneRmiHandler where THandler : class
{
    protected readonly IServiceScopeFactory ScopeFactory;
    protected readonly ILogger<THandler> Logger;
    protected readonly PlayerStateManager PlayerManager;
    protected readonly BroadcastService Broadcast;

    public abstract ushort Opcode { get; }
    public abstract string Name { get; }

    protected ZoneRmiHandlerBase(
        IServiceScopeFactory scopeFactory,
        ILogger<THandler> logger,
        PlayerStateManager playerManager,
        BroadcastService broadcast)
    {
        ScopeFactory = scopeFactory;
        Logger = logger;
        PlayerManager = playerManager;
        Broadcast = broadcast;
    }

    public async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        
        if (!PlayerManager.TryGetPlayer(session.SessionId, out var player) || player == null)
        {
            Logger.LogDebug("Session {Id}: No player state for {Name} handler", session.SessionId, Name);
            return;
        }

        using var scope = ScopeFactory.CreateScope();
        await HandleAsync(session, payload, player, scope.ServiceProvider, ct);
    }

    protected abstract Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        PlayerState player,
        IServiceProvider services,
        CancellationToken ct);
}
