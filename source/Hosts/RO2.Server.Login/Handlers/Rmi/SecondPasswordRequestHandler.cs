using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handler for RMI 12009 - ReqSecondPassword.
/// The client sends it with an empty body right after the 12502 login result, asking which PIN dialog to
/// show, and retail answers every one with S2C 12507 AnsSecondPassword [u16 hasPin]: 18/18 reference logins,
/// body 0 bytes in 18/18, the 12507 always the very next packet. The client then sends 12010
/// (ReqInputSecondPassword) with the PIN.
/// </summary>
public class SecondPasswordRequestHandler : LoginRmiHandlerBase<SecondPasswordRequestHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqSecondPassword;
    public override string Name => "ReqSecondPassword";

    public SecondPasswordRequestHandler(IServiceScopeFactory scopeFactory, ILogger<SecondPasswordRequestHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: ReqSecondPassword (RMI 12009) received ({Size} bytes)",
            session.SessionId, payload.Length);

        
        
        
        if (session.AccountId is null || !LoginHandler.TryGetHasSecondPassword(session, out var hasPin))
        {
            Logger.LogWarning("Session {Id}: ReqSecondPassword before a successful login — not answered",
                session.SessionId);
            return;
        }

        
        
        await session.SendRmiAsync(LoginS2COpcodes.AnsSecondPassword, writer =>
        {
            writer.WriteUInt16(hasPin ? (ushort)1 : (ushort)0);
        });

        Logger.LogDebug("Session {Id}: AnsSecondPassword (12507) sent - hasPin={HasPin}",
            session.SessionId, hasPin);
    }
}
