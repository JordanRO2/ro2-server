using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>RMI 12014 ReqLoginOTP - OTP authentication</summary>
public class OtpLoginHandler : LoginRmiHandlerBase<OtpLoginHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqLoginOTP;
    public override string Name => "OtpLogin";
    public OtpLoginHandler(IServiceScopeFactory s, ILogger<OtpLoginHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: OtpLogin", session.SessionId); return Task.CompletedTask; }
}
