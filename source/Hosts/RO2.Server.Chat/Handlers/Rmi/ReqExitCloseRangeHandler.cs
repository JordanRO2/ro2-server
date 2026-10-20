using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// RMI 23022 ReqExitCloseRangeChatRoom → 23541 AnsExitCloseRangeChatRoom: the player leaves a close-range
/// ("local") chat room.
///
/// C2S body, fixed 16 bytes: the room GUID, the one the Zone handed out in 2762 AnsOpenCloseRangeChatRoom
/// (OpenCloseRangeChatRoomHandler).
///
/// Reply, fixed 18 bytes: <c>[u16 result][16B room GUID]</c> (U16 + Blob&lt;16&gt;), the request's GUID
/// echoed byte for byte. Result 0 means the player left. Result 17 (string_ui 9521
/// "Option only available in channels you are participating.") refuses an exit from a room the player already
/// left, such as a lone owner's repeated exit.
/// So the decision is made BEFORE the answer: CloseRangeChatRoomMembers.Leave first, and when this process holds no
/// member list for the room, <see cref="CloseRangeExitCheck"/> (recent exits in memory, then the Zone/Chat signals).
/// A refused exit sends 17 and nothing else: no 23520, no exit signal, so the Zone sends no 2765/2766/2770.
///
/// The leaver is taken out of the room's member list (CloseRangeChatRoomMembers, kept since a joiner came in
/// with 23020), and the members who stay get 23520 NfyChatRoomExitMmeberInfo [room guid][leaver uid]. When the
/// owner leaves an empty room, no 23520 is sent, as nobody is left to tell.
///
/// When the leaver is the room's owner and members remain, the room is not closed: it passes to another member
/// (CloseRangeChatRoomMembers.Leave, LeaveOutcome.HandedOff).
///
/// Then the exit is handed to the Zone through the signal channel (CloseRangeRoomSignals.PublishExitAsync,
/// with the leaver's character id, the member count left and, for a hand-off, the new owner), because the room's
/// sign is updated FROM THE ZONE: 2765 NfyExitCloseRangeChatRoom reaches the owner on its Zone
/// connection just after the 23541, a joiner's exit is 2765 for the joiner + 2770 with the new count, and a
/// hand-off is 2765 for the old owner + 2766 + 2770 for the new one (CloseRangeExitListener). The 23541
/// goes out first and does not wait on the signal: a failed write is logged and costs only the sign.
/// </summary>
public class ReqExitCloseRangeHandler : ChatRmiHandlerBase<ReqExitCloseRangeHandler>
{
    private const int RoomGuidLength = CloseRangeExitCheck.RoomGuidLength;

    public override ushort Opcode => ChatC2SOpcodes.ReqExitCloseRangeChatRoom; // 23022
    public override string Name => "ExitCloseRange";
    public ReqExitCloseRangeHandler(IServiceScopeFactory s, ILogger<ReqExitCloseRangeHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != RoomGuidLength)
        {
            Logger.LogWarning("Session {Id}: 23022 body is {Len} bytes (retail: 16)", session.SessionId, payload.Length);
            return;
        }

        if (session.CharacterId is not uint leaver)
        {
            // Before its 23001 login the session is no member of anything we could look up; answered 0,
            // with no exit signal (the Zone has no character to take the sign from).
            Logger.LogDebug("Session {Id}: 23022 before ChatAuth; answered 0, no exit signal", session.SessionId);
            await session.SendInternalRmiAsync(ChatS2COpcodes.AnsExitCloseRangeChatRoom,
                CloseRangeExitCheck.BuildAnswer(CloseRangeExitCheck.ResultLeft, payload.Span), ct);
            return;
        }

