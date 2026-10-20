using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// RMI 23023 ReqChatCloseRangeChatRoom: a line typed in a close-range ("local") chat room, answered with
/// 23542 AnsChatCloseRangeChatRoom [u16 0] and then the line itself as 23529 NfyRoomChat_Private to every
/// member of the room, the sender included.
/// </summary>
/// <remarks>
/// <para>
/// Sender: the client's user-chat window send button writes the room GUID of the current close-range room
/// (the one the 2762 answer set), a tab value of 0x10, its own GID as senderUid, and the typed text with the
/// client's colour code appended.
/// </para>
/// <para>
/// Wire: <c>[u32 innerLen = len-4 @0][guid16 room @4][u16 tab 0x0010 @20][u64 senderUid @22][u8 nChars @30][nChars
/// UTF-16LE @31]</c>, with len == 31 + 2·nChars and innerLen == len - 4. The last character is U+F801.
/// </para>
/// <para>
/// The answer goes to the sender's session: 23542 <c>0000</c> (unencrypted) and, in the same millisecond, 23529
/// carrying the 23023 body byte for byte.
/// </para>
/// <para>
/// Why the echo matters: the line is never drawn locally. On receiving 23529 the client finds no private room
/// with that GUID, compares the GUID with its current close-range room, and draws the line only when senderUid is
/// in the room's member map. The sender's own GID is in its map (the owner's from the 2762 handling when the room
/// opened, a joiner's from its 23517), so a sender sees its own line exactly when the server sends it back.
/// </para>
/// <para>
/// Who receives it: every member of the room. The client draws a 23529 on any receiver whose member map holds
/// senderUid, and prints the name from that map entry, not from the wire. Every member's map holds every member:
/// the owner's own GID goes in when the room opens; a joiner's 23538 makes the room its current one and clears
/// the map, and its 23517 fills it with the whole list; each 23516 adds the newcomer to the map of every member
/// already in, the owner included.
/// </para>
/// <para>
/// Recipients: the member list <see cref="CloseRangeChatRoomMembers"/> keeps from the 23020 joins
/// (ReqJoinCloseRangeHandler), looked up by the GUID at body+4; the sender first, so that its 23529 follows
/// its 23542 closely. No list exists for a room nobody has joined yet (its owner is the only
/// member), nor after a restart of this process. Then, and when the sender is not in the list this process
/// holds, the line goes to the sender alone: the one receiver this server knows holds senderUid in its map.
/// </para>
/// <para>
/// Checks before the echo: the body must have the expected shape (it goes back verbatim), and senderUid must
/// equal the uid the session logged in with, the same check as ReqRoomChatHandler's private-room relay. A
/// failed check is logged and gets no answer at all: 23542 only defines result 0 for success, and a non-zero
/// code would show a refusal message the protocol gives no code for.
/// </para>
/// </remarks>
public class ReqChatCloseRangeHandler : ChatRmiHandlerBase<ReqChatCloseRangeHandler>
{
    /// <summary>Size of the leading u32 <c>innerLen</c> (<c>innerLen == len - 4</c>).</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Size of the room GUID.</summary>
    public const int RoomGuidLength = 16;

    /// <summary>Size of the u64 senderUid.</summary>
    public const int SenderUidLength = 8;

    /// <summary>Bytes per UTF-16 code unit of the text.</summary>
    public const int Utf16UnitSize = 2;

    /// <summary>Size of the 23542 answer body: a single u16 result, 0 = accepted.</summary>
    public const int AcceptedAnswerLength = 2;

    /// <summary>body+4: the 16-byte room GUID.</summary>
    public const int RoomOffset = LengthPrefixSize;

    /// <summary>body+22: the u64 senderUid.</summary>
    public const int SenderOffset = 22;

    /// <summary>body+30: the u8 character count.</summary>
    public const int CharCountOffset = 30;

    /// <summary>body+31: the UTF-16LE text.</summary>
    public const int TextOffset = 31;

    /// <summary>What <see cref="Check"/> found.</summary>
    public enum Verdict
    {
        /// <summary>Retail's shape, sent by the session's own uid: answer 23542 [0] and relay it.</summary>
        Accepted,
        /// <summary>Not len == 31 + 2n with innerLen == len - 4 and n &gt; 0.</summary>
        Malformed,
        /// <summary>The session has no 23001 uid yet.</summary>
        NotLoggedIn,
        /// <summary>senderUid is not the session's own uid.</summary>
        WrongSender,
    }

    public override ushort Opcode => ChatC2SOpcodes.ReqChatCloseRangeChatRoom; // 23023
    public override string Name => "ChatCloseRange";
    public ReqChatCloseRangeHandler(IServiceScopeFactory s, ILogger<ReqChatCloseRangeHandler> l) : base(s, l) { }

