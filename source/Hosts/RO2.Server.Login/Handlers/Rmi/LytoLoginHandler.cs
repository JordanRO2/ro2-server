using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>RMI 12016 ReqLytoLogin - Lyto authentication</summary>
public class LytoLoginHandler : LoginRmiHandlerBase<LytoLoginHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqLytoLogin;
    public override string Name => "LytoLogin";
    public LytoLoginHandler(IServiceScopeFactory s, ILogger<LytoLoginHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: LytoLogin", session.SessionId); return Task.CompletedTask; }
}
