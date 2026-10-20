using RO2.Application.Interfaces;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ChannelMoveLoginHandler>;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1421 ReqChannelMoveLogin (fixed 52-byte body).
/// Layout:
///   0  byte[16] token  - verbatim copy of the ticket the previous zone handed out at 2556+6
///   16 u32 accountId   - the same id that zone wrote at 2556+0
///   20 wchar[12] ip    - the client's own address (e.g. "192.168.0.2"), null-terminated
///   44 8 bytes         - fixed tail, the same one 1303 ReqLoginChannel ends with
/// It carries no channel handle: the target listener resolves the channel from the token.
///
/// <para>This runs on a BRAND-NEW connection, which is why it derives from the stateless
/// RO2.Network.Handlers.ZoneRmiHandlerBase (as NameCheckHandler and ReserveDeleteHandler do) and not from
/// the Zone base that requires a PlayerState. A fresh ProudNet key exchange always happens between the 2556
/// and this 1421, a 1417 LoginPC never precedes it, and the client never sends 1303 ReqLoginChannel on the
/// new connection - so at this point there is no player, no character and no session.AccountId, and a
/// PlayerState-requiring base would return early with "No player state" before the reply could reach the
/// wire, leaving the player waiting forever after the move.</para>
///
/// <para>1421 IS the zone auth for this connection. Nothing else will set session.AccountId, and
/// LoginPCHandler (1417, which follows the 2557 almost immediately) returns early without it,
/// so restoring the account from the ticket here is what makes the move land instead of stranding.</para>
/// </summary>
public class ChannelMoveLoginHandler : NetworkZoneRmiBase
{
    // No ZoneC2SOpcodes constant exists for 1421 (Opcodes.cs stops at ReqChannelMove = 1420).
    public override ushort Opcode => ZoneC2SOpcodes.ReqChannelMoveLogin;
    public override string Name => "ChannelMoveLogin";

    public ChannelMoveLoginHandler(IServiceScopeFactory s, ILogger<ChannelMoveLoginHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < ChannelMoveTicketStore.TokenBytes + 4)
        {
            Logger.LogWarning("Session {SessionId}: ChannelMoveLogin body is {Size} B, expected 52", session.SessionId, payload.Length);
            return;
        }

        // S-H7: a 1421 belongs to a BRAND-NEW connection (see the class remarks: no account, no PlayerState).
        // One on a connection that already holds an account or has a character in the world is refused before
        // the ticket is consumed, so it cannot swap the account under the connection and the ticket stays
        // valid for the connection it was meant for.
        if (ZoneAuthHandler.IsReauthentication(session, services.GetRequiredService<PlayerStateManager>()))
        {
            Logger.LogWarning("Session {SessionId}: ChannelMoveLogin refused - the connection is already "
                + "authenticated (account {AccountId})", session.SessionId, session.AccountId);
            return;
        }

        // Read both fields before anything is awaited (payload.Span is a ref struct and cannot survive one).
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(ChannelMoveTicketStore.TokenBytes, 4));
        var tokenHex = Convert.ToHexString(payload.Span.Slice(0, ChannelMoveTicketStore.TokenBytes));
        var ticket = services.GetRequiredService<ChannelMoveTicketStore>()
            .Consume(payload.Span.Slice(0, ChannelMoveTicketStore.TokenBytes), accountId);

        if (ticket is null)
        {
            // No pending move for this token: it expired (>30 s), the
            // zone restarted mid-move, the account does not match the one the ticket was issued to, it was
            // already claimed, or a newer 1420 of the same account revoked it (ChannelMoveTicketStore keeps
            // one ticket per account; clients always redeem the latest one).
            // We deliberately send NOTHING rather than a plausible-looking 2557: the reply's only payload
            // is a channel handle we would have to invent, and the 1417 that follows it needs the account
            // this ticket was the sole proof of - so a made-up ack only walks the client into a dead end.
            // The account id in the body is not evidence: ZoneAuthHandler refuses to trust the claimed id
            // there for the same reason.
            Logger.LogError("Session {SessionId}: ChannelMoveLogin with no pending move (token {Token}, "
                + "claimed account {AccountId}) - dropping; the client will have to reconnect through Login",
                session.SessionId, tokenHex, accountId);
            return;
        }

        // Restore the account the move was issued for. This is the hand-off that 1303 would normally do:
        // without it, LoginPCHandler bails on "no account" and the character never re-enters the world.
        // The claim has already revoked any other ticket of this account (Consume), so no second
        // connection can be authenticated from the same move.
        session.AccountId = ticket.AccountId;

        // The party's tactics marks set before this move are no longer resent to this character: the map-load
        // 2751 leaves them out for the mover, while marks set after the
        // move do reach it (PartyTacticsMarks remarks). Recorded here, on the accepted ticket, because this is the
        // one point every completed move passes, before the 1417 and the 1304 that sends the list.
        PartyTacticsMarks.NoteChannelMove(ticket.CharacterUid);

        Logger.LogInformation("Session {SessionId}: ChannelMoveLogin accepted - account {AccountId}, "
            + "character UID 0x{Uid:X16}, channel 0x{Handle:X8}",
            session.SessionId, ticket.AccountId, ticket.CharacterUid, ticket.ChannelHandle);

        // Read out of the ticket here, not inside the writer callback: the nullable flow state of a
        // captured local does not survive into a lambda.
        var channelHandle = ticket.ChannelHandle;

        // The account is now on the channel it moved to. Record it (ChannelPresenceStore) so the 1417 that
        // follows this move's 2557 labels the character with the NEW channel, which is what friend lists then
        // show: an online friend's 23505 location follows its latest 2557, not the channel it had at its
        // messenger login (see ChannelPresenceStore). No messenger re-login is sent on a move, so this is the
        // only way the new channel reaches the Chat server. Written before the 2557 so
        // the 1417 cannot overtake it; a storage failure only leaves the old label, it never blocks the move.
        try
        {
            await services.GetRequiredService<IChannelPresenceStore>()
                .SetAccountChannelAsync(ticket.AccountId, channelHandle, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {SessionId}: could not record channel 0x{Handle:X8} of account {AccountId}",
                session.SessionId, channelHandle, ticket.AccountId);
        }

        // Send channel move ack (RMI 2557 AnsChannelMoveLogin)
        await session.SendRmiAsync(ZoneS2COpcodes.AnsChannelMoveLogin, writer =>
        {
            // Fixed 6-byte body, no length prefix: [u16 result][u32 channelHandle], with result 0.
            // The handle is the one the client itself named in 1420, and it maps one-to-one to the
            // port advertised at 2556+54. It is carried in the ticket because 1421 does not repeat it
            // and this connection knows nothing else.
            writer.WriteUInt16(0);
            writer.WriteUInt32(channelHandle);
        });
    }
}
