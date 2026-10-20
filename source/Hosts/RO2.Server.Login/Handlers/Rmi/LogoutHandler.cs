using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>RMI 12006 ReqLogOut - Logout from login server</summary>
public class LogoutHandler : LoginRmiHandlerBase<LogoutHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqLogOut;
    public override string Name => "LoginLogout";
    public LogoutHandler(IServiceScopeFactory s, ILogger<LogoutHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: LoginLogout", session.SessionId); return Task.CompletedTask; }
}
