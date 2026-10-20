using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>RMI 12015 ReqGFLogin - GameForge authentication</summary>
public class GFLoginHandler : LoginRmiHandlerBase<GFLoginHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqGFLogin;
    public override string Name => "GFLogin";
    public GFLoginHandler(IServiceScopeFactory s, ILogger<GFLoginHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: GFLogin", session.SessionId); return Task.CompletedTask; }
}
