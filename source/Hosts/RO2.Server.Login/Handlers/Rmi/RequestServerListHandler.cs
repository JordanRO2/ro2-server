using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Configuration;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12004 - Request Server List
/// Fallback path: client may send this directly instead of going through 12010
/// </summary>
public class RequestServerListHandler : LoginRmiHandlerBase<RequestServerListHandler>
{
    private readonly ServerConfiguration _config;

    public override ushort Opcode => LoginC2SOpcodes.RequestServerList;
    public override string Name => "RequestServerList";

    public RequestServerListHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<RequestServerListHandler> logger,
        ServerConfiguration config)
        : base(scopeFactory, logger)
    {
        _config = config;
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        // the list (channel handles, Zone host and port) is for authenticated connections only.
        if (session.AccountId is null)
        {
            Logger.LogWarning("Session {Id}: server list (12004) requested before a successful login; not answered",
                session.SessionId);
            return;
        }

        Logger.LogInformation("Session {Id}: Server list requested (12004)", session.SessionId);

        var zoneHost = _config.Zone.Host == "0.0.0.0" ? "127.0.0.1" : _config.Zone.Host;
        await LoginService.SendServerGroupListAsync(
            session, _config.World, zoneHost, (ushort)_config.Zone.Port);
    }
}
