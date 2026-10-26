using System.Buffers.Binary;
using System.Text;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the guild server-to-client message bodies, and parsers for guild client-to-server requests.
/// </summary>
/// <remarks>
/// <para>
/// Most guild messages are raw bodies with no compact inner header and no message tag. The roster and list
/// answers (2560, 2564, 2566, 2929) carry a leading u32 holding the body length minus 4.
/// </para>
/// <para>
/// Body layouts (offsets are body-relative):
/// </para>
/// <list type="bullet">
/// <item>2560 AnsGuildMemList: [u32 len-4][16 guild GUID][u16 count][count x 115-byte member record].</item>
/// <item>2573 NfyGuildMemberAdd (132 bytes): [u8 1][16 guild GUID][115-byte member record].</item>
/// <item>2571 NfyGuildMemberRemove (77 bytes): [u8 reason][16 guild GUID][8 GID][wchar[22] name][8 GID].</item>
/// <item>
/// 2559 AnsMyGuildInfo: [u32 44+2*nameLen][16 guild GUID][u8 nameLen][name][27 identity fields]
/// [u32 57+2*noticeLen][16 guild GUID][u16 noticeLen][notice][39 notice-object fields]
/// [u8][u32 objLen][objLen character object][152 reserved][compact length n][n x u32 guild skill id].
/// The 27 identity fields are [u16 emblem index][4][16 date record][4][u8 0x80]. The character object is
/// [u16][u16][u8 nameLen][name][u16 n][n x u32][u16 m][m x u8]; the name in it does not identify the guild
/// master.
/// </item>
/// <item>
/// 2929 AnsGuildListRegisteredRobe: [u32 len-4]{[u16 code][25-byte current robe][u16 count][count x 25-byte
/// robe]}, where a robe record is [u32 item id][f64 OLE date][u32 status][u32][u32 colour][u8 0x80].
/// </item>
/// <item>
/// 2674 / 2856 guild storage list: [u16 0][u32 len-6][u64 funds][u16 count][count x 126-byte item record].
/// </item>
/// <item>2675 NfyAddGuildStorage_Item (126 bytes): one item record. 2676 NfyDelGuildStorage_Item (1 byte): [u8 vault slot].</item>
/// <item>2564 NfyGuildNoticeUpdated: [u32 len-4][16 guild GUID][u16 char count][UTF-16LE notice].</item>
/// <item>2644 NfyGuildMemberModifyAuthority (9 bytes): [u8 authority id][8 GID].</item>
/// <item>2645 NfyGuildMasterHandover (18 bytes): [8 new master GID][8 old master GID][u8 0][u8 old master's new rank].</item>
/// </list>
/// </remarks>
public static class GuildPackets
{
    /// <summary>Size of the u32 length prefix that opens the length-prefixed bodies; the value it holds excludes these bytes.</summary>
    private const int LengthPrefixLen = 4;

    /// <summary>Size of the guild GUID field.</summary>
    private const int GuildGuidLen = 16;

    /// <summary>Size of a COleDateTime record: [f64 OLE date][u32 status][u32 high dword of the date].</summary>
    private const int OleDateRecordLen = 16;

    /// <summary>Offset of the trailing u32 of a COleDateTime record, which repeats the high dword of the date.</summary>
    private const int OleDateHighDwordOff = 12;

    /// <summary>Value of the trailing u32 of a null COleDateTime record.</summary>
    private const uint OleDateNullHighDword = 0xFFFFFFFFu;

    // ---------- member record (shared by 2560 list and 2573 add) --------------------------------

    /// <summary>Size of a roster member record.</summary>
    internal const int MemberRecordLen = 115;

    /// <summary>Record offset of the member's GID (u64).</summary>
    private const int MemberRecGidOff = 0;

    /// <summary>Record offset of the member's level (u8).</summary>
    private const int MemberRecLevelOff = 21;

    /// <summary>Record offset of the member's name field: a NUL-terminated UTF-16LE string that runs to the end of the record.</summary>
    private const int MemberRecNameOff = 73;

