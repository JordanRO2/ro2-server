using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1390 ReqPartyRight: grant or revoke a party right. Parsed, accepted only from the leader and
/// logged (<see cref="PartyService.SetRight"/>); nothing is sent back.
/// </summary>
/// <remarks>
/// <para>
/// The layout: the build 303 client never sends 1390, so the request is described by its blob alone. The blob is 13
/// bytes: the dword at blob+0 (innerSize 13, msgTag 58155), the session handle at blob+4, a u32 at blob+8 and a
/// u8 at blob+12 (grant 1 or revoke 0); the second argument is never written.
/// </para>
/// <para>
/// The body is COMPACT: <c>[u8 1][u8 13][blob]</c>, so blob+X = body+X+2. The old parse read the u32 at
/// body+8 (= blob+6) and the byte at body+12 (= blob+10).
/// </para>
/// </remarks>
public class PartyRightHandler : ZoneRmiHandlerBase<PartyRightHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyRight;
    public override string Name => "PartyRight";

 /// 1390's msgTag, the high half of the dword at blob+0.
    internal const ushort MsgTagReqPartyRight = 58155;

 /// The blob length written as innerSize.
    internal const int BlobLength = 13;

    public PartyRightHandler(IServiceScopeFactory s, ILogger<PartyRightHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var subject, out var grant))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyRight ({Len}B)", session.SessionId, payload.Length);
            return Task.CompletedTask;
        }

        services.GetRequiredService<PartyService>().SetRight(session.SessionId, subject, grant);
        return Task.CompletedTask;
    }

 /// <summary>
 /// Read a 1390 body: the compact envelope, the 13-byte blob with innerSize 13 and msgTag 58155, the
 /// u32 at blob+8 (meaning unknown) and the grant byte at blob+12.
 /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out uint subject, out byte grant)
    {
        subject = 0;
        grant = 0;
        if (!PartyAnswerPackets.TryGetCompactBlob(body, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPartyRight, BlobLength, out _))
            return false;

        subject = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        grant = blob[12];
        return true;
    }
}
