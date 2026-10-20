using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// Handles RMI 12013 (ReqAeriaGamesLogin), the portal-code login. The server has no portal account system,
/// so every request is refused with <see cref="LoginResponseCodes.AccountNotFound"/>.
/// </summary>
/// <remarks>
/// The request body is two Proud wide strings: the authentication code and a second string. Only their
/// lengths are logged; the contents are not decoded. The reply is a 12502 result with account id 0 and no name.
/// </remarks>
public class AeriaLoginHandler : LoginRmiHandlerBase<AeriaLoginHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqAeriaGamesLogin;
    public override string Name => "AeriaLogin";
    public AeriaLoginHandler(IServiceScopeFactory s, ILogger<AeriaLoginHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        int pos = 0;
        bool parsed = TryReadWStrLength(body, ref pos, out int codeChars)
                      & TryReadWStrLength(body, ref pos, out int secondChars);
        if (parsed && pos == body.Length)
            Logger.LogInformation("Session {Id}: 12013 portal-code login (code {Code} chars, second string {Second} chars); " +
                                  "no portal accounts, refused with 12502 {Result}",
                session.SessionId, codeChars, secondChars, LoginResponseCodes.AccountNotFound);
        else
            Logger.LogWarning("Session {Id}: 12013 body of {Len} bytes is not [WStr][WStr]; refused with 12502 {Result}",
                session.SessionId, body.Length, LoginResponseCodes.AccountNotFound);

        
        await LoginService.SendLoginResultAsync(session, LoginResponseCodes.AccountNotFound);
    }

 /// <summary>
 /// Skips one Proud wide string (<c>[u8 widthTag in {1,2}][CompactLen n][n * widthTag]</c>) and
 /// returns its length in characters. The contents are not decoded.
 /// </summary>
    public static bool TryReadWStrLength(ReadOnlySpan<byte> body, ref int pos, out int chars)
    {
        chars = 0;
        if (pos >= body.Length) return false;
        byte width = body[pos];
        if (width is not (1 or 2)) return false;
        if (!body[(pos + 1)..].TryReadCompactLength(out ulong n, out int prefix)) return false;
        long end = (long)pos + 1 + prefix + (long)Math.Min(n, int.MaxValue) * width;
        if (n > int.MaxValue || end > body.Length) return false;
        chars = (int)n;
        pos = (int)end;
        return true;
    }
}
