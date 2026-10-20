using System.Buffers.Binary;
using System.Text;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the party-roster broadcasts that carry the shared 208-byte member record:
/// 2361 (OnPartyNotifyUpdate), 2357 (OnPartyNotifyCreate) and 2359 (OnPartyNotifyJoin).
/// </summary>
/// <remarks>
/// <para>
/// A malformed server-to-client body crashes the remote client: the compact-length decoder accepts only the
/// marker bytes {1, 2, 4, 8}, and a rejected body reaches the opcode consumer as a null blob. The framing
/// (compact-length prefix, self size and message tag) is therefore built exactly as the client expects.
/// </para>
/// <para>
/// Envelope (all three): the standard compact ByteArray envelope (<see cref="PacketWriter.WriteCompactBABody"/>),
/// <c>[compactLen(selfLen)][u16 selfLen][u16 msgTag][payload]</c>, where <c>selfLen = 2 + (2 + payload.Length)</c>
/// and the compact marker is 1 when selfLen &lt; 0x80, otherwise 2. Every roster broadcast is longer than 0x80
/// bytes, so its marker is always 2. The payload is <c>header ++ count x 208-byte member records</c>.
/// </para>
/// <para>
/// Member record (208 bytes, offsets record-relative):
/// </para>
/// <code>
/// +0    u32 actor handle          low byte 0x11 for a player character
/// +4    u64 character GID
/// +15   u8  level
/// +16   i16 combat class
/// +18   u8  profession            ProJob type id, 1 to 4
/// +19   HP / SP block
/// +35   u32 map id
/// +41   3 x f32 position
/// +53   u8  online flag           1 in the world, 0 locked (away)
/// +54   8 bytes                   not written, left zero
/// +62   u8  frame slot
/// +64   u8  ProJob level
/// +65   u8  ProJob id
/// +66   wchar_t[21] character name   UTF-16LE fixed buffer
/// +108  wchar_t[21] guild name       UTF-16LE fixed buffer
/// +150  58 bytes                  trailing block, always zero
/// </code>
/// </remarks>
public static class PartyNotifyPackets
{
    /// <summary>Message tag of RMI 2357 (OnPartyNotifyCreate): 0xE331.</summary>
    public const ushort MsgTagPartyNotifyCreate = 58161;

    /// <summary>Message tag of RMI 2359 (OnPartyNotifyJoin): 0xE333.</summary>
    public const ushort MsgTagPartyNotifyJoin = 58163;

    /// <summary>Message tag of RMI 2361 (OnPartyNotifyUpdate): 0xE335.</summary>
    public const ushort MsgTagPartyNotifyUpdate = 58165;

    /// <summary>Length of the shared party-member record, in bytes.</summary>
    public const int MemberRecordLen = 208;

    /// <summary>Maximum number of member records in one roster broadcast; the member count is a single byte.</summary>
    public const int MaxMemberRecords = byte.MaxValue;

    /// <summary>Fixed header length of a 2361 body, in bytes, before the first member record.</summary>
    public const int UpdateHeaderLen = 63;

    /// <summary>Fixed header length of a 2357 body, in bytes, before the first member record.</summary>
    public const int CreateHeaderLen = 62;

    /// <summary>Fixed header length of a 2359 body, in bytes. The body carries a single joining member and no member count.</summary>
    public const int JoinHeaderLen = 8;

    // ---- member record offsets (record-relative) -----------------------------------------------

    /// <summary>Offset of the actor handle in a member record (u32; low byte 0x11 for players).</summary>
    public const int RecActorHandleOff = 0;

    /// <summary>Offset of the character GID in a member record (u64).</summary>
    public const int RecGidOff = 4;

    /// <summary>Offset of the character name in a member record (UTF-16LE buffer).</summary>
    public const int RecNameOff = 66;

    /// <summary>Offset of the guild name in a member record (UTF-16LE buffer).</summary>
    public const int RecGuildOff = 108;

    /// <summary>Offset of the trailing block of a member record (58 bytes, always zero); the guild name buffer ends here.</summary>
    public const int RecTrailerOff = 150;