        // session.CharacterId is set by 23001 ChatAuth and is the Character.Id the Zone's room was registered under
        // (PlayerState.CharacterId) and the member list holds.
        var sends = new List<CloseRangeChatRoomMembers.Send>();
        var (outcome, memberCount, newMaster) = CloseRangeChatRoomMembers.Leave(payload.Span, leaver, sends);
        long now = Environment.TickCount64;
        bool accepted;
        bool noRecord = false;
        bool readError = false;   // a signal-read fault, not a genuine aged-out room: still publish the exit
        string why;
        switch (outcome)
        {
            case CloseRangeChatRoomMembers.LeaveOutcome.NotAMember:
                accepted = false;
                why = "not in the room's member list";
                break;
            case CloseRangeChatRoomMembers.LeaveOutcome.UnknownRoom:
                if (!CloseRangeExitCheck.TryAdmitUnknownRoomExit(session.SessionId))
                {
                    // A flood of exits for rooms unknown here. Dropped: there is no refusal frame for it.
                    Logger.LogWarning("Session {Id}: 23022 flood for rooms unknown here; dropped", session.SessionId);
                    return;
                }
                if (!CloseRangeExitCheck.TryClaim(payload.Span, leaver, now))
                {
                    // A duplicate exit can follow the first within milliseconds, possibly before its signal is visible.
                    accepted = false;
                    why = "already left it";
                    break;
                }
                CloseRangeExitCheck.Verdict verdict;
                try
                {
                    verdict = await CloseRangeExitCheck.ResolveAsync(services.GetRequiredService<ICloseRangeRoomSignals>(), payload, leaver, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Cannot tell; answer as before rather than refuse an exit that may be real.
                    Logger.LogWarning(ex, "Session {Id}: could not read the close-range room signals; exit accepted", session.SessionId);
                    verdict = CloseRangeExitCheck.Verdict.NoRecord;
                    readError = true;   // cannot tell aged-out from a backend hiccup: keep the prior publish-on-error behaviour
                }
                accepted = verdict != CloseRangeExitCheck.Verdict.Refuse;
                noRecord = verdict == CloseRangeExitCheck.Verdict.NoRecord;
                why = verdict switch
                {
                    CloseRangeExitCheck.Verdict.Accept => "by the room signals",
                    CloseRangeExitCheck.Verdict.NoRecord => "no open signal for the room",
                    _ => "the room is closed or the leaver was never in it",
                };
                break;
            default: // MemberLeft, OwnerLeft, HandedOff
                accepted = true;
                why = outcome.ToString();
                break;
        }

        Logger.LogInformation("Session {Id}: {Who} leaves close-range chat room {Guid}: {Verdict} ({Why})",
            session.SessionId, session.Username ?? "?", Convert.ToHexString(payload.Span), accepted ? "left" : "refused", why);

        // Same transport as the other messenger answers (see ChatHelper.SendEmptyList).
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsExitCloseRangeChatRoom, CloseRangeExitCheck.BuildAnswer(
            accepted ? CloseRangeExitCheck.ResultLeft : CloseRangeExitCheck.ResultNotParticipating, payload.Span), ct);
        if (!accepted) return;

        CloseRangeExitCheck.RecordAccepted(payload.Span, leaver, now);
        await CloseRangeChatRoomMembers.DeliverAsync(sends);
        // An exit for a room with no open signal is answered 0 but not published; publishing one signal per
        // random GUID pushes real 'open' signals out of the capped bus. Cost: a real exit whose open signal already
        // aged out does not update the Zone's sign (it has aged out too).
        if (noRecord && !readError) return;
        if (outcome != CloseRangeChatRoomMembers.LeaveOutcome.UnknownRoom)
            Logger.LogDebug("Session {Id}: close-range exit {Outcome}, {Count} member(s) left, new master {Master}",
                session.SessionId, outcome, memberCount, newMaster);

        try
        {
            await services.GetRequiredService<ICloseRangeRoomSignals>().PublishExitAsync(
                payload, leaver, memberCount, newMaster, ownerGone: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Session {Id}: could not signal the close-range room exit to the Zone", session.SessionId);
        }
    }
}
