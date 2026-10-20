using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1459 ReqPartyDelMsg: the player deletes their own notice from a party-matching board (window 41).
/// </summary>
/// <remarks>
/// <para>
/// SENDERS. Two buttons of party-matching window 41 send it:
/// <list type="bullet">
/// <item>Page 0, the recruit board: a button sends kind 1.</item>
/// <item>Page 2, the apply board: a button sends kind 2.</item>
/// </list>
/// Both go through the game facade and the Zone_C2S proxy.
/// </para>
/// <para>
/// REQUEST, 11 bytes with no envelope (Blob&lt;11&gt;):
/// <code>
/// +0 u16 never written by the sender: garbage, ignored
/// +2 u8 kind: 1 = recruit board (1554 notices), 2 = apply board (1458 notices)
/// +3 u64 the sender's own GID
/// </code>
/// </para>
/// <para>
/// ANSWER, 2620 AnsPartyDelMsg, 11 bytes with no envelope. The client reads only the first two fields:
/// <code>
/// +0 u16 result: 0 = deleted; any other value makes the consumer return 0 with no message
/// +2 u8 kind echo: 1 reloads the recruit board (sends 1560 again), 2 reloads the apply board (sends 1460 again)
/// +3 u64 GID echo, not read
/// </code>
/// So after a success the client reloads the board by itself. The kind byte means the same as the tab byte
/// of 2618 (PartyMatchingInviteMsgHandler).
/// </para>
/// <para>
/// The old handler read a u32 at +0, which is the uninitialised pair, and answered with one byte where the
/// reader takes 11. That byte, 1, is a non-zero result, so the client skipped the refresh silently.
/// </para>
/// <para>
/// STATE. Recruit notices (kind 1) live on <see cref="PartyMatchingBoard"/> and apply notices (kind 2, posted
/// by 1458, see PartyMatchingApplyMsgHandler) on <see cref="PartyApplyBoard"/>. Both are keyed by the poster's
/// character id, and a delete removes the sender's own notice, never the one the GID in the body names. The
/// client's refresh that follows the 2620 (1560 or 1460) then no longer lists it.
/// </para>
/// </remarks>
public class PartyDelMsgHandler : ZoneRmiHandlerBase<PartyDelMsgHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyDelMsg;
    public override string Name => "PartyDelMsg";

    public PartyDelMsgHandler(IServiceScopeFactory s, ILogger<PartyDelMsgHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>1459 and 2620 body length (Blob&lt;11&gt; on both sides).</summary>
    internal const int BodyLength = 11;

 /// <summary>Kind byte for the recruit board (window 41 page 0), whose notices 1554 posts.</summary>
    internal const byte KindRecruitBoard = 1;

 /// <summary>Kind byte for the apply board (window 41 page 2), whose notices 1458 posts.</summary>
    internal const byte KindApplyBoard = 2;

    private const int KindOffset = 2;
    private const int GidOffset = 3;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < BodyLength)
        {
            Logger.LogDebug("Session {Id}: PartyDelMsg body is {Len} bytes, need {Need}", session.SessionId, span.Length, BodyLength);
            return;
        }
        byte kind = span[KindOffset];
        ulong gid = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(GidOffset));

        bool removed;
        switch (kind)
        {
            case KindRecruitBoard:
                removed = PartyMatchingBoard.Instance.Remove(player.CharacterId);
                break;
            case KindApplyBoard:
                removed = PartyApplyBoard.Instance.Remove(player.CharacterId);
                break;
            default:
                
                Logger.LogDebug("Session {Id}: PartyDelMsg kind {Kind} is not a board; no answer", session.SessionId, kind);
                return;
        }

        Logger.LogDebug("Session {Id}: PartyDelMsg kind={Kind} gid=0x{Gid:X16} removed={Removed}",
            session.SessionId, kind, gid, removed);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsPartyDelMsg,
            writer => writer.WriteBytes(BuildAnsPartyDelMsg2620(kind, gid)), ct);
    }

 /// <summary>
 /// 2620 body: <c>[u16 result 0][u8 kind][u64 gid]</c>, 11 bytes, no envelope. Result 0 is the only
 /// value the consumer acts on. It then reloads the board <paramref name="kind"/> names. The GID
 /// is the request's own, echoed back.
 /// </summary>
    internal static byte[] BuildAnsPartyDelMsg2620(byte kind, ulong gid)
    {
        var body = new byte[BodyLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 0);
        body[KindOffset] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(GidOffset), gid);
        return body;
    }
}
