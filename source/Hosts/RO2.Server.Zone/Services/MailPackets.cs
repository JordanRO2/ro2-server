using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using RO2.Domain.Entities;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire serialization for the mail RMI family (2444 to 2449, 2617, and their request parsers). Enveloped messages use the
/// compact-length byte array envelope with their fixed message tag, so the first body byte is always a valid marker.
/// </summary>
/// <remarks>
/// Message tags and lengths: 2444 OnMailList (tag 0xE353), 2445 OnMailSend (0xE35A, blob 29 / body 31), 2446 OnMailRead
/// (raw u32 length, no tag), 2447 OnMailGetItem (0xE358, blob 145 / body 148), 2448 OnMailDelete (0xE356, blob 19 /
/// body 21), 2449 OnMailNew (0xE35B), 2617 OnMailGetMoney (0xE35D, blob 27 / body 29).
/// The mailbox owner is the receiver's actor handle, written at blob+4 of every message and echoed by the client at
/// blob+4 of every request; the 8-byte mail uid (blob+8 of the requests) is the key of each request.
/// Offsets written "blob+N" are relative to the blob that starts at the u16 self length, right after the compact-length
/// prefix; the self length is at blob+0 and the msgTag at blob+2.
/// </remarks>
public static class MailPackets
{
    // ----- msgTags (little-endian u16 written at blob+2) -----

    /// <summary>msgTag of 2444 OnMailList: 0xE353 = 58195.</summary>
    public const ushort MsgTagList     = 0xE353;

    /// <summary>msgTag of 2445 OnMailSend: 0xE35A = 58202.</summary>
    public const ushort MsgTagSend     = 0xE35A;

    /// <summary>msgTag of 2447 OnMailGetItem: 0xE358 = 58200.</summary>
    public const ushort MsgTagGetItem  = 0xE358;

    /// <summary>msgTag of 2448 OnMailDelete: 0xE356 = 58198.</summary>
    public const ushort MsgTagDelete   = 0xE356;

    /// <summary>msgTag of 2449 OnMailNew: 0xE35B = 58203. Same frame shape as the list header (tag, owner, u16 count).</summary>
    public const ushort MsgTagNew      = 0xE35B;

    /// <summary>msgTag of 2617 OnMailGetMoney: 0xE35D = 58205.</summary>
    public const ushort MsgTagGetMoney = 0xE35D;

    // ----- Record sizes -----

    /// <summary>Size of one mail row in the mail list (2444): 146 bytes.</summary>
    public const int ListRecordLength = 146;

    /// <summary>Size of the item record embedded in mail messages (uid@0, itemId@8, durability@12): 126 bytes.</summary>
    public const int ItemRecordLength = 126;

    /// <summary>Offset in an item record of the bag container byte.</summary>
    private const int ItemRecordContainerOffset = 15;

    /// <summary>Offset in an item record of the bag slot byte.</summary>
    private const int ItemRecordSlotOffset = 16;

    /// <summary>
    /// Container byte (item record +15) of a mail attachment that is still in the mail: 102, with slot 0. The matching
    /// 2447 record carries the item's real bag container 1..4. The client skips any container above 4 when it places
    /// an item in the bag, so the attachment preview can never land in (or clear) a bag cell.
    /// </summary>
    public const byte MailAttachmentContainer = 102;

    /// <summary>
    /// Container byte (item record +15) of the item an auction mail carries (<see cref="AuctionMailKind"/>: 2 expired,
    /// 7 cancelled, 11 purchased), in place of <see cref="MailAttachmentContainer"/>. Also above 4, so the bag placement
    /// skips it too.
    /// </summary>
    public const byte AuctionAttachmentContainer = 103;

    /// <summary>Size of a mail date: an OLE date double, 4 zero bytes, then the double's high dword.</summary>
    private const int MailDateBytes = 16;

    /// <summary>Zero tail after the date double of an unset mail date (<see cref="MailDateBytes"/> minus the 8-byte double).</summary>
    private const int UnsetMailDateTailBytes = MailDateBytes - sizeof(double);

    // ----- Fixed UTF-16 field sizes (a wchar[21] is 42 bytes: 20 characters plus the terminating NUL) -----

    /// <summary>Sender name in a list row: wchar[21] at row +62.</summary>
    private const int SenderNameBytes = 42;

