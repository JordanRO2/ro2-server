using System.Buffers.Binary;
using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Chat;

/// <summary>Wire builders of the add/delete-friend exchange (23508, 23509, 23512).</summary>
public static class SocialPackets
{
    /// <summary>
    /// Length of the head of a one-entry <see cref="CommunityChannels.BuildFriendListBody"/> body that precedes the
    /// record: <c>[reserved:2][innerLen:4][count:2]</c>.
    /// </summary>
    private const int FriendListBodyHeaderLength = 2 + 4 + 2;

    /// <summary>Size of the 23508 head before the record: <c>[u16 result][u32 innerLen]</c>.</summary>
    private const int AddFriendAnswerHeaderLength = 2 + 4;

    /// <summary>Offset of the u32 record length in a 23508 body.</summary>
    private const int AddFriendAnswerLengthOffset = 2;

    /// <summary>23509 AnsDelFriend: the result is always 0 (a zero u16), and the uid follows at +2.</summary>
    private const int DelFriendAnswerUidOffset = 2;
    private const int DelFriendAnswerLength = 2 + 8;

    /// <summary>Width tag of a bare wide string on the wire: 2 = UTF-16LE, followed by a compact-length character count.</summary>
    private const byte WStrWidthTagUtf16 = 0x02;

    /// <summary>Bytes per UTF-16 code unit.</summary>
    private const int Utf16UnitSize = 2;

    /// <summary>The all-zero record that carries a failed 23508: uid 0, empty name and location, zero trailers.</summary>
    private static readonly CommunityChannels.FriendEntry FailureEntry =
        new(0, "", false, 0, 0, "", TrailerA: 0, TrailerB: 0);

    /// <summary>
    /// 23508 AnsAddFriend: [u16 result][u32 innerLen][one friend record], innerLen == len-6.
    /// A failure carries an all-zero 17-byte record (uid 0, empty name, zeros, empty location, zero
    /// trailers): 12 00 / 06 00 / 21 00 followed by 11 00 00 00 and 17 zero bytes.
    /// The record comes from <see cref="CommunityChannels.BuildFriendListBody"/>, whose one-entry body is
    /// [reserved:2][innerLen:4][count:2][record], so the record is everything from byte 8.
    /// </summary>
    public static byte[] BuildAddFriendAnswer(ushort result, CommunityChannels.FriendEntry? entry)
    {
        var e = entry ?? FailureEntry;
        var record = CommunityChannels.BuildFriendListBody(new[] { e }).AsSpan(FriendListBodyHeaderLength);
        var body = new byte[AddFriendAnswerHeaderLength + record.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), result);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(AddFriendAnswerLengthOffset), (uint)record.Length);
        record.CopyTo(body.AsSpan(AddFriendAnswerHeaderLength));
        return body;
    }

    /// <summary>
    /// 23509 AnsDelFriend: [u16 result][u64 uid], the request's uid echoed back with result 0.
    /// </summary>
    public static byte[] BuildDelFriendAnswer(ulong uid)
    {
        var body = new byte[DelFriendAnswerLength];
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(DelFriendAnswerUidOffset), uid);
        return body;
    }

    /// <summary>
    /// 23512 NfyAddFriend, sent to the player who was just added: a bare WStr of the requester's name,
    /// [widthTag:u8=02][CompactLen nChars][UTF-16LE]. It carries the REQUESTER's name, not the receiver's.
    /// </summary>
    public static byte[] BuildNfyAddFriend(string requesterName)
    {
        var name = Encoding.Unicode.GetBytes(requesterName);
        using var w = new PacketWriter(4 + name.Length);
        w.WriteByte(WStrWidthTagUtf16);
        w.WriteCompactLength(name.Length / Utf16UnitSize);
        w.WriteBytes(name);
        return w.ToArray();
    }
}
