using RO2.Economy.Domain;
using System.Buffers.Binary;
using System.Text;
using RO2.Domain.Entities;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Server-to-client packets of the cash mall (balance, purchase answers, purchase history) and of the web-mall storage box (box lists and claims).
/// </summary>
public static class CashMallPackets
{
 /// <summary>Result code that means success. Answers that carry a result code write it as a 16-bit value.</summary>
    public const ushort ResultOk = 0;

 /// <summary>Size in bytes of the 32-bit length that prefixes the blob of a purchase answer (2870).</summary>
    public const int BlobLengthBytes = 4;

 /// <summary>Size in bytes of the 16-bit entry count at the start of a purchase blob (2870).</summary>
    public const int EntryCountBytes = 2;

 /// <summary>Size in bytes of the UTF-16 code unit used by every text field in these packets.</summary>
    public const int Utf16CharBytes = 2;


 /// <summary>Length of the balance body (2868), in bytes.</summary>
    public const int MyInfoBytes = 14;

 /// <summary>Offset of the balance (a 32-bit float) in the balance body (2868), after the 16-bit result code.</summary>
    public const int MyInfoBalanceOffset = 2;

 /// <summary>
 /// Builds the balance body (2868): a result code of 0, the balance and two reserved fields.
 /// </summary>
    public static byte[] Build2868(long balance)
    {
        var body = new byte[MyInfoBytes];
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(MyInfoBalanceOffset), balance);
        return body;
    }

    
 /// <summary>One product delivered by a successful purchase.</summary>
    public readonly record struct Delivered(uint ItemId, ushort Quantity, uint OrderId);

 /// <summary>Size in bytes of one purchase entry.</summary>
    public const int PurchaseEntryBytes = 12;

 /// <summary>Offset of the item id inside a purchase entry (2870), after a 16-bit zero.</summary>
    public const int PurchaseEntryItemOffset = 2;

 /// <summary>Offset of the quantity inside a purchase entry (2870).</summary>
    public const int PurchaseEntryQuantityOffset = 6;

 /// <summary>Offset of the order id inside a purchase entry (2870).</summary>
    public const int PurchaseEntryOrderOffset = 8;

 /// <summary>
 /// Size in bytes of the fixed part that follows the entries of a purchase blob (2870): a 16-bit result code, a 16-bit zero, the balance as a 32-bit float and a 32-bit zero.
 /// </summary>
    public const int PurchaseTrailerBytes = 12;

 /// <summary>Offset of the balance inside the purchase trailer (2870).</summary>
    public const int PurchaseTrailerBalanceOffset = 4;

 /// <summary>Value of the final byte of a purchase answer (2870) when the purchase was a gift.</summary>
    public const byte GiftFlag = 1;

 /// <summary>
 /// Builds the answer to a successful purchase (2870), listing each delivered product with its quantity and order id, followed by the new balance.
 /// </summary>
 /// <remarks>
 /// The trailing byte is 1 when the purchase was a gift, otherwise 0.
 /// </remarks>
    public static byte[] Build2870Success(IReadOnlyList<Delivered> lines, long balance, bool gift)
    {
        int blobLen = EntryCountBytes + lines.Count * PurchaseEntryBytes + PurchaseTrailerBytes;
        var body = new byte[BlobLengthBytes + blobLen + 1];
        var s = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)blobLen);
        BinaryPrimitives.WriteUInt16LittleEndian(s[BlobLengthBytes..], (ushort)lines.Count);
        int pos = BlobLengthBytes + EntryCountBytes;
        foreach (var l in lines)
        {
            
            BinaryPrimitives.WriteUInt32LittleEndian(s[(pos + PurchaseEntryItemOffset)..], l.ItemId);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(pos + PurchaseEntryQuantityOffset)..], l.Quantity);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(pos + PurchaseEntryOrderOffset)..], l.OrderId);
            pos += PurchaseEntryBytes;
        }
        
        BinaryPrimitives.WriteSingleLittleEndian(s[(pos + PurchaseTrailerBalanceOffset)..], balance);
        s[^1] = gift ? GiftFlag : (byte)0;
        return body;
    }

 /// <summary>
 /// Builds the answer to a refused purchase (2870): no entries, the given result code and a zero balance.
 /// </summary>
    public static byte[] Build2870Refusal(ushort code, bool gift)
    {
        const int blobLen = EntryCountBytes + PurchaseTrailerBytes;
        var body = new byte[BlobLengthBytes + blobLen + 1];
        var s = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, blobLen);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(BlobLengthBytes + EntryCountBytes)..], code);
        s[^1] = gift ? GiftFlag : (byte)0;
        return body;
    }

    
 /// <summary>One purchase history line (2874): sequence number, item, quantity, unit price, time, and for gifts the sender and recipient names.</summary>
    public readonly record struct HistoryLine(uint Sequence, uint ItemId, ushort Quantity, long UnitPrice, ulong Time,
        bool Gift, string SenderName, string RecipientName);

 /// <summary>Maximum number of history records returned in one answer. The newest records are kept.</summary>
    public const int HistoryMaxRecords = 200;

 /// <summary>Maximum length of a name in a history record.</summary>
    public const int HistoryNameMaxChars = 20;

 /// <summary>Size in bytes of one history record without its names: sequence, item, quantity, unit price, time and gift byte (23), plus one length byte for each of the two names.</summary>
    public const int HistoryRecordMinBytes = 25;

 /// <summary>Spare capacity reserved for the record count when sizing the history blob.</summary>
    public const int HistoryBlobSpareBytes = 64;

 /// <summary>Size in bytes of the history answer header (2874): a 16-bit result code, the flag byte and the 32-bit blob length.</summary>
    public const int HistoryHeaderBytes = 7;

 /// <summary>
 /// Builds the purchase history answer (2874). Records are listed newest first, each with its sequence number and time.
 /// </summary>
 /// <remarks>
 /// An empty history is sent as a list with no records. The request flag is echoed back in the answer.
 /// </remarks>
    public static byte[] Build2874(byte flag, IReadOnlyList<HistoryLine> newestFirst)
    {
        using var blob = new PacketWriter(HistoryBlobSpareBytes + newestFirst.Count * HistoryRecordMinBytes);
        int n = Math.Min(newestFirst.Count, HistoryMaxRecords);
        blob.WriteUInt16((ushort)n);
        for (int i = 0; i < n; i++)
        {
            var l = newestFirst[i];
            blob.WriteUInt32(l.Sequence);
            blob.WriteUInt32(l.ItemId);
            blob.WriteUInt16(l.Quantity);
            blob.WriteSingle(l.UnitPrice);
            blob.WriteUInt64(l.Time);
            blob.WriteByte(l.Gift ? GiftFlag : (byte)0);
            WriteU8CountedName(blob, l.Gift ? l.SenderName : "");
            WriteU8CountedName(blob, l.Gift ? l.RecipientName : "");
        }
        var b = blob.GetWrittenSpan();
        using var w = new PacketWriter(HistoryHeaderBytes + b.Length);
        w.WriteUInt16(ResultOk);
        w.WriteByte(flag);
        w.WriteUInt32((uint)b.Length);
        w.WriteBytes(b);
        return w.ToArray();
    }

 /// <summary>Builds the account's purchase history as a newest-first list, numbering each record.</summary>
    public static List<HistoryLine> HistoryNewestFirst(IReadOnlyList<CashMallPurchase> oldestFirst)
    {
        var lines = new List<HistoryLine>(Math.Min(oldestFirst.Count, HistoryMaxRecords));
        for (int i = oldestFirst.Count - 1; i >= 0 && lines.Count < HistoryMaxRecords; i--)
        {
            var p = oldestFirst[i];
            lines.Add(new HistoryLine((uint)(i + 1), p.ItemId, p.Quantity, p.UnitPrice,
                (ulong)Math.Max(0, WallClockUnix(p.BoughtUtc)), p.Gift, p.SenderName, p.RecipientName));
        }
        return lines;
    }

    
 /// <summary>One listed box entry: its id, units waiting, item, filing date and, for the gift box, the sender.</summary>
    public readonly record struct ListedEntry(uint EntryId, byte Count, uint ItemId, double FiledOleDate, string SenderName);

 /// <summary>
 /// Number of entries shown on one page of a box list. Each page answer lists the entries from its first row to the end of the box.
 /// </summary>
    public const int ListPageRows = 5;

 /// <summary>Maximum number of entries one box list answer can carry.</summary>
    public const int ListMaxEntries = byte.MaxValue;

 /// <summary>Size in bytes of one box list entry. A gift box entry adds <see cref="GiftSenderNameBytes"/>.</summary>
    public const int ListEntryBytes = 25;

 /// <summary>Size in bytes of the sender name field of a gift box entry (zero-padded UTF-16).</summary>
    public const int GiftSenderNameBytes = 82;

 /// <summary>Size in bytes of the header of a purchase box list (2639): page, page count, box total and entry count.</summary>
    public const int ListHeaderBytes = 7;

 /// <summary>Tab number written in the header of a gift box list (2640), between the box total and the entry count. It is the gift box.</summary>
    public const byte GiftListTab = 2;

 /// <summary>
 /// Returns the entries a page answer lists from a box of <paramref name="total"/> entries, and the page count of the box.
 /// </summary>
    public static (int Start, int Count, ushort Pages) ListPage(int total, uint page)
    {
        total = Math.Max(total, 0);
        int pages = (total + ListPageRows - 1) / ListPageRows;
        long index = Math.Max(page, 1u) - 1;
        int start = (int)Math.Min(index * ListPageRows, total);
        return (start, Math.Min(total - start, ListMaxEntries), (ushort)Math.Min(pages, ushort.MaxValue));
    }

 /// <summary>
 /// Builds the answer to a box list request (1471): the gift box uses the gift list format (2640), any other box uses the purchase list format (2639).
 /// </summary>
 /// <remarks>
 /// <paramref name="newestFirst"/> is the whole box in list order. The entries returned are the ones <see cref="ListPage"/> selects for the requested page.
 /// </remarks>
    public static byte[] BuildBoxListAnswer(byte box, uint page, IReadOnlyList<ListedEntry> newestFirst)
    {
        var (start, count, pages) = ListPage(newestFirst.Count, page);
        var listed = new ListedEntry[count];
        for (int i = 0; i < count; i++) listed[i] = newestFirst[start + i];
        var pageEcho = (ushort)Math.Min(page, ushort.MaxValue);
        var boxTotal = (ushort)Math.Min(newestFirst.Count, ushort.MaxValue);
        return box == CashMallStore.BoxGift
            ? Build2640(pageEcho, pages, boxTotal, listed)
            : Build2639(pageEcho, pages, boxTotal, listed);
    }

 /// <summary>
 /// Builds the purchase box list answer (2639, <c>AnsWebItemSearch</c>): page information, the box total, then one entry per listed item.
 /// </summary>
 /// <remarks>
 /// The box total counts all entries in the box, not only the listed ones. More than <see cref="ListMaxEntries"/> entries are refused, because the count is a single byte.
 /// </remarks>
    public static byte[] Build2639(ushort page, ushort pages, ushort boxTotal, IReadOnlyList<ListedEntry> entries)
    {
        if (entries.Count > ListMaxEntries)
            throw new ArgumentException($"a list answer carries at most {ListMaxEntries} entries", nameof(entries));
        using var blob = new PacketWriter(ListHeaderBytes + entries.Count * ListEntryBytes);
        blob.WriteUInt16(page);
        blob.WriteUInt16(pages);
        blob.WriteUInt16(boxTotal);
        blob.WriteByte((byte)entries.Count);
        foreach (var e in entries) WriteListEntry(blob, e);
        return WithCompactLength(blob.GetWrittenSpan());
    }

 /// <summary>
 /// Builds the gift box list answer (2640, <c>AnsWebGiftItemSearch</c>): the purchase list format plus the sender name of each entry.
 /// </summary>
 /// <remarks>
 /// The box total counts all entries in the box, not only the listed ones. More than <see cref="ListMaxEntries"/> entries are refused.
 /// </remarks>
    public static byte[] Build2640(ushort page, ushort pages, ushort boxTotal, IReadOnlyList<ListedEntry> entries)
    {
        if (entries.Count > ListMaxEntries)
            throw new ArgumentException($"a list answer carries at most {ListMaxEntries} entries", nameof(entries));
        using var blob = new PacketWriter(ListHeaderBytes + 1 + entries.Count * (ListEntryBytes + GiftSenderNameBytes));
        blob.WriteUInt16(page);
        blob.WriteUInt16(pages);
        blob.WriteUInt16(boxTotal);
        blob.WriteByte(GiftListTab);
        blob.WriteByte((byte)entries.Count);
        foreach (var e in entries)
        {
            WriteListEntry(blob, e);
            WriteFixedUtf16(blob, e.SenderName, GiftSenderNameBytes);
        }
        return WithCompactLength(blob.GetWrittenSpan());
    }

    
 /// <summary>Length of the claim body (2641), in bytes.</summary>
    public const int ClaimBytes = 194;

 /// <summary>Size in bytes of the sender name field of a claim.</summary>
    public const int ClaimSenderNameBytes = 42;

 /// <summary>Value of the reserved 32-bit field after the entry id of a claim (2641). It is always zero.</summary>
    public const uint ClaimReservedWord = 0;

 /// <summary>Value of the reserved byte before the box number of a claim (2641). It is always zero.</summary>
    public const byte ClaimReservedByte = 0;

 /// <summary>
 /// Builds the claim answer (2641, <c>AnsWebItemAtMyInven</c>), sent once for each unit delivered. It carries the claimed entry, the box, the date, the sender and the bag record of the item.
 /// </summary>
    public static byte[] Build2641(uint entryId, byte box, double filedOleDate, string senderName, ReadOnlySpan<byte> record126)
    {
        if (record126.Length != LootNotifyPackets.ItemRecordLength)
            throw new ArgumentException($"expected a {LootNotifyPackets.ItemRecordLength}-byte record", nameof(record126));
        using var w = new PacketWriter(ClaimBytes);
        w.WriteUInt32(entryId);
        w.WriteUInt32(ClaimReservedWord);
        w.WriteByte(ClaimReservedByte);
        w.WriteByte(box);
        w.WriteDouble(filedOleDate);
        w.WriteDouble(HighDwordOnly(filedOleDate));
        WriteFixedUtf16(w, box == CashMallStore.BoxGift ? senderName : "", ClaimSenderNameBytes);
        w.WriteBytes(record126);
        return w.ToArray();
    }

    
 /// <summary>Returns the server-local date of a stored UTC instant, as used by the box lists and claims.</summary>
    public static double WallClockOleDate(DateTime storedUtc) => CashMallStore.ToLocal(storedUtc).ToOADate();

 /// <summary>Returns the Unix time of a stored UTC instant on the server's wall clock, used for purchase history times.</summary>
    public static long WallClockUnix(DateTime storedUtc)
        => new DateTimeOffset(CashMallStore.ToLocal(storedUtc).Ticks, TimeSpan.Zero).ToUnixTimeSeconds();

 /// <summary>Returns <paramref name="value"/> with the low 32 bits of its encoding cleared.</summary>
    public static double HighDwordOnly(double value)
        => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) & unchecked((long)HighDwordMask));

 /// <summary>Clips <paramref name="name"/> to the maximum length a history record carries.</summary>
    public static string ClipName(string? name)
        => string.IsNullOrEmpty(name) ? "" : name.Length <= HistoryNameMaxChars ? name : name[..HistoryNameMaxChars];

    /// <summary>Mask that keeps the high 32 bits of a 64-bit value.</summary>
    private const ulong HighDwordMask = 0xFFFFFFFF00000000UL;

    /// <summary>Largest size in bytes of a compact length prefix: a width byte and a 64-bit value.</summary>
    private const int CompactLengthMaxBytes = 9;

    private static void WriteListEntry(PacketWriter w, ListedEntry e)
    {
        w.WriteUInt32(e.EntryId);
        w.WriteByte(e.Count);
        w.WriteUInt32(e.ItemId);
        w.WriteDouble(e.FiledOleDate);
        w.WriteDouble(HighDwordOnly(e.FiledOleDate));
    }

    private static void WriteU8CountedName(PacketWriter w, string name)
    {
        var s = ClipName(name);
        w.WriteByte((byte)s.Length);
        w.WriteBytes(Encoding.Unicode.GetBytes(s));
    }

 /// <summary>Writes UTF-16LE text, zero-padded to <paramref name="bytes"/> and always leaving room for a terminating zero.</summary>
    private static void WriteFixedUtf16(PacketWriter w, string? text, int bytes)
    {
        var field = new byte[bytes];
        if (!string.IsNullOrEmpty(text))
        {
            int maxChars = bytes / Utf16CharBytes - 1;
            var s = text.Length <= maxChars ? text : text[..maxChars];
            Encoding.Unicode.GetBytes(s, field);
        }
        w.WriteBytes(field);
    }

    private static byte[] WithCompactLength(ReadOnlySpan<byte> blob)
    {
        using var w = new PacketWriter(blob.Length + CompactLengthMaxBytes);
        w.WriteCompactLength(blob.Length);
        w.WriteBytes(blob);
        return w.ToArray();
    }
}
