using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1392 ReqPartySummary: a request for the party's summary. Answered with the requester's roster on
/// 2361 OnPartyNotifyUpdate.
/// </summary>
/// <remarks>
/// <para>
/// The build 303 client never sends 1392 (nor 2356), so the request layout is described by its blob alone: a
/// 12-byte compact ByteArray whose first dword is one store of (innerSize 12, msgTag 58159), blob+4 the session
/// handle and blob+8 its only argument. The u32 at blob+8 has no known meaning; it is only logged.
/// </para>
/// <para>
/// The answer. The name-matched 2356 OnPartySummary has a no-op consumer, carries no traffic, and its old raw body crashed the client; the
/// old no-party fallback of a bare 0x00 was itself an invalid compact marker. So the reply is the full roster
/// (2361, built by the shared, <see cref="PartyService.BuildRosterBody"/>) to the requester
/// only, the one reply that changes anything on the client. With no party there is no roster, so nothing is sent.
/// </para>
/// </remarks>
public class PartySummaryHandler : ZoneRmiHandlerBase<PartySummaryHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartySummary;
    public override string Name => "PartySummary";

    /// <summary>1392 blob msgTag, the high half of the dword at blob+0.</summary>
    internal const ushort MsgTagReqPartySummary = 58159;

    /// <summary>1392 blob length: innerSize 12 in that same store.</summary>
    internal const int BlobLength = 12;

    /// <summary>Offset of the request's only argument (a u32) in the 1392 blob, after the handle at blob+4.</summary>
    private const int ArgOffset = 8;

    public PartySummaryHandler(IServiceScopeFactory s, ILogger<PartySummaryHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParseRequest(payload.Span, out uint arg))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartySummary ({Len}B)", session.SessionId, payload.Length);
            return;
        }

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        Logger.LogInformation("{Name}: 1392 PartySummary arg {Arg} (meaning unknown, no client caller); party {Party}",
            player.Name, arg, party?.PartyId);
        if (party == null || party.MemberSessionIds.Count == 0) return;
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyNotifyUpdate, partyService.BuildRosterBody(party));
    }

    /// <summary>
    /// Checks the 1392 envelope (compact ByteArray, blob <c>[u16 12][u16 58159][u32 handle][u32 arg]</c>) and
    /// returns the u32 at blob+8 (<see cref="ArgOffset"/>).
    /// </summary>
    internal static bool TryParseRequest(ReadOnlySpan<byte> body, out uint arg)
    {
        arg = 0;
        if (!PartyAnswerPackets.TryGetCompactBlob(body, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPartySummary, BlobLength, out _))
            return false;
        arg = BinaryPrimitives.ReadUInt32LittleEndian(blob[ArgOffset..]);
        return true;
    }
}
