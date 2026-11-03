using System.Buffers.Binary;
using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Chat;

/// <summary>Wire builders of the add/delete-friend exchange (23508, 23509, 23512).</summary>
public static class SocialPackets
{
    /// <summary>
    /// Length of the head of a one-entry <see cref="BuildFriendListBody"/> body that precedes the
    /// record: <c>[reserved:2][innerLen:4][count:2]</c>.
    /// </summary>
    private const int FriendListBodyHeaderLength = 2 + 4 + 2;

    private const ushort ReservedHeader = 0;        // leading u16 of the 23505 body
    private const byte ReservedByte = 0;            // always-zero byte inside a friend record

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
    private static readonly FriendEntry FailureEntry =
        new(0, "", false, 0, 0, "", TrailerA: 0, TrailerB: 0);

    /// <summary>
    /// 23508 AnsAddFriend: [u16 result][u32 innerLen][one friend record], innerLen == len-6.
    /// A failure carries an all-zero 17-byte record (uid 0, empty name, zeros, empty location, zero
    /// trailers): 12 00 / 06 00 / 21 00 followed by 11 00 00 00 and 17 zero bytes.
    /// The record comes from <see cref="BuildFriendListBody"/>, whose one-entry body is
    /// [reserved:2][innerLen:4][count:2][record], so the record is everything from byte 8.
    /// </summary>
    public static byte[] BuildAddFriendAnswer(ushort result, FriendEntry? entry)
    {
        var e = entry ?? FailureEntry;
        var record = BuildFriendListBody(new[] { e }).AsSpan(FriendListBodyHeaderLength);
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

 /// <summary>One friend in a friend list, as sent in RMI 23505.</summary>
    public sealed record FriendEntry(ulong Uid, string Name, bool Online, byte Level, byte Job,
        string Location, byte TrailerA, byte TrailerB);

 /// <summary>
 /// Builds the body of RMI 23505 AnsFriendList, the reply to 23003. The body is a u16 zero, a u32 inner length, a u16
 /// count and one record per friend. An empty list is encoded with the same header and a zero count.
 /// Record layout: [uid:u64][nameLen:u8][name:UTF-16LE][0:u8][online:u8][level:u8][job:u8][0:u8]
 /// [locationLen:u8][location:UTF-16LE][trailerA:u8][trailerB:u8]. The location is non-empty only for an online friend.
 /// </summary>
    public static byte[] BuildFriendListBody(IReadOnlyList<FriendEntry> friends)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(ReservedHeader);
        var lenPos = w.Reserve(4);              // inner length, filled in below
        int start = w.Length;
        w.WriteUInt16((ushort)friends.Count);
        foreach (var f in friends)
        {
            w.WriteUInt64(f.Uid);
            WriteLen8UnicodeString(w, f.Name);
            w.WriteByte(ReservedByte);
            w.WriteBool(f.Online);
            w.WriteByte(f.Level);
            w.WriteByte(f.Job);
            w.WriteByte(ReservedByte);
            // Location is sent only while the friend is online.
            WriteLen8UnicodeString(w, f.Online ? f.Location : "");
            w.WriteByte(f.TrailerA);
            w.WriteByte(f.TrailerB);
        }
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.GetWrittenSpan().ToArray();
    }

    private static void WriteLen8UnicodeString(PacketWriter w, string? s)
    {
        var bytes = Encoding.Unicode.GetBytes(s ?? "");
        w.WriteByte((byte)(bytes.Length / 2));
        w.WriteBytes(bytes);
    }
}
