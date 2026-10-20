using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Login.Services;

namespace RO2.Server.Login.Handlers.Rmi;

/// <summary>
/// RMI 12012 ReqSteamLogin: a Steam login. This server has no Steam identity, so it is refused at once with the
/// 12502 the client already handles, on its Steam path, as an unregistered account.
/// </summary>
/// <remarks>
/// <para>
/// <b>When the client sends it.</b> The client builds it only on its Steam login path, when the launcher's
/// Steam switch is set (<c>/STARTER=2 /steam=yes</c>). It comes right after the 12001/12501 version check.
/// Regular logins use 12002 -&gt; 12502, so the request layout comes from the client alone.
/// </para>
/// <para>
/// <b>Request</b>, NONE family: <c>[u64 steamId][Bytes auth ticket][WStr local string]</c>, where the ticket is
/// a CompactLen + bytes and the string is a WStr <c>[u8 widthTag][CompactLen n][n * widthTag]</c>. Only the steam
/// id is read, for the log; the ticket is never logged or kept.
/// </para>
/// <para>
/// <b>Reply</b>: 12502 with <see cref="LoginResponseCodes.AccountNotFound"/> (1003), account id 0, no name
/// (none was submitted). The client sends 1003 to its state 8, whose Steam branch shows string_ui 2007
/// "Account not registered." titled 2004 "Error"; that branch reads no name. Without an answer the client
/// sits in its Steam login state. What is missing to accept it: a SteamID-to-account link (none exists in
/// this server) and validation of the auth ticket, which needs the Steam publisher Web API key.
/// session.AccountId and the authenticated flag are left unset.
/// </para>
/// </remarks>
public class SteamLoginHandler : LoginRmiHandlerBase<SteamLoginHandler>
{
    public override ushort Opcode => LoginC2SOpcodes.ReqSteamLogin;
    public override string Name => "SteamLogin";
    public SteamLoginHandler(IServiceScopeFactory s, ILogger<SteamLoginHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        if (TryReadRequest(body, out ulong steamId, out int ticketBytes))
            Logger.LogInformation("Session {Id}: 12012 Steam login for SteamID {SteamId} ({Ticket}-byte ticket); " +
                                  "no Steam accounts, refused with 12502 {Result}",
                session.SessionId, steamId, ticketBytes, LoginResponseCodes.AccountNotFound);
        else
            Logger.LogWarning("Session {Id}: 12012 body of {Len} bytes is not [u64][Bytes][WStr]; refused with 12502 {Result}",
                session.SessionId, body.Length, LoginResponseCodes.AccountNotFound);

        
        await LoginService.SendLoginResultAsync(session, LoginResponseCodes.AccountNotFound);
    }

 /// <summary>
 /// Parses <c>[u64 steamId][CompactLen n][n bytes ticket][WStr]</c> and returns the steam id and the ticket's length.
 /// False when the body does not have exactly that shape.
 /// </summary>
    internal static bool TryReadRequest(ReadOnlySpan<byte> body, out ulong steamId, out int ticketBytes)
    {
        steamId = 0;
        ticketBytes = 0;
        if (body.Length < 8) return false;
        steamId = BinaryPrimitives.ReadUInt64LittleEndian(body);
        int pos = 8;
        if (!TrySkipCompactBytes(body, ref pos, 1, out ticketBytes)) return false;
        
        if (pos >= body.Length || body[pos] is not (1 or 2)) return false;
        int width = body[pos++];
        return TrySkipCompactBytes(body, ref pos, width, out _) && pos == body.Length;
    }

 /// <summary>
 /// Skips a Proud compact length <c>[u8 k in {1,2,4,8}][k bytes LE n]</c> followed by <c>n * unit</c> bytes, and
 /// returns n.
 /// </summary>
    private static bool TrySkipCompactBytes(ReadOnlySpan<byte> body, ref int pos, int unit, out int count)
    {
        count = 0;
        if (pos >= body.Length) return false;
        int k = body[pos];
        if (k is not (1 or 2 or 4 or 8) || pos + 1 + k > body.Length) return false;
        ulong n = 0;
        for (int i = 0; i < k; i++) n |= (ulong)body[pos + 1 + i] << (8 * i);
        long end = (long)pos + 1 + k + (long)Math.Min(n, int.MaxValue) * unit;
        if (n > int.MaxValue || end > body.Length) return false;
        count = (int)n;
        pos = (int)end;
        return true;
    }
}