    /// <summary>Maximum number of characters in the name buffer, before the guild name field.</summary>
    public const int RecNameMaxChars = (RecGuildOff - RecNameOff) / 2;

    /// <summary>Maximum number of characters in the guild name buffer, before the trailing block.</summary>
    public const int RecGuildMaxChars = (RecTrailerOff - RecGuildOff) / 2;

    /// <summary>Offset of the profession byte in a member record (ProJob type ID, 1 to 4).</summary>
    public const int RecProfessionOff = 18;

    /// <summary>Offset of the online flag in a member record: <see cref="MemberOnline"/> when the member is in the world, <see cref="MemberLocked"/> when locked (away).</summary>
    public const int RecOnlineOff = 53;

    /// <summary>Online flag value of a member who is in the world.</summary>
    public const byte MemberOnline = 1;

    /// <summary>Online flag value of a locked (away) member; the client draws the lock icon.</summary>
    public const byte MemberLocked = 0;

    /// <summary>Offset of the ProJob level byte in a member record.</summary>
    public const int RecProJobLevelOff = 64;

    /// <summary>Offset of the ProJob ID byte in a member record.</summary>
    public const int RecProJobIdOff = 65;

    // ---- header offsets (payload-relative; the payload is the bytes after the u16 msgTag) -------

    /// <summary>Offset of the party handle (u32; low byte 0x15) in the roster header.</summary>
    public const int HdrPartyHandleOff = 4;

    /// <summary>Offset of the member count byte in the roster header.</summary>
    public const int HdrCountOff = 11;

    /// <summary>
    /// Offset of the member count byte that the 2361 client reads to size its record loop. It must carry the same
    /// value as the count at <see cref="HdrCountOff"/>, so a template reused with a different roster size must
    /// update both. This is also why the 2361 header is one byte longer than the 2357 one; in 2357 this offset is
    /// the first record byte.
    /// </summary>
    public const int UpdateHdrClientCountOff = 62;

    /// <summary>Offset of the leader GID (u64) in the roster header.</summary>
    public const int HdrLeaderGidOff = 12;

    /// <summary>Offset of the second copy of the leader GID in the roster header.</summary>
    public const int HdrLeaderGid2Off = 54;

    // ---- frame assemblers ----------------------------------------------------------------------

    /// <summary>
    /// Builds RMI 2361 (OnPartyNotifyUpdate): the full party roster, sent on any membership or state change.
    /// The payload is <c>63 + count x 208</c> bytes.
    /// </summary>
    /// <param name="partyHandle">Party handle, written at payload offset <see cref="HdrPartyHandleOff"/>.</param>
    /// <param name="header">
    /// The 63-byte fixed header template. Only the party handle and the two member count bytes are overwritten;
    /// every other byte (leader GID copies and the party settings) is copied unchanged.
    /// </param>
    /// <param name="memberRecords">The 208-byte member records, one per member (1 to 255).</param>
    public static byte[] BuildPartyNotifyUpdate2361(uint partyHandle, ReadOnlySpan<byte> header,
        IReadOnlyList<byte[]> memberRecords)
        => BuildRosterBroadcast(MsgTagPartyNotifyUpdate, UpdateHeaderLen, partyHandle, header,
            memberRecords, writeCount: true, clientCountOff: UpdateHdrClientCountOff);

    /// <summary>
    /// Builds RMI 2357 (OnPartyNotifyCreate): the initial roster sent when a party forms. Identical to 2361
    /// except for the message tag and a header one byte shorter.
    /// </summary>
    public static byte[] BuildPartyNotifyCreate2357(uint partyHandle, ReadOnlySpan<byte> header,
        IReadOnlyList<byte[]> memberRecords)
        => BuildRosterBroadcast(MsgTagPartyNotifyCreate, CreateHeaderLen, partyHandle, header,
            memberRecords, writeCount: true);