    /// <summary>Title in a list row: wchar[21] at row +104..145, the end of the 146-byte row.</summary>
    private const int TitleBytes      = 42;

    /// <summary>Recipient name in a 1393 ReqMailSend: wchar[21] at blob+8.</summary>
    private const int SendRecipBytes  = 42;

    /// <summary>Title in a 1393 ReqMailSend: wchar[21] at blob+50..91. The zeny field starts right after it at blob+92.</summary>
    private const int SendTitleBytes  = 42;

    // ----- 1393 ReqMailSend layout (blob-relative) -----
    // [u16 selfLen@0][u16 tag@2][u32 owner@4][wchar[21] recipient@8][wchar[21] title@50][u64 zeny@92]
    // [u64 itemUid@100][u8 itemSlot@108][u8 itemContainer@109][u8 itemCount@110][u16 bodyChars@111][wchar body@113]

    /// <summary>Fixed part of a 1393 ReqMailSend: everything up to the body text at blob+113.</summary>
    private const int SendFixedBytes     = 113;

    private const int SendRecipOffset    = 8;
    private const int SendTitleOffset    = 50;
    private const int SendGoldOffset     = 92;
    private const int SendItemUidOffset  = 100;
    private const int SendItemCountOffset = 110;
    private const int SendBodyLenOffset  = 111;

    // ----- Shared request key (1396, 1397, 1398, 1457): [owner u32@4][mail uid u64@8] -----

    private const int OwnerOffset        = 4;
    private const int MailRefUidOffset   = 8;
    private const int MailRefLength      = 16;

    /// <summary>Result code 0 of 2445, 2448 and 2617: success.</summary>
    private const ushort ResultSuccess = 0;

    // ----- 2445 OnMailSend result codes (blob+8) and postage -----
    // The client shows one message from its string table per non-zero code (16201 + n shows string_ui 2300 + n,
    // n = 0..8) and returns before it touches the wallet or the bag.

    /// <summary>Result code 16201 (string_ui 2300 "Mailbox System Error").</summary>
    public const ushort SendSystemError = 16201;

    /// <summary>Result code 16202 (string_ui 2301 "Can't find the target player."): the recipient was not found.</summary>
    public const ushort SendTargetNotFound = 16202;

    /// <summary>Result code 16204 (string_ui 2303 "Mailbox is full."): the recipient's mailbox is full.</summary>
    public const ushort SendMailboxFull = 16204;

    /// <summary>
    /// Number of mails a player-to-player send is refused at, because the recipient's mailbox is full. Counts the whole
    /// mailbox, not only unread mails. System and auction mail are not limited. Not a table value.
    /// </summary>
    public const int MailboxPlayerSendLimit = 31;

    /// <summary>Result code 16208 (string_ui 2307 "Item is empty."): the attached item is not in the sender's bag.</summary>
    public const ushort SendItemEmpty = 16208;

    /// <summary>Result code 16209 (string_ui 2308 "You don't have enough Zeny."): the sender cannot pay.</summary>
    public const ushort SendNotEnoughZeny = 16209;

    /// <summary>
    /// Zeny charged on top of the attached zeny for each successful send: the 2445 wallet value is the previous wallet
    /// minus the attached zeny minus this postage. Not a table value. A refused send costs nothing.
    /// </summary>
    public const long PostageZeny = 10;

    // ----- S2C builders -----

