using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>RMI 12008 ReqServerStatus - Request server status</summary>
public class ServerStatusHandler : LoginRmiHandlerBase<ServerStatusHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqServerStatus;
    public override string Name => "ServerStatus";
    public ServerStatusHandler(IServiceScopeFactory s, ILogger<ServerStatusHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: ServerStatus", session.SessionId); return Task.CompletedTask; }
}
