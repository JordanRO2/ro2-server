using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12003 - Extended Auth Data
/// Client sends MAC address, username, auth token, server region after login
/// </summary>
public class ExtendedAuthHandler : LoginRmiHandlerBase<ExtendedAuthHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ExtendedAuth;
    public override string Name => "ExtendedAuth";

    public ExtendedAuthHandler(IServiceScopeFactory scopeFactory, ILogger<ExtendedAuthHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: Extended auth received ({Size} bytes)",
            session.SessionId, payload.Length);

        
        
        
        return Task.CompletedTask;
    }
}