    /// <summary>The level written for a member whose level is 0.</summary>
    private const byte MemberRecMinLevel = 1;

    /// <summary>
    /// A complete 115-byte member record. Only the GID, the level and the name field are replaced per member;
    /// the rest is the fixed template the client renders as a valid roster row.
    /// </summary>
    /// <remarks>
    /// Layout: [u64 GID][13 bytes zero][u8 level][51 bytes of class, job and date fields kept as the template
    /// holds them][wchar[21] name field with its terminator].
    /// </remarks>
    private static readonly byte[] MemberRecordTemplate = HexToBytes(
        "cb9e73a7b3b6e00d"       // +0   GID
        + "00000000000000000000000000"     // +8   zero
        + "32"   // +21  level
        + "0009000400320000cb310000000000003e3ed276698ee64000000000698ee64009140300000000000000000000000000000000"     // +22  class, job and date fields
        + "44006500610063006f006e0042006c00610063006b000000000000000000000000000000000000000000");  // +73  name field

    private static byte[] BuildMemberRecord(GuildMember m)
    {
        var rec = (byte[])MemberRecordTemplate.Clone();
        if (rec.Length != MemberRecordLen) Array.Resize(ref rec, MemberRecordLen);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(MemberRecGidOff), m.Gid);
        rec[MemberRecLevelOff] = m.Level == 0 ? MemberRecMinLevel : m.Level;
        // Name field: zero it, then write the real name (the client reads it NUL-terminated).
        Array.Clear(rec, MemberRecNameOff, MemberRecordLen - MemberRecNameOff);
        WriteWideInto(rec, MemberRecNameOff, m.Name, (MemberRecordLen - MemberRecNameOff - 2) / 2);
        return rec;
    }

    // ---------- 2560 AnsGuildMemList ------------------------------------------------------------

    public static byte[] BuildAnsGuildMemList(Guild g)
    {
        var members = g.Members;
        using var w = new PacketWriter(32 + members.Count * MemberRecordLen);
        var lenPos = w.Reserve(LengthPrefixLen);   // filled with (body length - 4)
        var start = w.Length;
        w.WriteBytes(Guid16(g));
        w.WriteUInt16((ushort)members.Count);
        foreach (var m in members) w.WriteBytes(BuildMemberRecord(m));
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.GetWrittenSpan().ToArray();
    }

    // ---------- 2573 NfyGuildMemberAdd ----------------------------------------------------------

    /// <summary>The first byte of the 2573 body.</summary>
    private const byte MemberAddLeadByte = 0x01;

    /// <summary>
    /// Builds the 2573 NfyGuildMemberAdd notification: [u8 1][16 guild GUID][115-byte member record]. The body is
    /// 132 bytes.
    /// </summary>
    public static byte[] BuildNfyGuildMemberAdd(Guild g, GuildMember m)
    {
        using var w = new PacketWriter(140);
        w.WriteByte(MemberAddLeadByte);
        w.WriteBytes(Guid16(g));
        w.WriteBytes(BuildMemberRecord(m));
        return w.GetWrittenSpan().ToArray();
    }

    // ---------- 2571 NfyGuildMemberRemove -------------------------------------------------------

    /// <summary>The 2571 reason code for a member who was kicked.</summary>
    public const byte MemberRemoveReasonKicked = 2;

    /// <summary>The 2571 reason code for a member who left.</summary>
    public const byte MemberRemoveReasonLeft = 3;

    private const int MemberRemoveLen = 77;
    private const int MemberRemoveReasonOff = 0;
    private const int MemberRemoveGuidOff = 1;
    private const int MemberRemoveMemberGidOff = 17;
    private const int MemberRemoveNameOff = 25;
    private const int MemberRemoveNameChars = 22;
    private const int MemberRemoveActorGidOff = 69;

    /// <summary>
    /// Builds the 2571 NfyGuildMemberRemove notification: [u8 reason][16 guild GUID][8 removed member's GID]
    /// [wchar[22] name][8 GID of the actor]. The body is 77 bytes.
    /// </summary>
    /// <param name="reason">2 = the member was kicked, 3 = the member left.</param>
    /// <param name="actorGid">Who removed the member: the kicker on a kick, the member themselves on a leave.</param>
    public static byte[] BuildNfyGuildMemberRemove(Guild g, GuildMember m, byte reason, ulong actorGid)
    {
        var body = new byte[MemberRemoveLen];
        body[MemberRemoveReasonOff] = reason;
        Guid16(g).CopyTo(body.AsSpan(MemberRemoveGuidOff, GuildGuidLen));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(MemberRemoveMemberGidOff), m.Gid);
        WriteWideInto(body, MemberRemoveNameOff, m.Name, MemberRemoveNameChars - 1);    // keep the NUL
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(MemberRemoveActorGidOff), actorGid);
        return body;
    }

    // ---------- 2559 AnsMyGuildInfo -------------------------------------------------------------

    /// <summary>The emblem index meaning that the guild has no registered emblem image.</summary>
    private const ushort NoEmblemIndex = 0;

    /// <summary>The longest guild name, in characters, that the 2559 answer carries.</summary>
    private const int GuildNameMaxChars = 24;

    /// <summary>
    /// The 2559 body is a chain of length-prefixed objects. The length of the identity object is
    /// <c>16 + 1 + 2*nameLen + 27</c>, which is this base plus twice the name length.
    /// </summary>
    private const int Info2559Struct1Base = 44;

    /// <summary>
    /// The length of the notice object is <c>16 + 2 + 2*noticeLen + 39</c>, which is this base plus twice the
    /// notice length.
    /// </summary>
    private const int Info2559Struct2Base = 57;

    /// <summary>Value of the flag byte that ends the identity fields of the 2559 answer.</summary>
    private const byte Info2559IdentityFlag = 0x80;

    /// <summary>Size of the three fields between the emblem index and the flag byte: [4][16 date record][4].</summary>
    private const int Info2559IdentityFieldsLen = 24;

    /// <summary>Status dword of the date record in the notice-object fields of a guildless answer: the null-date status.</summary>
    private const byte Info2559NullDateStatus = 0x02;

    /// <summary>Length of the character object of a guildless answer: [u16][u16][u8 0][u16 0][u16 0].</summary>
    private const int Info2559EmptyCharObjectLen = 9;

    /// <summary>Size of the reserved block before the guild skill list.</summary>
    private const int Info2559ReservedLen = 152;

    /// <summary>
    /// The identity fields that follow the emblem index in a guild's 2559 answer, in order: a 4-byte field, a
    /// 16-byte date record, a 4-byte field, and the 0x80 flag byte. 25 bytes.
    /// </summary>
    private static readonly byte[] Info2559MidTail = HexToBytes(
        "25f6c100"                           // 4-byte field
        + "823899644b88e640" + "00000000" + "4b88e640"   // date record {f64, status 0, high dword}
        + "146ff0ff"                         // 4-byte field
        + "80");                             // flag byte (Info2559IdentityFlag)

    /// <summary>
    /// Everything that follows the notice in a guild's 2559 answer, as a fixed template: the 39 notice-object
    /// fields, the u8 before the character object, the character object with its length, the 152 reserved
    /// bytes, and the empty guild skill list. 223 bytes.
    /// </summary>
    /// <remarks>
    /// The character object holds a placeholder character name. The 152 reserved bytes are not read by the
    /// client.
    /// </remarks>
    private static readonly byte[] Info2559Tail = HexToBytes(
        // Notice-object fields (39 bytes).
        "00000000"                                       // empty nested blob (u32 length 0)
        + "8704830000000000"                             // 8-byte field
        + "258f9322cb77e640" + "00000000" + "cb77e640"   // date record {f64, status 0, high dword}
        + "170100"                                       // three 1-byte fields
        + "05000000" + "32000000"                        // two 4-byte fields
        // The u8 between the notice object and the character object.
        + "03"
        // Character object: u32 length 25, then [u16][u16][u8 nameLen][name][u16 0][u16 0].
        + "19000000"
        + "25000300" + "08" + "6c005300700065006500640079006c00" + "00000000"
        // Reserved block (152 bytes).
        + "00000000000000000000000000000000000000000000000000000000000000000000000000000000948dfe510000000060e69b0f0000000000000000f67f000058489aeff67f00001d6054eff67f000060e69b0f000000005e8762eff67f0000e0047c1500000000e8047c150000000008fcf31b000000000000000000000000000000000000000000000000000000000000000000000000"
        // Guild skill list: compact length 0.
        + "0100");

    // ---------- notice ----------

    /// <summary>
    /// A guild notice ends with this private-use character, which the client expects as the last character of
    /// every notice of a real guild. A guild without a notice sends exactly this one character.
    /// </summary>
    private const string NoticeTerminator = "";

    /// <summary>The 2929 reason code for a viewer who is not the guild master: there is no robe list to show.</summary>
    internal const ushort RobeCodeNotMaster = 0x13;

    /// <summary>The 2929 code for the guild master, who may list robes.</summary>
    private const ushort RobeCodeMaster = 0;

    /// <summary>Container id the client shows guild-vault items in.</summary>
    private const byte GuildStorageBag = 0x65;

    /// <summary>
    /// The emblem index written into the 2559 answer, read from the guild emblem registry that also answers
    /// 2612. 0 means the guild has no emblem.
    /// </summary>
    /// <remarks>
    /// The caller must have run <c>IGuildEmblemRepository.InitAsync</c>.
    /// </remarks>
    private static ushort EmblemIndexOf(Guild g, IGuildEmblemRepository emblems) => emblems.IndexOf(Guid16(g));

    public static byte[] BuildAnsMyGuildInfo(Guild g, IGuildEmblemRepository emblems)
    {
        var name = g.Name ?? string.Empty;
        if (name.Length > GuildNameMaxChars) name = name.Substring(0, GuildNameMaxChars);
        var notice = g.Notice ?? string.Empty;
        // The notice ends with the private-use terminator.
        var noticeChars = notice + NoticeTerminator;
        using var w = new PacketWriter(96 + name.Length * 2 + noticeChars.Length * 2 + Info2559Tail.Length);
        w.WriteUInt32((uint)(Info2559Struct1Base + name.Length * 2));
        w.WriteBytes(Guid16(g));
        w.WriteByte((byte)name.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(name));
        w.WriteUInt16(EmblemIndexOf(g, emblems));          // emblem index, must agree with 2612
        w.WriteBytes(Info2559MidTail);                     // remaining identity fields (25 bytes)
        w.WriteUInt32((uint)(Info2559Struct2Base + noticeChars.Length * 2));
        w.WriteBytes(Guid16(g));
        w.WriteUInt16((ushort)noticeChars.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(noticeChars));
        w.WriteBytes(Info2559Tail);
        return w.GetWrittenSpan().ToArray();
    }

    /// <summary>
    /// Builds the 2559 answer for a character that is in no guild. It has the same six top-level fields as a
    /// real guild, all empty.
    /// </summary>
    /// <remarks>
    /// Not built with <see cref="BuildAnsMyGuildInfo"/> for an empty guild: that would append the notice
    /// terminator and a populated third object.
    /// </remarks>
    public static byte[] BuildAnsMyGuildInfoNoGuild()
    {
        using var w = new PacketWriter(288);

        // Identity object: zero GUID, empty name, then the identity fields.
        w.WriteUInt32((uint)Info2559Struct1Base);  // 44 + 2*0
        w.WriteZeros(GuildGuidLen);                // guild GUID, all zero
        w.WriteByte(0);                            // nameLen = 0
        w.WriteUInt16(NoEmblemIndex);              // emblem index; 2612 agrees
        w.WriteZeros(Info2559IdentityFieldsLen);   // [4][16 date record][4], all zero
        w.WriteByte(Info2559IdentityFlag);

        // Notice object: empty and unterminated (a real guild with no notice still sends the terminator).
        w.WriteUInt32((uint)Info2559Struct2Base);  // 57 + 2*0
        w.WriteZeros(GuildGuidLen);                // guild GUID again, all zero
        w.WriteUInt16(0);                          // noticeLen = 0
        w.WriteUInt32(0);                          // nested blob, empty
        w.WriteZeros(8);                           // 8-byte field
        w.WriteZeros(8);                           // date record: f64 date = 0
        w.WriteByte(Info2559NullDateStatus);       // date record: status dword (low byte)
        w.WriteZeros(7);                           // date record: status dword (upper 3 bytes) and high dword
        w.WriteZeros(3);                           // three 1-byte fields
        w.WriteZeros(8);                           // two 4-byte fields

        w.WriteByte(0);                            // the u8 between the notice object and the character object

        // Character object: no name, both lists empty.
        w.WriteUInt32(Info2559EmptyCharObjectLen);
        w.WriteUInt16(0);                          // u16
        w.WriteUInt16(0);                          // u16
        w.WriteByte(0);                            // name length = 0
        w.WriteUInt16(0);                          // count of the u32 list
        w.WriteUInt16(0);                          // count of the u8 list

        w.WriteZeros(Info2559ReservedLen);         // reserved block
        w.WriteCompactLength(0);                   // guild skill list, 0 entries
        return w.GetWrittenSpan().ToArray();
    }

    // ---------- 2929 AnsGuildListRegisteredRobe -------------------------------------------------

    /// <summary>
    /// Builds the 2929 answer for a guild member who is not the guild master: an empty robe list with code 0x13,
    /// meaning there is no robe list to show for this viewer.
    /// </summary>
    public static byte[] BuildAnsGuildListRegisteredRobeEmpty() => (byte[])Robe2929Empty.Clone();

    /// <summary>
    /// The 33-byte not-master answer: [u32 29][u16 0x13 (RobeCodeNotMaster)][25-byte empty current robe]
    /// [u16 0 robes].
    /// </summary>
    private static readonly byte[] Robe2929Empty = HexToBytes(
        "1d000000"                                  // u32 length 29 (body length - 4)
        + "1300"                                    // code RobeCodeNotMaster
        + "00000000" + "0000000000000000"           // item id 0, OLE date 0
        + "00000000" + "000041fa"                   // status 0, 4-byte field
        + "00000000" + "80"                         // colour 0, flag 0x80
        + "0000");                                  // robe count 0

    /// <summary>
    /// Builds the 2929 answer for the guild master of a guild with no registered robe: code 0, the Basic Robe as
    /// the current robe, and a one-entry list holding the same record.
    /// </summary>
    /// <remarks>
    /// This server has no robe purchase, selection or recolouring, so every guild is in this default state.
    /// The body is [u32 0x36][u16 0][25-byte Basic Robe][u16 1][25-byte Basic Robe], 58 bytes.
    /// </remarks>
    public static byte[] BuildAnsGuildListRegisteredRobeBasic()
    {
        // The same record the guild's 2559 and the PC-record guild block carry as the current robe, so the robe
        // window and the guild panel never disagree about what is worn.
        var record = GuildRobeRecord.InfoForm();
        using var w = new PacketWriter(8 + 2 * record.Length);
        var lenPos = w.Reserve(LengthPrefixLen);   // body length - 4 (0x36 here)
        var start = w.Length;
        w.WriteUInt16(RobeCodeMaster);             // the master may list robes
        w.WriteBytes(record);                      // current robe = list[0]
        w.WriteUInt16(1);                          // one registered robe: the Basic Robe
        w.WriteBytes(record);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    // ---------- guild storage list 2674 / 2856 --------------------------------------------------

    /// <summary>Size of the storage list header: [u16 flags][u32 length][u64 funds][u16 count].</summary>
    private const int StorageListHeaderLen = 16;

    /// <summary>Offset where the storage list's length field ends; the length counts the bytes after it.</summary>
    private const int StorageListLenFieldEnd = 6;

    /// <summary>The flags word of the storage list.</summary>
    private const ushort StorageListFlags = 0;

    public static byte[] BuildGuildStorageList(Guild g)
    {
        var items = g.Storage.OrderBy(s => s.StorageSlot).ToList();
        using var w = new PacketWriter(StorageListHeaderLen + items.Count * ItemPackets.ItemRecordLength);
        w.WriteUInt16(StorageListFlags);                           // [0..1] flags
        var remaining = StorageListHeaderLen - StorageListLenFieldEnd + items.Count * ItemPackets.ItemRecordLength; // [2..5] = total body length - 6
        w.WriteUInt32((uint)remaining);
        w.WriteUInt64((ulong)Math.Max(0, g.Gold));                 // [6..13] guild funds
        w.WriteUInt16((ushort)items.Count);                        // [14..15] item count
        foreach (var it in items) w.WriteBytes(BuildGuildStorageItemRecord(it));
        return w.GetWrittenSpan().ToArray();
    }

    // ---------- 2675 NfyAddGuildStorage_Item / 2676 NfyDelGuildStorage_Item ---------------------

    public static byte[] BuildNfyAddGuildStorageItem(GuildStorageItem it) => BuildGuildStorageItemRecord(it);

    public static byte[] BuildNfyDelGuildStorageItem(byte vaultSlot) => new[] { vaultSlot };

    // ---------- 2564 NfyGuildNoticeUpdated ------------------------------------------------------

    /// <summary>
    /// Builds the 2564 notice update: [u32 len-4][16 guild GUID][u16 char count][UTF-16LE notice]. The char
    /// count includes the notice terminator.
    /// </summary>
    public static byte[] BuildNfyGuildNoticeUpdated(Guild g)
    {
        var noticeChars = (g.Notice ?? string.Empty) + NoticeTerminator;
        using var w = new PacketWriter(32 + noticeChars.Length * 2);
        var lenPos = w.Reserve(LengthPrefixLen);
        var start = w.Length;
        w.WriteBytes(Guid16(g));
        w.WriteUInt16((ushort)noticeChars.Length);                 // char count, terminator included
        w.WriteBytes(Encoding.Unicode.GetBytes(noticeChars));
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.GetWrittenSpan().ToArray();
    }

    // ---------- 2644 NfyGuildMemberModifyAuthority ----------------------------------------------

    private const int ModifyAuthorityLen = 9;
    private const int ModifyAuthorityIdOff = 0;
    private const int ModifyAuthorityGidOff = 1;

    public static byte[] BuildNfyGuildMemberModifyAuthority(ulong gid, byte authId)
    {
        var body = new byte[ModifyAuthorityLen];
        body[ModifyAuthorityIdOff] = authId;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(ModifyAuthorityGidOff), gid);
        return body;
    }

    // ---------- 2645 NfyGuildMasterHandover -----------------------------------------------------

    private const int HandoverLen = 18;
    private const int HandoverNewMasterGidOff = 0;
    private const int HandoverOldMasterGidOff = 8;
    private const int HandoverMasterRankOff = 16;
    private const int HandoverOldMasterRankOff = 17;

    /// <summary>
    /// Builds the 2645 NfyGuildMasterHandover notification: [8 new master GID][8 old master GID]
    /// [u8 master rank index][u8 old master's new rank].
    /// </summary>
    public static byte[] BuildNfyGuildMasterHandover(ulong newMasterGid, ulong oldMasterGid, byte oldMasterRank)
    {
        var body = new byte[HandoverLen];
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(HandoverNewMasterGidOff), newMasterGid);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(HandoverOldMasterGidOff), oldMasterGid);
        body[HandoverMasterRankOff] = GuildService.MasterRankIndex;
        body[HandoverOldMasterRankOff] = oldMasterRank;
        return body;
    }

    // ---------- 2565 AnsGuildInviteMember -------------------------------------------------------

    /// <summary>Fixed length of the 2565 body.</summary>
    private const int InviteAnsLen = 62;
    private const int InviteAnsNameOff = 2;         // wchar[22] invitee name
    private const int InviteAnsNameChars = 22;
    private const int InviteAnsTailOff = 46;        // 16-byte date record

    /// <summary>
    /// Builds the 2565 AnsGuildInviteMember answer to a guild invite request: [u16 result][wchar[22] invitee name]
    /// [16-byte date tail]. The body is 62 bytes.
    /// </summary>
    /// <remarks>
    /// The tail holds an OLE date for the end of the rejoin cooldown when the result is 73, and is marked null
    /// otherwise.
    /// </remarks>
    /// <param name="rejoinAtLocal">With result 73, the end of the invitee's rejoin cooldown in server local time; null for every other result.</param>
    public static byte[] BuildAnsGuildInviteMember(ushort result, string inviteeName, DateTime? rejoinAtLocal)
    {
        var body = new byte[InviteAnsLen];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), result);
        WriteWideInto(body, InviteAnsNameOff, inviteeName, InviteAnsNameChars - 1);   // keep the NUL
        var tail = body.AsSpan(InviteAnsTailOff, OleDateRecordLen);
        if (rejoinAtLocal is { } when)
        {
            var d = when.ToOADate();
            BinaryPrimitives.WriteDoubleLittleEndian(tail, d);
            BinaryPrimitives.WriteUInt32LittleEndian(tail.Slice(OleDateHighDwordOff), (uint)(BitConverter.DoubleToInt64Bits(d) >> 32));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tail.Slice(OleDateHighDwordOff), OleDateNullHighDword);
        }
        return body;
    }

    // ---------- 2566 NfyGuildInviteYouReq -------------------------------------------------------

    private const int InviteNfyLen = 226;
    private const int InviteNfyGuidOff = 4;
    private const int InviteNfyTokenOff = 20;
    private const int InviteNfyTokenLen = 16;
    private const int InviteNfyGuildNameOff = 36;      // wchar[50]
    private const int InviteNfyGuildNameChars = 50;
    private const int InviteNfyInviterOff = 136;       // wchar[22]
    private const int InviteNfyInviteeOff = 180;       // wchar[22]
    private const int InviteNfyPersonNameChars = 22;

    /// <summary>
    /// Builds the 2566 NfyGuildInviteYouReq notification sent to the invitee: [u32 222][16-byte guild GUID]
    /// [16-byte invite token][wchar[50] guild name][wchar[22] inviter][wchar[22] invitee][u16 0]. The body is
    /// 226 bytes.
    /// </summary>
    /// <remarks>
    /// The token is fresh for each invite. Every name field is NUL-terminated inside a zeroed buffer.
    /// </remarks>
    public static byte[] BuildNfyGuildInviteYouReq(Guild g, byte[] inviteToken, string inviterName, string inviteeName)
    {
        var body = new byte[InviteNfyLen];
        BinaryPrimitives.WriteUInt32LittleEndian(body, InviteNfyLen - LengthPrefixLen);               // length prefix: 222
        Guid16(g).CopyTo(body.AsSpan(InviteNfyGuidOff, GuildGuidLen));                                 // guild GUID
        inviteToken.AsSpan(0, InviteNfyTokenLen).CopyTo(body.AsSpan(InviteNfyTokenOff, InviteNfyTokenLen)); // per-invite token
        WriteWideInto(body, InviteNfyGuildNameOff, g.Name, InviteNfyGuildNameChars - 1);           // guild name
        WriteWideInto(body, InviteNfyInviterOff, inviterName, InviteNfyPersonNameChars - 1);      // inviter
        WriteWideInto(body, InviteNfyInviteeOff, inviteeName, InviteNfyPersonNameChars - 1);      // invitee
        // The last two bytes stay 0: an empty trailing string.
        return body;
    }

    // ---------- guild-vault item record ---------------------------------------------------------

    /// <summary>
    /// The inventory storage type the vault item is staged with while the standard record is built (the bank
    /// layout). The container and slot bytes it produces are overwritten with the guild-vault values.
    /// </summary>
    private const byte VaultStagingStorageType = 2;

    /// <summary>Item-record offset of the container byte.</summary>
    private const int ItemRecContainerOff = 0x0F;

    /// <summary>Item-record offset of the slot byte.</summary>
    private const int ItemRecSlotOff = 0x10;

    /// <summary>
    /// Builds the 126-byte item record for a guild vault slot: the standard item record with the container set
    /// to the guild vault and the slot set to the vault slot.
    /// </summary>
    private static byte[] BuildGuildStorageItemRecord(GuildStorageItem it)
    {
        var transient = new InventoryItem
        {
            Id = it.Uid, ItemId = it.ItemId, Count = it.Count <= 0 ? 1 : it.Count,
            Refine = it.Refine, Durability = it.Durability, CardsJson = it.CardsJson,
            Element = it.Element, DyeColor = it.DyeColor, SocketPunchersJson = it.SocketPunchersJson,
            DyeColor2 = it.DyeColor2, DyeColor3 = it.DyeColor3, FxEnchantIndex = it.FxEnchantIndex,
            EnchantReinforceId = it.EnchantReinforceId, EnchantSeconds = it.EnchantSeconds, Bound = it.Bound,
            AbilityValue = it.AbilityValue,
            StorageType = VaultStagingStorageType, Slot = it.StorageSlot,
        };
        var rec = ItemPackets.BuildItemRecord126(transient);
        if (rec.Length != ItemPackets.ItemRecordLength) Array.Resize(ref rec, ItemPackets.ItemRecordLength);
        rec[ItemRecContainerOff] = GuildStorageBag;        // container = guild vault
        rec[ItemRecSlotOff] = it.StorageSlot;              // slot
        return rec;
    }

    // ---------- broadcast helper ----------------------------------------------------------------

    /// <summary>
    /// Sends one body to every online member of the guild, optionally skipping one character. Offline members
    /// are skipped and receive their state at next login.
    /// </summary>
    public static async Task BroadcastToGuildAsync(
        Guild g, PlayerStateManager pm, BroadcastService bc, ushort opcode, byte[] body, uint? exceptCharId = null)
    {
        // Snapshot the member char ids so a concurrent roster edit can't break enumeration.
        uint[] charIds;
        lock (g.Gate) charIds = g.Members.Select(m => m.CharId).ToArray();
        foreach (var cid in charIds)
        {
            if (exceptCharId.HasValue && cid == exceptCharId.Value) continue;
            var p = pm.GetPlayerByCharacterId(cid);
            if (p == null) continue;
            var sess = bc.GetSession(p.SessionId);
            if (sess == null) continue;
            await sess.SendRmiAsync(opcode, (ReadOnlyMemory<byte>)body);
        }
    }

    // ---------- C2S parsing helpers -------------------------------------------------------------

    /// <summary>Default maximum length, in characters, of a name field read by <see cref="ReadWideNameAt"/>.</summary>
    private const int DefaultNameFieldChars = 24;

    /// <summary>Read a fixed-field, null-terminated UTF-16LE name starting at <paramref name="off"/>.</summary>
    public static string ReadWideNameAt(ReadOnlySpan<byte> body, int off, int maxChars = DefaultNameFieldChars)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < maxChars; i++)
        {
            int p = off + i * 2;
            if (p + 1 >= body.Length) break;
            char c = (char)(body[p] | (body[p + 1] << 8));
            if (c == '\0') break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------- internals -----------------------------------------------------------------------

    private static byte[] Guid16(Guild g)
    {
        if (g.Guid16 != null && g.Guid16.Length == GuildGuidLen) return g.Guid16;
        var b = new byte[GuildGuidLen];
        if (g.Guid16 != null) Array.Copy(g.Guid16, b, Math.Min(GuildGuidLen, g.Guid16.Length));
        return b;
    }

    private static void WriteWideInto(byte[] buf, int off, string s, int maxChars)
    {
        s ??= string.Empty;
        int n = Math.Min(s.Length, maxChars);
        for (int i = 0; i < n; i++)
        {
            int p = off + i * 2;
            if (p + 1 >= buf.Length) break;
            buf[p] = (byte)(s[i] & 0xFF);
            buf[p + 1] = (byte)((s[i] >> 8) & 0xFF);
        }
    }

    private static byte[] HexToBytes(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }
}
