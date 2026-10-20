using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>RMI 12017 ReqDSLogin - DS authentication</summary>
public class DSLoginHandler : LoginRmiHandlerBase<DSLoginHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqDSLogin;
    public override string Name => "DSLogin";
    public DSLoginHandler(IServiceScopeFactory s, ILogger<DSLoginHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: DSLogin", session.SessionId); return Task.CompletedTask; }
}