    /// <summary>
    /// Whether <paramref name="body"/> may be relayed to its room, sent by the session whose 23001 uid is
    /// <paramref name="memberUid"/> (<paramref name="loggedIn"/> false before that 23001).
    /// </summary>
    public static Verdict Check(ReadOnlySpan<byte> body, bool loggedIn, ulong memberUid)
    {
        // Relayed as is, so it must be exactly the expected shape.
        if (body.Length <= TextOffset) return Verdict.Malformed;
        int chars = body[CharCountOffset];
        if (chars == 0 || body.Length != TextOffset + Utf16UnitSize * chars ||
            BinaryPrimitives.ReadUInt32LittleEndian(body) != (uint)(body.Length - LengthPrefixSize))
            return Verdict.Malformed;

        // The receiver draws the line under senderUid (it looks it up in the room's member map), so
        // it must be the sender's own uid: the client writes its own GID there, which is the uid it
        // logged in with (MessengerUid).
        if (!loggedIn) return Verdict.NotLoggedIn;
        return BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(SenderOffset, SenderUidLength)) == memberUid
            ? Verdict.Accepted
            : Verdict.WrongSender;
    }

    /// <summary>23542 AnsChatCloseRangeChatRoom for an accepted line: <c>[u16 0]</c>, NONE family, 2 bytes.</summary>
    public static byte[] AcceptedAnswer() => new byte[AcceptedAnswerLength];

    /// <summary>
    /// Who gets the 23529 of <paramref name="sender"/>'s line, given the connected members this process holds
    /// for the line's room (<see cref="CloseRangeChatRoomMembers.MemberSessions"/>, empty when it holds no
    /// list). All of them, the sender first, when the sender is one of them; otherwise the sender alone.
    /// </summary>
    public static IReadOnlyList<T> Recipients<T>(T sender, IReadOnlyList<T> roomMembers) where T : class
        => roomMembers.Contains(sender)
            ? [sender, .. roomMembers.Where(m => !ReferenceEquals(m, sender))]
            : [sender];

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        bool loggedIn = MessengerUid.TryGet(session, out var memberUid);
        var verdict = Check(body, loggedIn, memberUid);
        if (verdict != Verdict.Accepted)
        {
            Logger.LogWarning("[CloseRangeChat] session {Id}: line not relayed ({Verdict}, {Len} bytes: {Body})",
                session.SessionId, verdict, body.Length, Convert.ToHexString(body));
            return;
        }

        // Dirty text is dropped silently (no 23542, no 23529), then the per-character flood gate.
        if (!ChatText.IsCleanBody(body.Slice(TextOffset, Utf16UnitSize * body[CharCountOffset])))
        {
            Logger.LogWarning("[CloseRangeChat] session {Id}: line with control/private-use characters dropped", session.SessionId);
            return;
        }
        if (!ChannelChatFloodGate.TryAdmit(session))
        {
            Logger.LogInformation("[CloseRangeChat] session {Id}: line dropped, flood limit", session.SessionId);
            return;
        }

        var room = body.Slice(RoomOffset, RoomGuidLength);
        var recipients = Recipients(session, CloseRangeChatRoomMembers.MemberSessions(room));
        int chars = body[CharCountOffset];
        // The client's private-use end markers are stripped (with any NUL) for the log only.
        Logger.LogInformation("[CloseRangeChat] room {Room} {Sender}: {Msg} -> {Count} member(s)",
            Convert.ToHexString(room), session.Username ?? memberUid.ToString(),
            Encoding.Unicode.GetString(body.Slice(TextOffset, Utf16UnitSize * chars))
                .TrimEnd(ChatText.EndMarkerF801, ChatText.EndMarkerF842, '\0'),
            recipients.Count);

        // The 2-byte answer first, then the line in the same millisecond.
        // Both over the unencrypted internal-RMI path the other messenger answers use (see
        // ChatHelper.SendEmptyList); the 23542 is sent unencrypted.
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsChatCloseRangeChatRoom, AcceptedAnswer(), ct);

        // The line verbatim to every recipient, the sender first. DeliverAsync skips a member whose connection
        // went away since the lookup, so one dead member does not cost the others the line.
        var line = payload.ToArray();
        var sends = new List<CloseRangeChatRoomMembers.Send>(recipients.Count);
        foreach (var member in recipients)
            sends.Add(new CloseRangeChatRoomMembers.Send(member, ChatS2COpcodes.NfyRoomChat_Private, line));
        await CloseRangeChatRoomMembers.DeliverAsync(sends);
    }
}
