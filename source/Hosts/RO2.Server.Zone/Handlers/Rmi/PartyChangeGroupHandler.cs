using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1391 ReqPartyChangeGroup: the leader moves two members between raid groups. Parsed, checked
/// against the party (<see cref="PartyService.ChangeGroup"/>) and logged; nothing is stored or sent.
/// </summary>
/// <remarks>
/// <para>
/// The layout comes from the client's builder alone: 1391 has 0 frames in the 38.8M archive, the 133.9M
/// dump and the live capture. SendPartySwapMembers allocates a 26-byte ByteArray, stores the
/// dword at blob+0 (innerSize 26, msgTag 58157), the session handle at blob+4, then record A
/// <c>{u64 GID @8, u8 group @16}</c> and record B <c>{u64 GID @17 (unaligned), u8 group @25}</c>, and calls
/// Zone_C2S proxy +744 (= 1391). The body is COMPACT: <c>[u8 1][u8 26][blob]</c>, so blob+X = body+X+2.
/// The old parse skipped 8 body bytes and read a u32 at body+8 (= blob+6, inside the handle), so no member
/// ever matched.
/// </para>
/// <para>
/// The GIDs are the ones the roster records carry (<see cref="PartyAnswerPackets.MemberGid"/>), the same
/// value 1504 names members by. No client code reaches the builder (see <see cref="PartyService.ChangeGroup"/>),
/// so this is defensive parsing of a request build 303 does not send.
/// </para>
/// </remarks>
public class PartyChangeGroupHandler : ZoneRmiHandlerBase<PartyChangeGroupHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyChangeGroup;
    public override string Name => "PartyChangeGroup";

 /// <summary>1391's msgTag, the high half of the store in SendPartySwapMembers .</summary>
    internal const ushort MsgTagReqPartyChangeGroup = 58157;

 /// <summary>The blob length the builder allocates and writes as innerSize.</summary>
    internal const int BlobLength = 26;

    public PartyChangeGroupHandler(IServiceScopeFactory s, ILogger<PartyChangeGroupHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var request))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyChangeGroup ({Len}B)", session.SessionId, payload.Length);
            return Task.CompletedTask;
        }

        services.GetRequiredService<PartyService>().ChangeGroup(session.SessionId,
            request.GidA, request.GroupA, request.GidB, request.GroupB);
        return Task.CompletedTask;
    }

 /// <summary>
 /// Read a 1391 body: the compact envelope, the 26-byte blob with innerSize 26 and msgTag 58157, then
 /// the two (GID, group) records at blob+8/+16 and blob+17/+25.
 /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out PartyChangeGroupRequest request)
    {
        request = default;
        if (!PartyAnswerPackets.TryGetCompactBlob(body, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, MsgTagReqPartyChangeGroup, BlobLength, out var handle))
            return false;

        request = new PartyChangeGroupRequest(
            handle,
            BinaryPrimitives.ReadUInt64LittleEndian(blob[8..]), blob[16],
            BinaryPrimitives.ReadUInt64LittleEndian(blob[17..]), blob[25]);
        return true;
    }
}

/// <summary>The fields of a 1391 blob (SendPartySwapMembers ).</summary>
internal readonly record struct PartyChangeGroupRequest(uint Handle, ulong GidA, byte GroupA, ulong GidB, byte GroupB);
