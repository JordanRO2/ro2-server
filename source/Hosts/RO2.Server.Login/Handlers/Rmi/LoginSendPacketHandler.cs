using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>RMI 12007 SendPacket - Generic packet forward</summary>
public class LoginSendPacketHandler : LoginRmiHandlerBase<LoginSendPacketHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.SendPacket;
    public override string Name => "LoginSendPacket";
    public LoginSendPacketHandler(IServiceScopeFactory s, ILogger<LoginSendPacketHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: LoginSendPacket ({Size}b)", session.SessionId, payload.Length); return Task.CompletedTask; }
}
