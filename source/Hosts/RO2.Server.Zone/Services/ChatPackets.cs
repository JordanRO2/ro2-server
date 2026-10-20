using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// S2C 2324 OnReceiveChat, the one chat line the client files in its chat window: every player line
/// (the echo to the sender and the copies to the channel's audience, <see cref="Handlers.Rmi.ChatHandler"/>)
/// and every server line with the SYSTEM name (<see cref="Handlers.Rmi.ChatHandler"/>'s command feedback,
/// <see cref="AnnouncementService.SendSystemChatAsync"/>).
///
/// The body is a U32 VarBlob: [u32 blobLen] with blobLen == body length - 4, then the blob
///   +0  u16 innerSize — 0 on every player line and == blobLen on every SYSTEM line
///   +2  u16 msgTag 58013 (the request 1315 carries 58012)
///   +4  u32 handle — see <see cref="Handlers.Rmi.ChatHandler"/> for which handle each channel carries
///   +8  u8 nameLen, nameLen UTF-16 units of the sender's name
///       u8 chatType, u16 msgLen, msgLen UTF-16 units of the message
///       u32 trailer
/// The trailer is not a field: the client reads it into scratch space and never uses it, and it uses only the
/// name, the chatType and the message. 0 is written. The head is not read either, but it is written in the
/// same shape the stock server uses.
/// </summary>
public static class ChatPackets
{
    /// <summary>2324 msgTag at blob+2.</summary>
    public const ushort OnReceiveChatMsgTag = 58013;

    // The chat channels, the 1315 request's blob+8 byte and the 2324 chatType byte. The client maps its chat
    // modes to them (2 -> 1, 4 -> 2, 32 -> 3, 64 -> 7, anything else -> 0) when sending, and files them back
    // under the same modes (1 -> 2, 2 -> 4, 3 -> 32, 7 -> 64, 11 -> 256, other -> 1). The modes are named after
    // string_ui 9702 "N. Normal", 9705 "P. Party", 9706 "G. Guild", 9703 "A. Area" and 9707 "R. Raid".
    public const byte ChannelNormal = 0;
    public const byte ChannelParty = 1;
    public const byte ChannelGuild = 2;
    public const byte ChannelArea = 3;
    public const byte ChannelRaid = 7;

    /// <summary>
    /// chatType 11: the client files it under category 256, the System tab its own notices use. Every SYSTEM
    /// line carries it.
    /// </summary>
    public const byte ChannelSystem = 11;

    /// <summary>
    /// The sender shown on a system line. The stock value is the bare "SYSTEM", concatenated by the client
    /// straight onto the text with no separator. This server prefixes and spaces it as "[SYSTEM]: " so the line
    /// reads "[SYSTEM]: &lt;text&gt;" instead of "SYSTEM&lt;text&gt;".
    /// </summary>
    public const string SystemSenderName = "[SYSTEM]: ";

    /// <summary>
    /// The most name units the client reads: it clamps the u8 nameLen to 20 and then reads that
    /// many units, so a longer name would leave the rest of the body out of step.
    /// </summary>
    public const int MaxNameUnits = 20;

    /// <summary>
    /// The 2324 body. <paramref name="nameUnits"/> and <paramref name="msgUnits"/> are UTF-16LE bytes and go
    /// out as they are: a player's message is echoed with the 1315's own code units, including the colour
    /// code the client appends (U+F801 in most lines), byte for byte.
    /// <paramref name="innerSizeIsLength"/> is true only for a SYSTEM line (see the summary).
    /// </summary>
    public static byte[] BuildOnReceiveChat2324(uint handle, ReadOnlySpan<byte> nameUnits, byte chatType,
        ReadOnlySpan<byte> msgUnits, bool innerSizeIsLength)
    {
        int nameLen = Math.Min(nameUnits.Length / 2, MaxNameUnits);
        int msgLen = Math.Min(msgUnits.Length / 2, ushort.MaxValue);
        int blobLen = 8 + 1 + nameLen * 2 + 1 + 2 + msgLen * 2 + 4;

        using var w = new PacketWriter(4 + blobLen);
        w.WriteUInt32((uint)blobLen);                                // VarBlob length
        w.WriteUInt16(innerSizeIsLength ? (ushort)blobLen : (ushort)0); // +0 innerSize
        w.WriteUInt16(OnReceiveChatMsgTag);                          // +2 msgTag
        w.WriteUInt32(handle);                                       // +4 handle
        w.WriteByte((byte)nameLen);                                  // +8 nameLen
        w.WriteBytes(nameUnits[..(nameLen * 2)]);
        w.WriteByte(chatType);
        w.WriteUInt16((ushort)msgLen);
        w.WriteBytes(msgUnits[..(msgLen * 2)]);
        w.WriteUInt32(0);                                            // trailer, never read (see the summary)
        return w.ToArray();
    }

    /// <summary>
    /// A SYSTEM line: name "SYSTEM", handle 0 (no actor speaks it), chatType 11, innerSize == blob length, and
    /// a message that ends on its last character. msgLen counts the characters and nothing more (a message
    /// ending in '&lt;' has msgLen equal to its character count), so no terminator unit is appended.
    /// </summary>
    public static byte[] BuildSystemLine2324(string text)
        => BuildOnReceiveChat2324(0, Encoding.Unicode.GetBytes(SystemSenderName), ChannelSystem,
            Encoding.Unicode.GetBytes(text), innerSizeIsLength: true);
}