    /// <summary>
    /// Builds RMI 2359 (OnPartyNotifyJoin): a single-member delta sent when one member joins the party.
    /// The payload is the 8-byte header followed by exactly one 208-byte record.
    /// </summary>
    public static byte[] BuildPartyNotifyJoin2359(uint partyHandle, ReadOnlySpan<byte> header,
        byte[] memberRecord)
        => BuildRosterBroadcast(MsgTagPartyNotifyJoin, JoinHeaderLen, partyHandle, header,
            new[] { memberRecord }, writeCount: false);

    private static byte[] BuildRosterBroadcast(ushort msgTag, int headerLen, uint partyHandle,
        ReadOnlySpan<byte> header, IReadOnlyList<byte[]> records, bool writeCount,
        int clientCountOff = -1)
    {
        if (header.Length != headerLen)
            throw new ArgumentException($"header must be {headerLen} bytes, got {header.Length}", nameof(header));
        if (records.Count is < 1 or > MaxMemberRecords)
            throw new ArgumentOutOfRangeException(nameof(records), records.Count, $"record count must be 1..{MaxMemberRecords}");
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i] is null || records[i].Length != MemberRecordLen)
                throw new ArgumentException(
                    $"member record {i} must be {MemberRecordLen} bytes, got {records[i]?.Length.ToString() ?? "null"}",
                    nameof(records));
        }

        // Copy the header template and patch the fields the frame depends on, so it is internally
        // consistent whatever template the caller passed.
        var hdr = header.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(HdrPartyHandleOff), partyHandle);
        if (writeCount)
            hdr[HdrCountOff] = (byte)records.Count;
        // 2361 repeats the count at UpdateHdrClientCountOff, the byte its client sizes the record loop from.
        // 2357 and 2359 have no such field and pass clientCountOff = -1.
        if (clientCountOff >= 0)
            hdr[clientCountOff] = (byte)records.Count;

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(msgTag);   // message tag
            p.WriteBytes(hdr);       // fixed header
            foreach (var r in records)
                p.WriteBytes(r);     // 208-byte member records
        });
        return pw.ToArray();
    }

    // ---- record helpers ------------------------------------------------------------------------

    /// <summary>
    /// Builds a 208-byte member record with the identity fields set (actor handle, character GID, name and
    /// guild name). All other bytes are zero.
    /// </summary>
    public static byte[] BuildMemberRecord(uint actorHandle, ulong characterGid,
        string characterName, string guildName = "")
    {
        var rec = new byte[MemberRecordLen];
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(RecActorHandleOff), actorHandle);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(RecGidOff), characterGid);
        WriteFixedWide(rec, RecNameOff, characterName, RecNameMaxChars);
        WriteFixedWide(rec, RecGuildOff, guildName, RecGuildMaxChars);
        return rec;
    }

    /// <summary>
    /// Writes the member state (online flag, profession, ProJob level and id, guild name) into a 208-byte member
    /// record. The guild name buffer is cleared first, so a record never keeps a previous name.
    /// </summary>
    public static void ApplyMemberState(byte[] record, bool online, byte profession,
        byte proJobLevel, byte proJobId, string? guildName)
    {
        if (record is null || record.Length != MemberRecordLen)
            throw new ArgumentException($"member record must be {MemberRecordLen} bytes", nameof(record));
        record[RecOnlineOff] = online ? MemberOnline : MemberLocked;
        record[RecProfessionOff] = profession;
        record[RecProJobLevelOff] = proJobLevel;
        record[RecProJobIdOff] = proJobId;
        record.AsSpan(RecGuildOff, RecGuildMaxChars * 2).Clear();
        WriteFixedWide(record, RecGuildOff, guildName, RecGuildMaxChars);
    }

    /// <summary>
    /// Writes a UTF-16LE string into a fixed-width record buffer, truncated to <paramref name="maxChars"/>
    /// characters, with no length prefix and no terminator.
    /// </summary>
    private static void WriteFixedWide(byte[] rec, int offset, string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return;
        var take = Math.Min(value.Length, maxChars);
        Encoding.Unicode.GetBytes(value.AsSpan(0, take), rec.AsSpan(offset, take * 2));
    }
}