    /// <summary>
    /// Builds the 2444 OnMailList message: a fixed header with the owner and the row count, followed by the mail rows. An empty
    /// list gives the empty message (body 12).
    /// </summary>
    public static byte[] BuildMailList2444(uint ownerHandle, IReadOnlyList<byte[]> records)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagList);              // blob+2
            p.WriteUInt32(ownerHandle);             // blob+4  mailbox owner handle
            p.WriteUInt16((ushort)records.Count);   // blob+8  mail count
            foreach (var rec in records)            // blob+10 rows (146 bytes each)
                p.WriteBytes(rec);
        });
        return pw.ToArray();
    }

    /// <summary>Builds the 2449 OnMailNew message (body 12), which carries the receiver's unread mail count.</summary>
    public static byte[] BuildMailNew2449(uint ownerHandle, ushort count)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagNew);
            p.WriteUInt32(ownerHandle);
            p.WriteUInt16(count);
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Builds the 2445 OnMailSend response (blob 29): result @8, the sender's zeny after the send @10, the attached item's
    /// uid @18, and the slot @26, container @27 and stack count left @28 that the item keeps in the sender's bag.
    /// The client writes the zeny into its wallet, looks the uid up in the bag and, if found, re-places it with the stack
    /// count at @28 (0 clears the cell). A mail with no item carries 0 in the uid, slot and container.
    /// </summary>
    public static byte[] BuildMailSend2445(
        uint ownerHandle, long zenyAfter, ulong itemUid, byte itemSlot, byte itemContainer, byte itemCountLeft)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSend);                   // blob+2
            p.WriteUInt32(ownerHandle);                  // blob+4
            p.WriteUInt16(ResultSuccess);                // blob+8  result: sent
            p.WriteUInt64((ulong)Math.Max(0, zenyAfter)); // blob+10 sender wallet after postage + zeny
            p.WriteUInt64(itemUid);                      // blob+18 mailed item uid (0 = no item)
            p.WriteByte(itemSlot);                       // blob+26 its slot
            p.WriteByte(itemContainer);                  // blob+27 its container
            p.WriteByte(itemCountLeft);                  // blob+28 stack count left in that cell
        });
        return pw.ToArray();
    }

    /// <summary>Zero bytes after the result in a refused 2445 (blob+10..28).</summary>
    private const int SendFailedZeroBytes = 19;

    /// <summary>
    /// Builds the 2445 OnMailSend failure response: the result code and 19 zero bytes (body 31). The client stops at the
    /// result, so the wallet and bag stay as they were; the server must not have charged or moved anything either.
    /// </summary>
    public static byte[] BuildMailSendFailed2445(uint ownerHandle, ushort result)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSend);   // blob+2
            p.WriteUInt32(ownerHandle);  // blob+4
            p.WriteUInt16(result);       // blob+8
            p.WriteZeros(SendFailedZeroBytes); // blob+10..28
        });
        return pw.ToArray();
    }

    // ----- 2446 OnMailRead -----
    // Raw u32-length family (no compact envelope, no msgTag): [u32 innerLen][record]. Record layout:
    // [u32 owner@0][u16 result@4][u64 uid@6][u8 read@14][16B expiry@15][16B sent@31][126B item@47]
    // [u64 auction price@173][WStr16 body@181]. Fixed part 181 bytes.

    /// <summary>Result code 0 of a 2446 that found the mail (record+4).</summary>
    public const ushort ReadOk = 0;

    /// <summary>
    /// Result code 16203 (string_ui 2302 "Invalid mail.", the same code as <see cref="GetItemInvalidMail"/>) at record+4,
    /// answering a read whose uid the server did not resolve (see <see cref="BuildMailReadFailed2446"/>).
    /// </summary>
    public const ushort ReadInvalidMail = 16203;

    /// <summary>
    /// Builds the 2446 OnMailRead message with the full mail content: owner, result, uid, read flag, expiry and sent dates,
    /// attached item record, auction price and body text.
    /// </summary>
    /// <remarks>
    /// Dates are written as 16-byte mail dates (see <see cref="WriteMailDate"/>), expiry first and sent second, and the
    /// body is a WStr16 string. <paramref name="auctionPrice"/> (record+173) is not the mail's zeny: it is the instant
    /// price of the article an auction mail is about (<see cref="MailMessage.AuctionPrice"/>), 0 for any other mail.
    /// </remarks>
    public static byte[] BuildMailRead2446(
        uint ownerHandle, ulong mailUid, bool isRead,
        DateTime sentAt, long auctionPrice, ReadOnlySpan<byte> item126, string body)
    {
        // Build the inner record first so its exact length can prefix it.
        using var rec = new PacketWriter(256);
        rec.WriteUInt32(ownerHandle);          // record @0   owner handle
        rec.WriteUInt16(ReadOk);               // record @4   result
        rec.WriteUInt64(mailUid);              // record @6   uid
        rec.WriteByte((byte)(isRead ? 1 : 0)); // record @14  read flag
        // Same order as the list row: expiry first, sent second; expiry is sent + MailLifetimeDays.
        WriteMailDate(rec, sentAt.AddDays(MailLifetimeDays)); // record @15 expiry (16)
        WriteMailDate(rec, sentAt);            // record @31  sent (16)
        WriteItemRecord(rec, item126);         // record @47  item (126)
        rec.WriteUInt64((ulong)Math.Max(0, auctionPrice)); // record @173 auction instant price (0 = not an auction mail)
        WriteWStr16(rec, body);                // record @181 body text

        return WithInnerLength(rec.ToArray());
    }

    /// <summary>
    /// Builds the 2446 response for a mail uid the server does not resolve: owner = the current handle,
    /// <paramref name="result"/> at record+4 (16203), uid 0, read 0, both dates the OLE date of time_t 0 in local time
    /// with a zero tail, a zero item record, price 0 and an empty WStr16 body (187 bytes). With uid 0 the client finds no
    /// mail to update, so the answer changes nothing on screen.
    /// </summary>
    public static byte[] BuildMailReadFailed2446(uint ownerHandle, ushort result)
    {
        using var rec = new PacketWriter(192);
        rec.WriteUInt32(ownerHandle);     // record @0
        rec.WriteUInt16(result);          // record @4   result
        rec.WriteUInt64(0);               // record @6   uid
        rec.WriteByte(0);                 // record @14  read flag
        WriteUnsetMailDate(rec);          // record @15  expiry
        WriteUnsetMailDate(rec);          // record @31  sent
        rec.WriteZeros(ItemRecordLength); // record @47  item
        rec.WriteUInt64(0);               // record @173 price
        WriteWStr16(rec, string.Empty);   // record @181 body
        return WithInnerLength(rec.ToArray());
    }

    private static byte[] WithInnerLength(byte[] record)
    {
        using var pw = new PacketWriter(record.Length + 8);
        pw.WriteUInt32((uint)record.Length);   // body @0 innerLen
        pw.WriteBytes(record);
        return pw.ToArray();
    }

    // ----- 2447 OnMailGetItem result codes (blob+8) and dispositions (blob+144) -----
    // The client shows one message from its string table per non-zero result and returns before it touches the
    // record or the disposition.

    /// <summary>Result code 16203 (string_ui 2302 "Invalid mail."): the mail's item is already gone, for example when it was claimed before.</summary>
    public const ushort GetItemInvalidMail = 16203;

    /// <summary>Result code 16207 (string_ui 2306 "There is no space to store an item."): no bag space for the claimed item.</summary>
    public const ushort GetItemNoSpace = 16207;

    /// <summary>Result code 16201 (string_ui 2300 "Mailbox System Error"): used only when the inventory store cannot be reached.</summary>
    public const ushort GetItemSystemError = 16201;

    /// <summary>Disposition (blob+144) of a successful claim where the mail stays in the mailbox.</summary>
    public const byte GetItemKeepMail = 1;

    /// <summary>
    /// Disposition of a claim after which the mail is removed (2447 blob+144, 2617 blob+26): the client then deletes the
    /// mail itself. Sent for every successful auction-mail claim, exactly the mails
    /// <see cref="MailService.ClaimRemovesMail"/> removes from the store.
    /// </summary>
    public const byte ClaimDeletesMail = 2;

    /// <summary>Disposition (blob+144) of a failed claim, which also carries an all-zero item record.</summary>
    public const byte GetItemFailedDisposition = 0;

    /// <summary>
    /// Builds the 2447 OnMailGetItem response (blob 145): result @8, mail uid @10, the 126-byte item record @18 and the
    /// disposition @144. A non-zero result goes to the client's message box and nothing else is read. On success the
    /// client hands the record to the bag (slot = record+16, container = record+15) when its uid is above 0, and a stack
    /// count of 0 there clears the cell, so <paramref name="item126"/> must be the item as placed in the bag. Disposition
    /// <see cref="ClaimDeletesMail"/> makes the client delete the mail. Never result 0 with a zero record: the client
    /// would take the success path for a claim that moved nothing.
    /// </summary>
    public static byte[] BuildMailGetItem2447(
        uint ownerHandle, ushort result, ulong mailUid, ReadOnlySpan<byte> item126, byte disposition)
    {
        var item = new byte[ItemRecordLength];
        if (item126.Length == ItemRecordLength) item126.CopyTo(item);
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagGetItem); // blob+2
            p.WriteUInt32(ownerHandle);   // blob+4
            p.WriteUInt16(result);        // blob+8
            p.WriteUInt64(mailUid);       // blob+10
            p.WriteBytes(item);           // blob+18 (126)
            p.WriteByte(disposition);     // blob+144
        });
        return pw.ToArray();
    }

    /// <summary>Builds the 2447 failure response: the result code, the echoed mail uid, an all-zero item record, and disposition 0.</summary>
    public static byte[] BuildMailGetItemFailed2447(uint ownerHandle, ushort result, ulong mailUid)
        => BuildMailGetItem2447(ownerHandle, result, mailUid, ReadOnlySpan<byte>.Empty, GetItemFailedDisposition);

    /// <summary>
    /// Trailing byte (blob+18) of a 2448 OnMailDelete on the delete-ok path: always 2.
    /// </summary>
    private const byte DeleteOkTrailer = 2;

    /// <summary>Builds the 2448 OnMailDelete message (blob 19) for a removed mail: tag, owner, u16 result, u64 uid, trailing byte 2.</summary>
    public static byte[] BuildMailDelete2448(uint ownerHandle, ulong mailUid)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDelete);
            p.WriteUInt32(ownerHandle);
            p.WriteUInt16(ResultSuccess);
            p.WriteUInt64(mailUid);
            p.WriteByte(DeleteOkTrailer);
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Disposition (2617 blob+26) of a claim where the mail stays in the mailbox. The client deletes the mail only on
    /// <see cref="ClaimDeletesMail"/>, which is sent for auction-proceeds mails; every player mail is answered 1.
    /// </summary>
    public const byte GetMoneyKeepMail = 1;

    /// <summary>
    /// Builds the 2617 OnMailGetMoney response (blob 27): result @8, mail uid @10, the wallet total after the claim @18 and
    /// the disposition @26. The value at @18 is a running total, not the amount on the mail: the client compares it with its
    /// displayed zeny and replaces it, and no separate wallet update follows. <paramref name="disposition"/> is
    /// <see cref="ClaimDeletesMail"/> when the claim removed the mail from the store, else <see cref="GetMoneyKeepMail"/>.
    /// </summary>
    public static byte[] BuildMailGetMoney2617(uint ownerHandle, ulong mailUid, long walletTotal, byte disposition)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagGetMoney);                  // blob+2
            p.WriteUInt32(ownerHandle);                     // blob+4
            p.WriteUInt16(ResultSuccess);                   // blob+8  result
            p.WriteUInt64(mailUid);                         // blob+10 echo of the request uid
            p.WriteUInt64((ulong)Math.Max(0, walletTotal)); // blob+18 wallet after the claim
            p.WriteByte(disposition);                       // blob+26 1 keep / 2 client deletes the mail
        });
        return pw.ToArray();
    }

    /// <summary>Mail lifetime in days: a mail's expiry date is its sent date plus 30 days. Not a table value.</summary>
    public const int MailLifetimeDays = 30;

    /// <summary>
    /// Builds one 146-byte mail row for the mail list. Layout: uid@0, kind u8@8, isRead u8@9, expiry(16)@10, sent(16)@26,
    /// zeny@42, itemUid@50, itemTemplateId@58, senderName wchar[21]@62, title wchar[21]@104.
    /// </summary>
    /// <param name="kind">Mail kind (row+8). Kind 0 is a player mail, and kind 22 is a system mail whose sender and title are
    /// decimal string-table ids; only kinds 1 to 11 make the client read <paramref name="itemTemplateId"/>.</param>
    /// <param name="isRead">Whether the mail has been read (row+9). It greys the row and feeds the client's unread count.</param>
    public static byte[] BuildListRecord146(
        ulong mailUid, byte kind, bool isRead, DateTime sentAt, long gold,
        ulong itemUid, uint itemTemplateId, string senderName, string title)
    {
        using var pw = new PacketWriter(ListRecordLength);
        pw.WriteUInt64(mailUid);                    // @0
        pw.WriteByte(kind);                         // @8  mail kind
        pw.WriteByte((byte)(isRead ? 1 : 0));       // @9  read flag
        // Expiry first: the client feeds row+10 to its remaining-time formatter and shows row+26 as the absolute date.
        WriteMailDate(pw, sentAt.AddDays(MailLifetimeDays)); // @10 expiry (16)
        WriteMailDate(pw, sentAt);                  // @26 sent (16)
        pw.WriteUInt64((ulong)Math.Max(0, gold));   // @42 attached zeny
        pw.WriteUInt64(itemUid);                    // @50 attached item uid
        pw.WriteUInt32(itemTemplateId);             // @58 attached item template id
        WriteFixedUtf16(pw, senderName, SenderNameBytes); // @62 (42)
        WriteFixedUtf16(pw, title, TitleBytes);           // @104 (42)
        var rec = pw.ToArray();
        // Defensive: guarantee the exact row width regardless of any string edge case.
        if (rec.Length != ListRecordLength)
        {
            var fixedRec = new byte[ListRecordLength];
            Array.Copy(rec, fixedRec, Math.Min(rec.Length, ListRecordLength));
            return fixedRec;
        }
        return rec;
    }

    /// <summary>
    /// Builds the attached item record shown in the 2446 message (record+47): the attached item serialized as the bag
    /// serializes it, parked in container <see cref="MailAttachmentContainer"/> (<see cref="AuctionAttachmentContainer"/> for
    /// an auction mail), slot 0. Before the claim it is the attached item; once the item is claimed, the record depends on
    /// <paramref name="claimedThisEntry"/> (<see cref="MailService.ClaimedThisEntry"/>): until the next map load it keeps
    /// showing the item with only its uid zeroed, and after that it is all zero. A mail that never carried an item gets the
    /// all-zero record.
    /// </summary>
    public static byte[] BuildAttachmentRecord126(MailMessage mail, bool claimedThisEntry)
    {
        if (mail.AttachedItemId == 0 || (mail.ItemClaimed && !claimedThisEntry)) return new byte[ItemRecordLength];
        // A claimed item is in the bag under its own uid now; the mail shows it without one.
        ulong uid = mail.ItemClaimed ? 0 : mail.AttachedItemUid;
        var rec = WorldEntryService.BuildItemRecord126(MailService.AttachmentAsBagRow(mail, mail.ReceiverCharId, uid, 0));
        rec[ItemRecordContainerOffset] = AuctionMailKind.IsAuction(mail.Kind)   // container: still in the mail, never a bag cell
            ? AuctionAttachmentContainer
            : MailAttachmentContainer;
        rec[ItemRecordSlotOffset] = 0;
        return rec;
    }

    // ----- C2S parsing -----

    /// <summary>
    /// A parsed 1393 ReqMailSend request. <see cref="ItemCount"/> is the stack count the client attached (u8 @110),
    /// <see cref="Gold"/> the attached zeny (@92) and <see cref="Body"/> the mail text (@113).
    /// </summary>
    public readonly record struct MailSendRequest(
        uint OwnerHandle, string RecipientName, string Title, ulong ItemUid, uint ItemCount, long Gold, string Body);

    /// <summary>
    /// Strips the compact-length prefix from a message and returns the blob that starts at the u16 self length. Returns false
    /// when the message is malformed or too short. Lenient: the declared length is not compared, and the blob only has to
    /// hold the u16 self length.
    /// </summary>
    public static bool TryGetBlob(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> blob)
    {
        if (!payload.TryGetCompactBlob(CompactLengthCheck.Lenient, out blob) || blob.Length < 2)
        {
            blob = default;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Reads the owner handle (blob+4) and mail uid (blob+8) shared by the mail requests ReqMailGetItem (1396),
    /// ReqMailRead (1397), ReqMailDelete (1398) and ReqMailGetMoney (1457).
    /// </summary>
    public static bool TryParseMailRef(ReadOnlySpan<byte> payload, out uint ownerHandle, out ulong mailUid)
    {
        ownerHandle = 0; mailUid = 0;
        if (!TryGetBlob(payload, out var blob)) return false;
        if (blob.Length < MailRefLength) return false;
        ownerHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(OwnerOffset));
        mailUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(MailRefUidOffset));
        return true;
    }

    /// <summary>
    /// Parses the 1393 ReqMailSend request: the recipient name, title, zeny, attached item reference, stack count and body
    /// text (layout above, <see cref="SendFixedBytes"/>). The blob is 113 + 2m bytes for a body of m characters (NUL
    /// included). The zeny is read as u64 (bytes 96..99 are always 0). The stack count is the u8 at @110; @111 is the body
    /// length.
    /// </summary>
    public static bool TryParseSend(ReadOnlySpan<byte> payload, out MailSendRequest req)
    {
        req = default;
        if (!TryGetBlob(payload, out var blob)) return false;
        if (blob.Length < SendFixedBytes) return false;

        uint owner = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(OwnerOffset));
        // The recipient is looked up exactly as typed, with no trim: a name with a trailing space is a different name
        // and must be answered as not found rather than resolve to the trimmed one.
        string recip = ReadFixedUtf16Untrimmed(blob, SendRecipOffset, SendRecipBytes);
        string title = ReadFixedUtf16(blob, SendTitleOffset, SendTitleBytes);
        ulong gold = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(SendGoldOffset));
        ulong itemUid = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(SendItemUidOffset));
        uint itemCount = blob[SendItemCountOffset];
        int bodyChars = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(SendBodyLenOffset));
        string body = ReadFixedUtf16(blob, SendFixedBytes, bodyChars * 2);

        if (string.IsNullOrWhiteSpace(recip)) return false;
        req = new MailSendRequest(owner, recip, title, itemUid, itemCount,
            gold > long.MaxValue ? long.MaxValue : (long)gold, body);
        return true;
    }

    // ----- Primitives -----

    /// <summary>
    /// Writes a 16-byte mail date: the OLE Automation date (double), 4 zero bytes, then the double's high dword again. The
    /// client reads only the double. The value is local wall-clock time, while stored times are UTC.
    /// </summary>
    private static void WriteMailDate(PacketWriter pw, DateTime dt)
    {
        double oa;
        try { oa = dt.ToLocalTime().ToOADate(); }
        catch { oa = 0; }
        pw.WriteDouble(oa);
        pw.WriteUInt32(0);
        pw.WriteUInt32((uint)(BitConverter.DoubleToInt64Bits(oa) >> 32));
    }

    /// <summary>
    /// Writes the date slot of a 2446 that found no mail (<see cref="BuildMailReadFailed2446"/>): the OLE date of time_t 0
    /// in local time and a zero tail.
    /// </summary>
    private static void WriteUnsetMailDate(PacketWriter pw)
    {
        pw.WriteDouble(DateTime.UnixEpoch.ToLocalTime().ToOADate());
        pw.WriteZeros(UnsetMailDateTailBytes);
    }

    private static void WriteItemRecord(PacketWriter pw, ReadOnlySpan<byte> item126)
    {
        if (item126.Length == ItemRecordLength) pw.WriteBytes(item126);
        else pw.WriteZeros(ItemRecordLength);
    }

    /// <summary>Writes a string as a WStr16: a u16 character count, then UTF-16LE characters.</summary>
    private static void WriteWStr16(PacketWriter pw, string? s)
    {
        s ??= string.Empty;
        if (s.Length > ushort.MaxValue) s = s.Substring(0, ushort.MaxValue);
        pw.WriteUInt16((ushort)s.Length);
        if (s.Length > 0)
            pw.WriteBytes(Encoding.Unicode.GetBytes(s));
    }

    /// <summary>Writes a string as UTF-16LE into a fixed-size field, truncated (keeping a NUL terminator) and zero-padded.</summary>
    private static void WriteFixedUtf16(PacketWriter pw, string? s, int byteLen)
    {
        var field = new byte[byteLen];
        if (!string.IsNullOrEmpty(s))
        {
            int maxChars = byteLen / 2 - 1; // keep a null terminator
            if (maxChars < 0) maxChars = 0;
            var text = s!.Length > maxChars ? s.Substring(0, maxChars) : s;
            var bytes = Encoding.Unicode.GetBytes(text);
            Array.Copy(bytes, field, Math.Min(bytes.Length, byteLen));
        }
        pw.WriteBytes(field);
    }

    private static string ReadFixedUtf16(ReadOnlySpan<byte> blob, int off, int byteLen) =>
        ReadFixedUtf16Untrimmed(blob, off, byteLen).Trim();

    /// <summary>Reads a fixed-size UTF-16LE field up to its first NUL character, whitespace kept.</summary>
    private static string ReadFixedUtf16Untrimmed(ReadOnlySpan<byte> blob, int off, int byteLen)
    {
        if (off < 0 || off >= blob.Length) return string.Empty;
        int avail = Math.Min(byteLen, blob.Length - off);
        avail -= avail % 2;
        if (avail <= 0) return string.Empty;
        var slice = blob.Slice(off, avail);
        var s = Encoding.Unicode.GetString(slice);
        int nul = s.IndexOf('\0');
        if (nul >= 0) s = s.Substring(0, nul);
        return s;
    }
}
