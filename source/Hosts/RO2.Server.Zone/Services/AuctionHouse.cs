using RO2.Economy.Application;
using RO2.Economy.Domain;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The ItemInfo facts the public search needs and <see cref="ItemData"/> does not load: each template's
/// English display name (ItemInfo <c>String_Item_Name</c> resolved in <c>data/strings/string_item_name.json</c>),
/// its <c>Low_Category</c>, and the (<c>Require_Level</c>, <c>Item_Lv</c>) pair the search's level column
/// sorts on (<see cref="AuctionPackets.SearchKeyComparer"/>; ItemData has no Item_Lv). Read once, lazily, from the same <c>"data"</c> directory Program.cs hands
/// GameDataStore (as BuyItemHandler reads Merchant.json).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the English name.</b> The default search is ordered by the item's DISPLAY name: sorting by
/// (display name, ordinal) then start price is the order the client expects, whereas ordering by template id or
/// by the Korean ItemInfo.Name is not. The few remaining mismatches are name-table drift, where a template's
/// Korean name still carries an older English name.
/// </para>
/// <para>
/// A template with no English name falls back to the Korean ItemInfo.Name, and one with neither to its
/// decimal id, so every article still gets a stable key. With the files missing (a test run outside the
/// server's working directory) the catalog is empty and every lookup takes that fallback.
/// </para>
/// </remarks>
public static class AuctionItemCatalog
{
    private sealed record Row(uint ID, uint String_Item_Name, int Low_Category, int Require_Level, int Item_Lv);

    private sealed record Tables(IReadOnlyDictionary<uint, string> Names, IReadOnlyDictionary<uint, byte> LowCategories,
        IReadOnlyDictionary<uint, (int RequireLevel, int ItemLv)> Levels);

    private static readonly Lazy<Tables> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Loads the tables now (from a background task at boot), so the first search does not pay
    /// for parsing ItemInfo.json.</summary>
    public static void Warm() => _ = Loaded.Value;

    /// <summary>The template's English display name, else its Korean ItemInfo.Name, else its id.</summary>
    public static string DisplayName(uint templateId, GameDataStore? store)
    {
        if (Loaded.Value.Names.TryGetValue(templateId, out var name)) return name;
        var korean = store?.GetItem(templateId)?.Name;
        return string.IsNullOrEmpty(korean) ? templateId.ToString(System.Globalization.CultureInfo.InvariantCulture) : korean;
    }

    /// <summary>The template's ItemInfo Low_Category, or null when the template is unknown.</summary>
    public static byte? LowCategory(uint templateId)
        => Loaded.Value.LowCategories.TryGetValue(templateId, out var low) ? low : null;

    /// <summary>
    /// The template's ItemInfo (Require_Level, Item_Lv), the key of the search's level column (order byte
    /// 0x20 / 0x22). With the catalog unread it falls back to ItemData's Require_Level and Item_Lv 0, and an
    /// unknown template sorts as (0, 0).
    /// </summary>
    public static (int RequireLevel, int ItemLv) Level(uint templateId, GameDataStore? store)
    {
        if (Loaded.Value.Levels.TryGetValue(templateId, out var lv)) return lv;
        return (store?.GetItem(templateId)?.Require_Level ?? 0, 0);
    }

    private static Tables Load()
    {
        var names = new Dictionary<uint, string>();
        var lows = new Dictionary<uint, byte>();
        var levels = new Dictionary<uint, (int, int)>();
        try
        {
            var itemPath = System.IO.Path.Combine("data", "ItemInfo.json");
            var namePath = System.IO.Path.Combine("data", "strings", "string_item_name.json");
            if (!System.IO.File.Exists(itemPath)) return new Tables(names, lows, levels);
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<Row>>(System.IO.File.ReadAllBytes(itemPath));
            Dictionary<string, string>? strings = null;
            if (System.IO.File.Exists(namePath))
                strings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllBytes(namePath));
            foreach (var r in rows ?? new List<Row>())
            {
                lows.TryAdd(r.ID, (byte)Math.Clamp(r.Low_Category, 0, byte.MaxValue));
                levels.TryAdd(r.ID, (r.Require_Level, r.Item_Lv));
                if (strings != null && r.String_Item_Name != 0
                    && strings.TryGetValue(r.String_Item_Name.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n)
                    && !string.IsNullOrEmpty(n))
                    names.TryAdd(r.ID, n);
            }
        }
        catch
        {
            // Unreadable tables: every lookup takes the fallback (Korean name / id, no Low_Category filter).
        }
        return new Tables(names, lows, levels);
    }
}

/// <summary>
/// Wire (de)serialization of the consignment-sale family 1401-1406 / 2455-2461 and 2693. Every C2S offset below
/// is BLOB-relative (blob+0 = the u16 innerSize after the compact length prefix), which is what
/// <see cref="MailPackets.TryGetBlob"/> hands back. The bid messages (1407/2462, 1404/2459) are in
/// AuctionBidding.cs and AuctionBidAccept.cs.
/// </summary>
public static partial class AuctionPackets
{
    // S2C msgTags (blob+2). Each one is the request's tag + 1.
    public const ushort MsgTagSearch = 0xE361;          // 2456: 58209
    public const ushort MsgTagSellList = 0xE363;        // 2455: 58211
    public const ushort MsgTagTenderList = 0xE365;      // 2457: 58213
    public const ushort MsgTagSellStart = 0xE367;       // 2458: 58215
    public const ushort MsgTagSellCancel = 0xE36B;      // 2460: 58219
    public const ushort MsgTagBuySellingPrice = 0xE36D; // 2461: 58221

    // C2S msgTags (blob+2) and the fixed innerSize of each request.
    public const ushort MsgTagReqSearch = 0xE360;          // 1400: 58208, innerSize the constant 37
    public const ushort MsgTagReqSellList = 0xE362;        // 1401: 58210, blob 11
    public const ushort MsgTagReqTenderList = 0xE364;      // 1402: 58212, blob 11
    public const ushort MsgTagReqSellStart = 0xE366;       // 1403: 58214, blob 40
    public const ushort MsgTagReqSellCancel = 0xE36A;      // 1405: 58218, blob 24
    public const ushort MsgTagReqBuySellingPrice = 0xE36C; // 1406: 58220, blob 36

    /// <summary>AuctionEntry210: the client allocates and copies exactly 0xD2 (210) bytes per record.</summary>
    public const int EntryLength = 210;

    /// <summary>
    /// Container byte (item record +15) of an item that sits in an auction entry: 103 with slot 0.
    /// Like the mail container 102 it is above the bag guard (a container byte above 4 is never a bag), so an
    /// entry can never be placed into, or clear, a bag cell.
    /// </summary>
    public const byte ItemContainer = 103;

    /// <summary>Slot byte (item record +16) of an item that sits in an auction entry: always 0.</summary>
    private const byte ItemSlot = 0;

    /// <summary>Offset in a 126-byte item record of the container byte (<see cref="ItemContainer"/> in an entry).</summary>
    internal const int ItemRecordContainerOffset = 15;

    /// <summary>Offset in a 126-byte item record of the slot byte (<see cref="ItemSlot"/> in an entry).</summary>
    internal const int ItemRecordSlotOffset = 16;

    /// <summary>Inventory <c>StorageType</c> of the character's bag (1 = worn equipment, 2 = bank).</summary>
    private const byte BagStorageType = 0;

    /// <summary>Result code 0 of 2458 / 2460 / 2461: the request succeeded.</summary>
    public const ushort ResultSuccess = AuctionCodes.ResultSuccess;

    // Blob-relative layout of the requests (blob+0 = the u16 innerSize). Every request carries its u16 msgTag
    // at blob+2 and the requester's u32 handle at blob+4.
    private const int BlobTagOffset = 2;
    private const int BlobPageOffset = 8; // u16 page of 1400 / 1401 / 1402

    /// <summary>innerSize (blob length) of 1401 / 1402.</summary>
    private const int ListRequestBlobLength = 11;

    /// <summary>innerSize (blob length) of 1403.</summary>
    private const int SellStartBlobLength = 40;
    private const int SellStartItemUidOffset = 8;      // u64
    private const int SellStartSlotOffset = 16;        // u8 bag slot
    private const int SellStartContainerOffset = 17;   // u8 container
    private const int SellStartCountOffset = 18;       // u8 stack count
    private const int SellStartHoursOffset = 19;       // u8 duration in hours
    private const int SellStartStartPriceOffset = 20;  // u64
    private const int SellStartInstantPriceOffset = 28; // u64
    private const int SellStartNpcHandleOffset = 36;   // u32

    /// <summary>innerSize (blob length) of 1405.</summary>
    private const int CancelBlobLength = 24;

    /// <summary>innerSize (blob length) of 1406.</summary>
    private const int BuyBlobLength = 36;

    // 1405 and 1406 both open with the article uid and the item uid.
    private const int ArticleUidOffset = 8;  // u64
    private const int ItemUidOffset = 16;    // u64
    private const int BuyItemIdOffset = 24;  // u32 item template id
    private const int BuyPriceOffset = 28;   // u64

    // 1400 (U32 family): the u32 blob length prefix comes first, then the blob.
    private const int SearchLengthPrefixSize = 4;
    private const int SearchHighCategoryOffset = 10; // u8
    private const int SearchMediumCategoryOffset = 11; // u8
    private const int SearchLowCategoryOffset = 12;  // u8
    private const int SearchJobOffset = 14;          // u8 class filter
    private const int SearchIdCountOffset = 19;      // u16 n
    private const int SearchIdListOffset = 21;       // n x u32 item template ids, then the u8 order byte

    /// <summary>Chars in the name field of an entry (+150): wchar[21], so 20 characters and a NUL.</summary>
    private const int EntryNameChars = 21;

    // 2693 NfyAuctionSystemMessage: a raw body, no length prefix.
    private const int SystemMessageLength = 28;
    private const int SystemMessageHandleOffset = 0;    // u32
    private const int SystemMessageTypeOffset = 4;      // u32
    private const int SystemMessageArticleUidOffset = 8; // u64
    private const int SystemMessageItemIdOffset = 16;   // u32
    private const int SystemMessageAmountOffset = 20;   // u64

    /// <summary>
    /// Page size of every auction list: the client computes pageCount = max(1,(total+9)/10).
    /// </summary>
    public const int PageSize = 10;

    /// <summary>2461/2460 result the client special-cases (16308): a buyout of an article another character had
    /// already bought, answered 16308 with the buyer's wallet unchanged.</summary>
    public const ushort ResultArticleGone = AuctionCodes.ResultArticleGone;

    // 2693 NfyAuctionSystemMessage types (u32 at body+4), each tied to the request it follows.
    // NotifySold carries an amount (the seller's proceeds); a 1-zeny sale gives amount 0, since
    // (long)float32(0.95) = 0. BuyAsync sends the notice either way.
    public const uint NotifySold = AuctionCodes.NotifySold;      // to the SELLER, amount = proceeds
    public const uint NotifyBought = AuctionCodes.NotifyBought;    // to the BUYER, sent before 2449 + 2461
    public const uint NotifyCancelled = AuctionCodes.NotifyCancelled; // to the canceller, sent before 2460

    /// <summary>The article uid band (104·10^16 + counter); items use band 101 and mails 103.</summary>
    public static ulong MakeArticleUid(ulong id) => AuctionArticle.MakeUid(id);

    // ---------------------------------------------------------------------------------------------
    // C2S
    // ---------------------------------------------------------------------------------------------

    /// <summary>1401 / 1402: [u16 11][u16 tag][u32 handle][u16 page @8][u8 kind @10].</summary>
    public static bool TryParseListRequest(ReadOnlySpan<byte> payload, ushort msgTag, out ushort page)
    {
        page = 0;
        if (!MailPackets.TryGetBlob(payload, out var b) || b.Length < ListRequestBlobLength) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobTagOffset)) != msgTag) return false;
        page = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobPageOffset));
        return true;
    }

    /// <summary>Parsed 1403 ReqAuctionSellStart.</summary>
    public readonly record struct SellStartRequest(
        ulong ItemUid, byte Slot, byte Bag, byte Count, byte Hours, ulong StartPrice, ulong BuyoutPrice, uint NpcHandle);

    /// <summary>
    /// 1403: itemUid u64 @8, bag slot u8 @16, container u8 @17, count u8 @18, duration in HOURS u8 @19
    /// (168 or 24; each new entry shows hours·60 minutes at entry+192),
    /// start price u64 @20, instant price u64 @28, the auction NPC's handle u32 @36 (low byte 0x13).
    /// The count is the stack count the new entry carries (record+14).
    /// </summary>
    public static bool TryParseSellStart(ReadOnlySpan<byte> payload, out SellStartRequest req)
    {
        req = default;
        if (!MailPackets.TryGetBlob(payload, out var b) || b.Length < SellStartBlobLength) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobTagOffset)) != MsgTagReqSellStart) return false;
        req = new SellStartRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(SellStartItemUidOffset)),
            b[SellStartSlotOffset], b[SellStartContainerOffset], b[SellStartCountOffset], b[SellStartHoursOffset],
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(SellStartStartPriceOffset)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(SellStartInstantPriceOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(SellStartNpcHandleOffset)));
        return true;
    }

    /// <summary>1405: article uid u64 @8, item uid u64 @16 — both echoed verbatim by 2460.</summary>
    public static bool TryParseCancel(ReadOnlySpan<byte> payload, out ulong articleUid, out ulong itemUid)
    {
        articleUid = itemUid = 0;
        if (!MailPackets.TryGetBlob(payload, out var b) || b.Length < CancelBlobLength) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobTagOffset)) != MsgTagReqSellCancel) return false;
        articleUid = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(ArticleUidOffset));
        itemUid = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(ItemUidOffset));
        return true;
    }

    /// <summary>Parsed 1406 ReqAuctionBuySellingPrice.</summary>
    public readonly record struct BuyRequest(ulong ArticleUid, ulong ItemUid, uint ItemId, ulong Price);

    /// <summary>1406: article uid u64 @8, item uid u64 @16, item template id u32 @24, price u64 @28.</summary>
    public static bool TryParseBuy(ReadOnlySpan<byte> payload, out BuyRequest req)
    {
        req = default;
        if (!MailPackets.TryGetBlob(payload, out var b) || b.Length < BuyBlobLength) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobTagOffset)) != MsgTagReqBuySellingPrice) return false;
        req = new BuyRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(ArticleUidOffset)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(ItemUidOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(BuyItemIdOffset)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(BuyPriceOffset)));
        return true;
    }

    /// <summary>
    /// The order byte a 1400 carries (its second-to-last byte), kept RAW: it is a bit field, not a list of
    /// cases, so any value decodes (<see cref="SearchKeyComparer"/>). The named members are the commonly used
    /// values.
    /// </summary>
    /// <remarks>
    /// The client builds it from the column-header click: bits 5-7 hold the clicked column and that column's own
    /// bit is toggled on each click.
    /// </remarks>
    public enum SearchOrder : byte
    {
        /// <summary>0x02, the default: display name ascending, then +134, then +142.</summary>
        Default = 0x02,
        /// <summary>0x03: display name DESCENDING, then +134 ascending.</summary>
        NameDesc = 0x03,
        /// <summary>0x20: (Require_Level, Item_Lv) ascending, then name.</summary>
        LevelAsc = 0x20,
        /// <summary>0x22: (Require_Level, Item_Lv) descending, then name.</summary>
        LevelDesc = 0x22,
        /// <summary>0x42: the current price (entry+134) ascending.</summary>
        StartPriceAsc = 0x42,
        /// <summary>0x46: the current price (entry+134: the start price, or the top bid once there is one),
        /// highest first, display name next, then +142 ascending. The client calls the column "start price"; it
        /// sorts the value the record shows.</summary>
        StartPriceDesc = 0x46,
        /// <summary>0x62: instant price (entry+142) ascending.</summary>
        InstantPriceAsc = 0x62,
        /// <summary>0x6A: instant price, highest first, display name next,
        /// then +134 ascending.</summary>
        InstantPriceDesc = 0x6A,
        /// <summary>0x82: remaining minutes (entry+192) DESCENDING with bit 4 clear, then name,
        /// then +134 ascending.</summary>
        RemainingDesc = 0x82,
        /// <summary>0x92: remaining minutes ascending with bit 4 set.</summary>
        RemainingAsc = 0x92,
    }

    /// <summary>
    /// What an article is ordered by in the public search: its id and the sort inputs. The price is the
    /// entry+134 value (the start price, or the top bid once there is one), not the stored start price; build
    /// the key with <see cref="SearchKeyOf"/>. <paramref name="ExpiryMinute"/> is the expiry floored to the
    /// whole minute, the floor <see cref="RemainingMinutes"/> subtracts from, so ordering by it orders exactly
    /// the entry+192 value every record of one page shows (all are read at the same "now"). The level column's
    /// key is per template, like the name, and comes from the comparer's level lookup.
    /// <paramref name="ExpiresTicks"/> is the full-precision expiry (<c>ExpiresAt.Ticks</c>), which only the
    /// final tie-break reads: tied records are ordered by remaining time, and two articles in the same
    /// whole minute still differ there. A key built without it (0) falls through to the id, the old order.
    /// </summary>
    public readonly record struct SearchKey(ulong Id, uint ItemId, long CurrentPrice, long BuyoutPrice, long ExpiryMinute = 0,
        long ExpiresTicks = 0);

    /// <summary>The search key of <paramref name="a"/>: its entry+134 price as BuildEntry210 writes it, its
    /// expiry minute (column 4) and its full-precision expiry (the final tie-break).</summary>
    public static SearchKey SearchKeyOf(AuctionArticle a)
        => new(a.Id, a.ItemId, CurrentPrice(a), a.BuyoutPrice, a.ExpiresAt.Ticks / TimeSpan.TicksPerMinute,
            a.ExpiresAt.Ticks);

    /// <summary>
    /// The 2456 record order for any 1400 order byte <c>b</c>. The PRIMARY column is <c>b &gt;&gt; 5</c>:
    /// 0 display name, 1 (Require_Level, Item_Lv), 2 entry+134 (current price), 3 entry+142 (instant price),
    /// 4 entry+192 (remaining minutes); a column above 4 is not used and falls back to 0. Column <c>c</c>'s
    /// direction is its own bit, <c>(b &gt;&gt; c) &amp; 1</c>: set = descending for columns 0-3, while
    /// column 4 is INVERTED (clear = descending, set = ascending). Ties then go through the remaining columns of
    /// [name, +134, +142], each in the direction of its own bit; then by template id ascending, then by expiry
    /// descending (most remaining time first), and last by article id, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, per column:
    /// </para>
    /// <list type="bullet">
    /// <item>col 0: bit 0 clear ascending (0x02), set descending (0x03, 0x13, 0x15). Name is
    ///   <see cref="string.CompareOrdinal(string, string)"/> on the English display name (not the template id, not
    ///   the Korean ItemInfo.Name, not case-insensitive).</item>
    /// <item>col 1: bit 1 clear ascending (0x20), set descending (0x22), on (Require_Level, Item_Lv); ties by name
    ///   ascending. This is the weakest-established column.</item>
    /// <item>col 2: bit 2 clear ascending (0x42, 0x4A), set descending (0x46). "Current price" is entry+134: on a
    ///   record with a bid that is the bid, so an article with a bid orders by the bid, not the start price.</item>
    /// <item>col 3: bit 3 clear ascending (0x62), set descending (0x6A).</item>
    /// <item>col 4, inverted: clear descending (0x82, 0x80, 0x8A), set ascending (0x92, 0x90, 0x9A).</item>
    /// <item>The secondary keys follow their own bits: 0x15 has bit 2 set and its name ties go +134
    ///   DESCENDING, while 0x03 / 0x13 (bit 2 clear) go ascending. Ties on (+134, name) go +142 ascending; ties on
    ///   (+142, name) and (minutes, name) go +134 ascending. Name ties after columns 2/3/4 are ascending.</item>
    /// <item>The tail after [name, +134, +142]:
    ///   <list type="bullet">
    ///   <item>Tied, same template, different +192: remaining minutes DESCENDING, at full precision
    ///     (<see cref="SearchKey.ExpiresTicks"/>), so an older article with the longer duration comes first.</item>
    ///   <item>Tied, different templates (one English name): template id ascending. This rule is weakly
    ///     established; it sits before the expiry because expiry-descending alone would invert that pair, and it acts
    ///     on nothing else (same template means same name).</item>
    ///   <item>Tied in the same minute: no key can order them deterministically; the id
    ///     step (newest first) only keeps paging deterministic.</item>
    ///   <item>The seller/tender lists (2455/2457) share this tail.</item>
    ///   </list></item>
    /// </list>
    /// <para>
    /// With bits 2/3 clear the default 0x02 is exactly the key (name, +134, +142, id), so the seller and tender
    /// lists (<see cref="OrderForList(IEnumerable{AuctionArticle}, Func{uint, string})"/>) keep their order.
    /// Column 1's key and the secondary-bit rule rest on limited evidence.
    /// </para>
    /// </remarks>
    public sealed class SearchKeyComparer : IComparer<SearchKey>
    {
        // The columns of the order byte. A column's own bit in the order byte (bit index == column index)
        // is its direction.
        private const int ColName = 0;
        private const int ColLevel = 1;
        private const int ColCurrentPrice = 2;  // entry+134
        private const int ColInstantPrice = 3;  // entry+142
        private const int ColRemaining = 4;     // entry+192, direction inverted

        /// <summary>The primary column sits in bits 5-7 of the order byte.</summary>
        private const int PrimaryColumnShift = 5;

        /// <summary>Tie-break columns after the primary one, in the client's expected order.</summary>
        private static readonly int[] Secondary = [ColName, ColCurrentPrice, ColInstantPrice];

        private readonly byte _order;
        private readonly int _primary;
        private readonly Func<uint, string> _name;
        private readonly Func<uint, (int RequireLevel, int ItemLv)>? _level;
        private readonly Dictionary<uint, string> _names = new();
        private readonly Dictionary<uint, (int, int)> _levels = new();

        /// <param name="order">The raw 1400 order byte.</param>
        /// <param name="displayName">The template's display name (<see cref="AuctionItemCatalog.DisplayName"/>).</param>
        /// <param name="level">The template's (Require_Level, Item_Lv) (<see cref="AuctionItemCatalog.Level"/>);
        /// only the level column reads it, so the lists that always use the default order pass none.</param>
        public SearchKeyComparer(SearchOrder order, Func<uint, string> displayName, Func<uint, (int RequireLevel, int ItemLv)>? level = null)
        {
            _order = (byte)order;
            int col = _order >> PrimaryColumnShift;
            _primary = col <= ColRemaining ? col : ColName;
            _name = displayName;
            _level = level;
        }

        private string Name(uint itemId)
        {
            if (!_names.TryGetValue(itemId, out var n)) _names[itemId] = n = _name(itemId);
            return n;
        }

        private (int, int) Level(uint itemId)
        {
            if (_level == null) return (0, 0);
            if (!_levels.TryGetValue(itemId, out var l)) _levels[itemId] = l = _level(itemId);
            return l;
        }

        private int Column(int col, SearchKey x, SearchKey y)
        {
            int c = col switch
            {
                ColName => Math.Sign(string.CompareOrdinal(Name(x.ItemId), Name(y.ItemId))),
                ColLevel => Level(x.ItemId).CompareTo(Level(y.ItemId)),
                ColCurrentPrice => x.CurrentPrice.CompareTo(y.CurrentPrice),
                ColInstantPrice => x.BuyoutPrice.CompareTo(y.BuyoutPrice),
                _ => x.ExpiryMinute.CompareTo(y.ExpiryMinute),
            };
            bool bit = ((_order >> col) & 1) != 0;
            bool descending = col == ColRemaining ? !bit : bit;
            return descending ? -c : c;
        }

        public int Compare(SearchKey x, SearchKey y)
        {
            int c = Column(_primary, x, y);
            foreach (var col in Secondary)
            {
                if (c != 0) break;
                if (col != _primary) c = Column(col, x, y);
            }
            if (c != 0) return c;
            // Tail (see the remarks): template ascending, expiry descending at full precision, then the id,
            // newest first, which only keeps paging deterministic.
            c = x.ItemId.CompareTo(y.ItemId);
            if (c != 0) return c;
            c = y.ExpiresTicks.CompareTo(x.ExpiresTicks);
            return c != 0 ? c : y.Id.CompareTo(x.Id);
        }
    }

    /// <summary><paramref name="keys"/> in the client's expected order for <paramref name="order"/> (<see cref="SearchKeyComparer"/>).
    /// The public search passes <paramref name="level"/> (<see cref="AuctionService.SearchAsync"/>).</summary>
    public static List<SearchKey> OrderSearchKeys(IEnumerable<SearchKey> keys, SearchOrder order, Func<uint, string> displayName,
        Func<uint, (int RequireLevel, int ItemLv)>? level = null)
    {
        var list = keys.ToList();
        list.Sort(new SearchKeyComparer(order, displayName, level));
        return list;
    }

    /// <summary>Parsed 1400 ReqAuctionArticleList.</summary>
    /// <param name="ItemIds">The name search's candidate templates, or null when the request lists none.</param>
    /// <param name="Order">The raw order byte (<see cref="SearchOrder"/>).</param>
    /// <param name="JobFilter">blob+14: <see cref="AnyCategory"/> = off, else the searcher's job (see
    /// <see cref="AuctionService.SearchAsync"/>).</param>
    public readonly record struct SearchRequest(
        ushort Page, byte HighCategory, byte MediumCategory, byte LowCategory, uint[]? ItemIds, SearchOrder Order,
        byte JobFilter = AnyCategory);

    /// <summary>"Any" in a category filter byte: 0xFF in all three, and also 0 in the two sub-categories.</summary>
    public const byte AnyCategory = 0xFF;

    /// <summary>
    /// Whether 1400's blob+14 asks for the class filter. 0xFF means off; a byte
    /// that names no job bit (0, or past bit 63 of Require_Job) is taken as off rather than guessed at.
    /// </summary>
    public static bool IsJobFilter(byte job) => job is >= MinJobFilter and <= MaxJobFilter;

    /// <summary>Lowest job number of the class filter: job j tests bit (j - 1) of Require_Job.</summary>
    private const byte MinJobFilter = 1;

    /// <summary>Highest job number of the class filter; it tests bit 62, so the sign bit of the 64-bit mask is never used.</summary>
    private const byte MaxJobFilter = 63;

    /// <summary>
    /// The class filter of the public search (<see cref="AuctionService.SearchAsync"/>): a template passes
    /// when its ItemInfo Require_Job is 0 (any class) or has bit (job - 1) set, the job-bit convention of
    /// StatService.StarterSkillGroups. A <paramref name="job"/> that is not a filter (<see cref="IsJobFilter"/>)
    /// passes everything.
    /// </summary>
    public static bool PassesJobFilter(long requireJob, byte job)
        => !IsJobFilter(job) || requireJob == 0 || (requireJob & (1L << (job - 1))) != 0;

    /// <summary>
    /// 2456's blob+12 / blob+14 for one page (<see cref="BuildSearch2456"/>): how many of the page's articles
    /// the requester is the top bidder of (entry+202) and how many it is selling (entry+194), matched by
    /// character id, the identities BuildEntry210 writes. Count the page AFTER it is cut.
    /// </summary>
    public static (ushort OwnTopBids, ushort OwnArticles) OwnCounts(IReadOnlyList<AuctionArticle> page, uint charId)
    {
        int bids = 0, own = 0;
        foreach (var a in page)
        {
            if (a.TopBidderCharId != 0 && a.TopBidderCharId == charId) bids++;
            if (a.SellerCharId == charId) own++;
        }
        return ((ushort)Math.Min(bids, ushort.MaxValue), (ushort)Math.Min(own, ushort.MaxValue));
    }

    /// <summary>
    /// 1400, U32 family: <c>[u32 blob length][u16 innerSize = 37, a constant][u16 tag 0xE360][u32 handle @4]
    /// [u16 page @8][u8 High_Category @10][u8 Medium_Category @11][u8 Low_Category @12][u8 @13][u8 job @14]
    /// [4 bytes @15][u16 n @19][n × u32 item id @21][u8 order][u8]</c>. The u32 equals the blob length and the blob
    /// is 23 + 4n. Every 2456 record's ItemInfo matches the request's High_Category (0xFF = any), Medium_Category
    /// when it is not 0/0xFF and Low_Category when it is not 0/0xFF, and lies in the id list when there is one
    /// (it is the client's name search).
    /// <para>
    /// @14 is the class checkbox: 0xFF = off, else the searcher's job, and the answer keeps only templates whose
    /// ItemInfo Require_Job is 0 or has bit (job - 1) set.
    /// </para>
    /// <para>
    /// Not applied: @13 reads 0xFF and @15/@16 read 0; @17/@18 read 0/100, 0/150 or 0xFF/0xFF, a range no
    /// item's Require_Level falls outside. The last byte (0xFF, 0, 1 or 2) may be a sex filter, but which value
    /// means which sex is not established. It stays unapplied.
    /// </para>
    /// </summary>
    public static bool TryParseSearch(ReadOnlySpan<byte> payload, out SearchRequest req)
    {
        req = default;
        if (payload.Length < SearchLengthPrefixSize + SearchIdListOffset) return false;
        var b = payload.Slice(SearchLengthPrefixSize);
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobTagOffset)) != MsgTagReqSearch) return false;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(SearchIdCountOffset));
        int orderOffset = SearchIdListOffset + sizeof(uint) * n;
        if (b.Length < orderOffset) return false;
        uint[]? ids = null;
        if (n > 0)
        {
            ids = new uint[n];
            for (int i = 0; i < n; i++) ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(SearchIdListOffset + sizeof(uint) * i));
        }
        // Raw: every value decodes as a bit field (SearchKeyComparer); the default when the byte is missing.
        var order = b.Length > orderOffset ? (SearchOrder)b[orderOffset] : SearchOrder.Default;
        req = new SearchRequest(BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobPageOffset)),
            b[SearchHighCategoryOffset], b[SearchMediumCategoryOffset], b[SearchLowCategoryOffset], ids, order, b[SearchJobOffset]);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // S2C
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Remaining minutes (entry+192, u16), counted between WHOLE minutes: floor(expiry to the minute) -
    /// floor(now to the minute), so the value drops by one each time the wall-clock minute changes, not each 60
    /// seconds after listing. A seconds-exact count fails.
    /// </summary>
    public static ushort RemainingMinutes(AuctionArticle a, DateTime nowUtc)
    {
        static long Minute(DateTime t) => t.Ticks / TimeSpan.TicksPerMinute;
        long left = Minute(a.ExpiresAt) - Minute(nowUtc);
        return (ushort)Math.Clamp(left, 0, ushort.MaxValue);
    }

    /// <summary>
    /// One AuctionEntry210. +0 article uid; +8 the item as the bag serializes it, parked in container 103,
    /// slot 0; +134 the current price: the start price until the first bid, then the top bid
    /// (<see cref="CurrentPrice"/>); +142 instant price (+134/+142 equal 1403's blob+20/+28 on a new listing);
    /// +150 a name wchar[21]; +192 remaining minutes; +194 the seller's 8-byte character uid (== the character uid of the session
    /// that lists it); +202 the top bidder's 8-byte character uid, zero with no bid.
    /// On a bid, +134 moves from the start price to the bids in minimum-bid steps
    /// (<see cref="MinimumBid"/>) while +202 changes bidder. The client reads +202 as the top
    /// bidder (it refuses a bid with string 8202 "Item already has a bid." when +202 is its own uid, and writes
    /// its own uid there when it bids).
    /// +150 is the SELLER's name in the public search (also on articles with a bidder) and the BIDDER column in the
    /// seller's own lists (2455/2458, <paramref name="withSellerName"/> false): the client prints string 8060
    /// "No Bidder" when +150 is empty, and the sell-at-bidding-price button refuses with 7972 "There are no
    /// bidders for the current item." when it is empty. The 2455/2458 records carry it empty when there is no bid,
    /// the no-bid value this writes too.
    /// The item is the full record, not a bare template: it can carry durability, a bestowed element at item+68
    /// and a filled rune-hole block at item+33..+67 (kind, Reinforce_ID, puncher template). So the record
    /// gets the article's element (<see cref="AuctionArticle.Element"/>) and its rune holes from the stored
    /// CardsJson through <see cref="ItemWorkbench.ApplyRuneholes"/>, as the bag's own records do, with the
    /// puncher template from the article's <see cref="AuctionArticle.SocketPunchersJson"/>.
    /// <paramref name="withSellerName"/>: true for the lists of other sellers' articles (2456, 2457), false for
    /// the seller's own (2455, 2458), where +150 is the top bidder's name instead.
    /// </summary>
    public static byte[] BuildEntry210(AuctionArticle a, DateTime nowUtc, bool withSellerName, GameDataStore store)
        => BuildEntry210Core(a, nowUtc, withSellerName, store);

    /// <summary>
    /// <see cref="BuildEntry210(AuctionArticle, DateTime, bool, GameDataStore)"/> without the game tables:
    /// the rune-hole block needs ItemInfo / ItemSocketGroup / ItemReinforceInfo to resolve (hole kind and
    /// Reinforce_ID are table lookups), so it stays zero here. Only for code with no GameDataStore (the
    /// entry-layout tests); every live caller passes the store.
    /// </summary>
    public static byte[] BuildEntry210(AuctionArticle a, DateTime nowUtc, bool withSellerName)
        => BuildEntry210Core(a, nowUtc, withSellerName, null);

    private static byte[] BuildEntry210Core(AuctionArticle a, DateTime nowUtc, bool withSellerName, GameDataStore? store)
    {
        var snapshot = new InventoryItem
        {
            Id = a.ItemUid,
            CharacterId = a.SellerCharId,
            ItemId = a.ItemId,
            Count = a.Count > 0 ? a.Count : 1,
            Refine = a.Refine,
            Durability = a.Durability,
            CardsJson = a.CardsJson,
            Element = a.Element,
            DyeColor = a.DyeColor,
            SocketPunchersJson = a.SocketPunchersJson,
            DyeColor2 = a.DyeColor2,
            DyeColor3 = a.DyeColor3,
            FxEnchantIndex = a.FxEnchantIndex,
            EnchantReinforceId = a.EnchantReinforceId,
            EnchantSeconds = a.EnchantSeconds,
            Bound = a.Bound,
            AbilityValue = a.AbilityValue,
            StorageType = BagStorageType,
            Slot = ItemSlot,
        };
        var item = store != null
            ? ItemWorkbench.RecordWithRuneholes(snapshot, store)
            : WorldEntryService.BuildItemRecord126(snapshot);
        item[ItemRecordContainerOffset] = ItemContainer;
        item[ItemRecordSlotOffset] = ItemSlot;

        using var pw = new PacketWriter(EntryLength);
        pw.WriteUInt64(a.Uid);                                  // +0
        pw.WriteBytes(item);                                    // +8   (126)
        pw.WriteUInt64((ulong)Math.Max(0, CurrentPrice(a)));    // +134
        pw.WriteUInt64((ulong)Math.Max(0, a.BuyoutPrice));      // +142
        WriteFixedUtf16(pw, withSellerName ? a.SellerName : (a.TopBidderCharId != 0 ? a.TopBidderName : string.Empty), EntryNameChars * sizeof(char)); // +150
        pw.WriteUInt16(RemainingMinutes(a, nowUtc));            // +192
        pw.WriteUInt64(a.SellerCharUid);                        // +194
        pw.WriteUInt64(a.TopBidderCharId != 0 ? a.TopBidderCharUid : 0); // +202
        return pw.ToArray();
    }

    /// <summary>
    /// 2455 OnAuctionSellList / 2457 OnAuctionTenderList (same shape):
    /// [u16 tag][u32 requester handle][u16 page @8][u16 total @10][u16 count @12][count × 210 @14].
    /// 14 + 210·count == blob length; handle and page echo the request.
    /// </summary>
    public static byte[] BuildList(ushort msgTag, uint handle, ushort page, int total, IReadOnlyList<byte[]> entries)
    {
        using var pw = new PacketWriter(16 + EntryLength * entries.Count);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(msgTag);
            p.WriteUInt32(handle);                                    // blob+4
            p.WriteUInt16(page);                                      // blob+8
            p.WriteUInt16((ushort)Math.Clamp(total, 0, ushort.MaxValue)); // blob+10
            p.WriteUInt16((ushort)entries.Count);                    // blob+12
            foreach (var e in entries) p.WriteBytes(e);               // blob+14
        });
        return pw.ToArray();
    }

    /// <summary>
    /// 2456 OnAuctionSearch: [u16 tag][u32 requester handle @4][u16 page @8]
    /// [u16 total @10][u16 own top bids @12][u16 own articles @14][u16 count @16][count × 210 @18]. blob == 18 +
    /// 210·count, page echoes the request, count == min(10, total − 10·(page−1)). An empty answer has total 0 and
    /// count 0.
    /// <para>
    /// <paramref name="ownArticles"/> (@14) is how many records ON THIS PAGE the requester is selling (entry+194
    /// == the requester's character uid).
    /// <paramref name="ownTopBids"/> (@12) is how many records on this page the requester is the top
    /// bidder of (entry+202 == that uid); it is 0 when there is no such record, even on pages with other players'
    /// bids.
    /// </para>
    /// </summary>
    public static byte[] BuildSearch2456(uint handle, ushort page, long total, IReadOnlyList<byte[]> entries,
        ushort ownTopBids = 0, ushort ownArticles = 0)
    {
        using var pw = new PacketWriter(20 + EntryLength * entries.Count);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSearch);
            p.WriteUInt32(handle);                                        // blob+4
            p.WriteUInt16(page);                                          // blob+8
            p.WriteUInt16((ushort)Math.Clamp(total, 0, ushort.MaxValue)); // blob+10
            p.WriteUInt16(ownTopBids);                                    // blob+12
            p.WriteUInt16(ownArticles);                                   // blob+14
            p.WriteUInt16((ushort)entries.Count);                        // blob+16
            foreach (var e in entries) p.WriteBytes(e);                   // blob+18
        });
        return pw.ToArray();
    }

    /// <summary>
    /// 2458 OnAuctionSellStart success, blob 239: [tag][handle][i16 result 0 @8][entry @10][u64 wallet after the
    /// fee @220][u64 bag item uid @228][u8 bag slot @236][u8 0 @237][u8 count left @238]. The entry has the
    /// request's uid, count and prices, container 103 / slot 0, an empty name, no bidder and 10080 or 1440
    /// minutes; @228 == the request's uid, @236 == its slot, @237 == 0. The client reads @228 to find the bag
    /// item and re-places it at @236 with count @238. The refusal form is
    /// <see cref="BuildSellStartRefusal2458"/>.
    /// </summary>
    public static byte[] BuildSellStart2458(uint handle, byte[] entry210, long wallet, ulong bagItemUid, byte bagSlot, byte countLeft)
        => BuildSellStart2458Core(handle, ResultSuccess, entry210, wallet, bagItemUid, bagSlot, countLeft);

    private static byte[] BuildSellStart2458Core(uint handle, ushort result, byte[] entry210, long wallet, ulong bagItemUid, byte bagSlot, byte countLeft)
    {
        using var pw = new PacketWriter(256);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSellStart);
            p.WriteUInt32(handle);                         // blob+4
            p.WriteUInt16(result);                         // blob+8 result
            p.WriteBytes(entry210);                        // blob+10
            p.WriteUInt64((ulong)Math.Max(0, wallet));     // blob+220
            p.WriteUInt64(bagItemUid);                     // blob+228
            p.WriteByte(bagSlot);                          // blob+236
            p.WriteByte(0);                                // blob+237
            p.WriteByte(countLeft);                        // blob+238
        });
        return pw.ToArray();
    }

    /// <summary>2460 OnAuctionSellCancel, blob 26: [tag][handle][i16 result][u64 article][u64 item].</summary>
    public static byte[] BuildSellCancel2460(uint handle, ushort result, ulong articleUid, ulong itemUid)
    {
        using var pw = new PacketWriter(32);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSellCancel);
            p.WriteUInt32(handle);     // blob+4
            p.WriteUInt16(result);     // blob+8
            p.WriteUInt64(articleUid); // blob+10
            p.WriteUInt64(itemUid);    // blob+18
        });
        return pw.ToArray();
    }

    /// <summary>2461 OnAuctionBuySellingPrice, blob 34: [tag][handle][i16 result][u64 article][u64 item]
    /// [u64 buyer wallet after paying @26].</summary>
    public static byte[] BuildBuySellingPrice2461(uint handle, ushort result, ulong articleUid, ulong itemUid, long wallet)
    {
        using var pw = new PacketWriter(40);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagBuySellingPrice);
            p.WriteUInt32(handle);                      // blob+4
            p.WriteUInt16(result);                      // blob+8
            p.WriteUInt64(articleUid);                  // blob+10
            p.WriteUInt64(itemUid);                     // blob+18
            p.WriteUInt64((ulong)Math.Max(0, wallet));  // blob+26
        });
        return pw.ToArray();
    }

    /// <summary>
    /// 2693 NfyAuctionSystemMessage — a RAW 28-byte body (no length prefix; read as Blob&lt;28&gt;):
    /// [u32 handle][u32 type][u64 article uid][u32 item template id][u64 amount]. Types in use: 1 (sold), 2
    /// (bought), 7 (cancelled).
    /// </summary>
    public static byte[] BuildSystemMessage2693(uint handle, uint type, ulong articleUid, uint itemId, long amount)
    {
        var b = new byte[SystemMessageLength];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(SystemMessageHandleOffset), handle);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(SystemMessageTypeOffset), type);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(SystemMessageArticleUidOffset), articleUid);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(SystemMessageItemIdOffset), itemId);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(SystemMessageAmountOffset), (ulong)Math.Max(0, amount));
        return b;
    }

    private static void WriteFixedUtf16(PacketWriter pw, string s, int byteLen)
    {
        var buf = new byte[byteLen];
        int chars = Math.Min(s.Length, byteLen / sizeof(char) - 1); // keep a NUL terminator
        for (int i = 0; i < chars; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(i * sizeof(char)), s[i]);
        pw.WriteBytes(buf);
    }
}

/// <summary>
/// Consignment-sale (auction house) logic: listing, the seller's list, the public search, cancel, instant buy
/// and expiry.
/// Not registered in DI (mirrors <see cref="MailService"/>): a handler builds one with <see cref="From"/>.
/// Items and money that leave an article travel by MAIL: the cancelled item returns in a
/// mail (its 2446 attachment is the cancelled uid), a bought item reaches the buyer's mailbox (2449 before
/// 2461, then 1397/1396 claim it), and the proceeds reach the seller's (a 95,000,000-zeny mail for an
/// article sold at 100,000,000). Bids (1407), accepting one (1404) and the top-bid list (1402) are in
/// AuctionBidding.cs and AuctionBidAccept.cs.
/// </summary>
public sealed partial class AuctionService
{
    private readonly IAuctionRepository _repo;
    private readonly IEconomyMailPort _mailPort;
    private readonly IAuctionNotifier _notifier;
    private readonly ILogger _log;

    private AuctionService(IAuctionRepository repo, IMailRepository mails, MailService mailService, PlayerStateManager players, BroadcastService broadcast, ILogger log)
    {
        _repo = repo;
        _mailPort = new EconomyMailAdapter(mails, mailService);
        _notifier = new AuctionNotifierAdapter(players, broadcast, log);
        _log = log;
    }

    public static AuctionService From(IServiceProvider services, PlayerStateManager players, BroadcastService broadcast, ILogger log) =>
        new(services.GetRequiredService<IAuctionRepository>(), services.GetRequiredService<IMailRepository>(),
            MailService.From(services, players, broadcast, log), players, broadcast, log);

    // ---------------------------------------------------------------------------------------------
    // Expiry sweep
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How often the sweep looks for articles whose duration ran out. An unsold item is mailed at expiry,
    /// not when its seller next looks; one minute keeps the lag small.
    /// </summary>
    public static readonly TimeSpan ExpirySweepPeriod = TimeSpan.FromMinutes(1);

    /// <summary>Articles closed per query; a sweep keeps querying until a batch comes back short.</summary>
    private const int ExpirySweepBatch = 100;

    private static int _sweepStarted; // 0 = not yet
    private static int _sweepRunning; // 0 = idle

    /// <summary>
    /// Starts the process-wide expiry sweep once: every <see cref="ExpirySweepPeriod"/> it returns every
    /// seller's due articles (<see cref="ReturnExpiredAsync"/>), whether or not the seller is online or ever
    /// opens their sell list again. Later calls are no-ops. Everything it holds is a singleton
    /// (<paramref name="scopes"/>, <paramref name="players"/>, <paramref name="broadcast"/>) and each pass
    /// resolves the <c>IAuctionRepository</c> from a fresh scope, so nothing scoped outlives
    /// its request. Called from the 1403 handler's constructor: RMI handlers are singletons that
    /// WireZoneHandlers instantiates once at boot (<c>GetServices&lt;IZoneRmiHandler&gt;()</c>), so the sweep
    /// runs from server start, articles that ran out while the server was down included.
    /// </summary>
    public static void StartExpirySweep(IServiceScopeFactory scopes, PlayerStateManager players, BroadcastService broadcast, ILogger log)
    {
        if (Interlocked.Exchange(ref _sweepStarted, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(ExpirySweepPeriod);
            while (await timer.WaitForNextTickAsync())
                await SweepOnceAsync(scopes, players, broadcast, log);
        });
    }

    private static async Task SweepOnceAsync(IServiceScopeFactory scopes, PlayerStateManager players, BroadcastService broadcast, ILogger log)
    {
        if (Interlocked.Exchange(ref _sweepRunning, 1) != 0) return; // the previous pass is still running
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var repo = sp.GetService<IAuctionRepository>();
            if (repo == null) return;
            var service = new AuctionService(repo, sp.GetRequiredService<IMailRepository>(),
                MailService.From(sp, players, broadcast, log), players, broadcast, log);
            int returned = await service.ReturnExpiredAsync(DateTime.UtcNow);
            if (returned > 0) log.LogInformation("Auction expiry sweep returned {Count} unsold article(s) by mail", returned);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Auction expiry sweep failed; retrying next period");
        }
        finally
        {
            Interlocked.Exchange(ref _sweepRunning, 0);
        }
    }

    /// <summary>
    /// Closes every article of any seller whose duration ran out by <paramref name="nowUtc"/> and settles it
    /// (<see cref="SettleExpiredAsync"/>: the item back to its seller, or to its top bidder when it has one),
    /// then sends each online receiver one 2449 with their unread count. Each close is the atomic
    /// Active -> Expired update, so an article that a buyer, its seller or the lazy seller-list path closes at
    /// the same moment is mailed once only. The whole pass stamps its mails with <paramref name="nowUtc"/>:
    /// a batch of expiry mails carries one identical sent time, the mark of one batch, not of each article's own
    /// expiry.
    /// Returns how many articles it closed.
    /// </summary>
    public async Task<int> ReturnExpiredAsync(DateTime nowUtc)
    {
        int returned = 0;
        var receivers = new HashSet<uint>();
        while (true)
        {
            var due = await _repo.GetDueAsync(nowUtc, ExpirySweepBatch);
            int closedThisBatch = 0;
            foreach (var article in due)
            {
                var closed = await _repo.TryExpireAsync(article.Uid, nowUtc);
                if (closed == null) continue; // closed another way in between
                closedThisBatch++;
                try
                {
                    await SettleExpiredAsync(closed, nowUtc, receivers);
                    returned++;
                }
                catch (Exception ex)
                {
                    // Not re-opened: if the insert did land, a retry would mail the item twice. The article
                    // document keeps the whole item snapshot and the top bid (state Expired), so it can be
                    // recovered by hand.
                    _log.LogError(ex, "Auction {Uid} expired but its item {ItemId} x{Count} (seller {Seller}, top bidder {Bidder}) could not be mailed",
                        closed.Uid, closed.ItemId, closed.Count, closed.SellerCharId, closed.TopBidderCharId);
                }
            }
            // Stop on a short batch, and on one where nothing could be closed, so an article that stays
            // Active against the conditional close can never spin this loop (the next period retries it).
            if (due.Count < ExpirySweepBatch || closedThisBatch == 0) break;
        }
        foreach (var receiver in receivers)
            await _mailPort.NotifyNewMailAsync(receiver);
        return returned;
    }

    /// <summary>
    /// Settles an article that <see cref="IAuctionRepository.TryExpireAsync"/> just closed. Without a bid its
    /// item goes back to the seller (<see cref="AuctionMailKind.Expired"/>). With one, the top bidder wins it
    /// at the top bid (<see cref="AwardToTopBidderAsync"/>): the bid's zeny was taken from the bidder when it
    /// was placed, so returning the item alone would keep the bidder's zeny with nobody. That the bid wins at
    /// expiry is not established. It is what the client's texts allow: the auction mail kinds and 2693 types have
    /// a text for a bid that wins (kind 4 / type 4, 8654 "@1 item that you bid on has been auctioned off") and for
    /// a bid returned because it was outbid (kind 6 / type 5) or the seller cancelled (kind 8 / type 8), but none
    /// for a bid returned at expiry.
    /// Every receiver's id goes into <paramref name="mailReceivers"/> for one 2449 once the caller is done.
    /// </summary>
    private async Task SettleExpiredAsync(AuctionArticle closed, DateTime nowUtc, HashSet<uint> mailReceivers)
    {
        if (closed.TopBidderCharId != 0)
        {
            await AwardToTopBidderAsync(closed, nowUtc, notifyBidder: true, mailReceivers);
            return;
        }
        await ReturnItemToSellerAsync(closed, AuctionMailKind.Expired, nowUtc);
        mailReceivers.Add(closed.SellerCharId);
    }

    /// <summary><see cref="SettleExpiredAsync"/> for one article, with its 2449s sent at once.</summary>
    private async Task SettleExpiredNowAsync(AuctionArticle closed, DateTime nowUtc)
    {
        var receivers = new HashSet<uint>();
        await SettleExpiredAsync(closed, nowUtc, receivers);
        foreach (var receiver in receivers)
            await _mailPort.NotifyNewMailAsync(receiver);
    }

    // The registration fee (RegistrationFee) and the listing rules (CheckSellStart) are in AuctionSellStartRules.cs.

    /// <summary>
    /// What the seller receives for a sale: 95% of the price, computed in single precision. The PRICE is rounded
    /// to float32 before the multiply too: the proceeds are float32(float32(price) × 0.95), which for a price of
    /// 10,000,000,000 gives 9,500,000,256 and for 100,000,000 gives exactly 95,000,000. Neither the exact product
    /// nor float32(price × 0.95) gives the same values for every price.
    /// </summary>
    public static long SellerProceeds(long price) => price <= 0 ? 0 : (long)(float)((float)price * SellerProceedsRate);

    /// <summary>The seller's share of a sale (95%); the other 5% is the house's cut. A double on purpose: the
    /// product is formed in double and then narrowed to float32 (<see cref="SellerProceeds"/>).</summary>
    private const double SellerProceedsRate = 0.95;

    /// <summary>Inventory <c>StorageType</c> of the character's bag (1 = worn equipment, 2 = bank).</summary>
    private const byte BagStorageType = 0;

    // ---------------------------------------------------------------------------------------------
    // Sell start (1403)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// What a 1403 came to. <see cref="Result"/> 0: the article was stored, and <see cref="Wallet"/> is the
    /// wallet after the fee. Otherwise the 2458 refusal code, with <see cref="Article"/> null and
    /// <see cref="Wallet"/> the wallet unchanged (the value the refusals carry at @220).
    /// </summary>
    public readonly record struct SellStartOutcome(ushort Result, AuctionArticle? Article, long Wallet, ulong BagItemUid, byte BagSlot, byte CountLeft);

    /// <summary>
    /// Takes the item (or the listed part of its stack) out of the bag, charges the registration fee and
    /// stores the article. A request that breaks a listing rule (<see cref="CheckSellStart"/>) changes nothing
    /// and comes back with its 2458 code; so does an insert that fails, after the item is put back.
    /// The first read only feeds the cheap pre-checks and the fee; the take itself re-reads the item under the
    /// seller's inventory gate and writes with the owner-filtered atomic calls (<c>RemoveItemAsync(id, charId)</c>
    /// / <c>AdjustCountAsync</c>), so a stack that a trade, a sale or a mail attach took meanwhile is listed
    /// by nobody: the loser gets 16304 with its fee handed back (MailService.StoreAsync takes the same way).
    /// </summary>
    public async Task<SellStartOutcome> StartSaleAsync(PlayerState seller, AuctionPackets.SellStartRequest req,
        IInventoryRepository inventory, ICharacterRepository characters, GameDataStore store)
    {
        var item = await inventory.GetByIdAsync(req.ItemUid);
        long fee = item != null ? RegistrationFee(store.GetItem(item.ItemId), req.Count, req.Hours) : 0;
        int liveArticles = await _repo.CountActiveBySellerAsync(seller.CharacterId, DateTime.UtcNow);
        long priceCap = GuildFundsInHandPackets.WalletCap(store.ConfigDef);
        ushort code = CheckSellStart(req, item, seller.CharacterId, priceCap, fee, seller.Gold, liveArticles,
            item != null ? store.GetItem(item.ItemId) : null);
        if (code != AuctionPackets.ResultSuccess || item == null)
            return Refuse(code != AuctionPackets.ResultSuccess ? code : AuctionPackets.ResultSellItemMismatch,
                $"item {req.ItemUid} x{req.Count} (bag stack {item?.Count}), {req.Hours}h, start {req.StartPrice}, instant {req.BuyoutPrice}, " +
                $"fee {fee}, wallet {seller.Gold}, {liveArticles} live articles");

        // The transactional listing (charge fee, take item into escrow, store the article, compensate on failure)
        // is the Economy application flow; the host keeps the game-data validation above and the wire below.
        var app = new AuctionApplicationService(_repo, new InventoryEscrowAdapter(inventory));
        var cmd = new AuctionListCommand(seller.CharacterId, seller.Name, seller.CharacterUid, req.ItemUid,
            req.Count, (long)req.StartPrice, (long)req.BuyoutPrice, req.Hours, fee);
        var result = await app.ListAsync(cmd, seller,
            ex => _log.LogError(ex, "Auction insert failed; restoring the item to {Seller}", seller.Name));

        switch (result.Outcome)
        {
            case AuctionListOutcome.NotEnoughMoney:
                return Refuse(AuctionPackets.ResultNotEnoughMoney, $"fee {fee}, wallet {seller.Gold}");
            case AuctionListOutcome.ItemGone:
                return Refuse(AuctionPackets.ResultSellItemMismatch, $"item {req.ItemUid} x{req.Count} was taken from the bag meanwhile");
            case AuctionListOutcome.InternalError:
                // 16301: 8606 "Failed purchase registration due to an internal system error."
                return new SellStartOutcome(AuctionPackets.ResultSellInternalError, null, seller.Gold, 0, 0, 0);
        }

        var article = result.Article!;
        // The slot the client knows the item by: 2458 blob+236 is the listed item's slot.
        byte bagSlot = WorldEntryService.BuildItemRecord126(
            InventoryEscrowAdapter.ToInventoryItem(result.Taken!, seller.CharacterId))[AuctionPackets.ItemRecordSlotOffset];

        await characters.UpdateGoldAsync(seller.CharacterId, seller.Gold);

        _log.LogInformation("Auction {Uid} listed by {Seller}: item {ItemId} x{Count}, start {Start}, instant {Buyout}, {Hours}h, fee {Fee}",
            article.Uid, seller.Name, article.ItemId, article.Count, article.StartPrice, article.BuyoutPrice, req.Hours, fee);
        return new SellStartOutcome(AuctionPackets.ResultSuccess, article, seller.Gold, result.Taken!.Id, bagSlot, (byte)Math.Clamp(result.CountLeft, 0, byte.MaxValue));

        SellStartOutcome Refuse(ushort result, string why)
        {
            _log.LogInformation("Auction listing by {Seller} refused with {Result}: {Why}", seller.Name, result, why);
            return new SellStartOutcome(result, null, seller.Gold, 0, 0, 0);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Seller's list (1401)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The seller's live articles, after settling (by mail) any whose duration ran out — the client string
    /// 2326 "The item you're trying to sell will be sent to your Mailbox soon." names the mailbox as where an
    /// unsold item goes. The expiry sweep (<see cref="StartExpirySweep"/>) normally settles them first; this
    /// only catches an article that ran out since its last pass, so the list never shows a dead one.
    /// </summary>
    public async Task<List<AuctionArticle>> GetSellerArticlesAsync(uint sellerCharId)
    {
        var now = DateTime.UtcNow;
        var receivers = new HashSet<uint>();
        foreach (var due in await _repo.GetDueBySellerAsync(sellerCharId, now))
        {
            var closed = await _repo.TryExpireAsync(due.Uid, now);
            if (closed == null) continue;
            await SettleExpiredAsync(closed, now, receivers);
        }
        foreach (var receiver in receivers)
            await _mailPort.NotifyNewMailAsync(receiver);
        return (await _repo.GetActiveBySellerAsync(sellerCharId, now)).ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Public search (1400)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One page of every seller's live articles that pass the request's filters, and how many pass in all.
    /// The categories are the listed template's ItemInfo High_Category / Medium_Category / Low_Category, the
    /// fields the answers match (<see cref="AuctionPackets.TryParseSearch"/>); the name search's id list
    /// narrows by template. Low_Category (@12) is a real filter, not a hint. The searcher's own
    /// articles are listed too, while <see cref="BuyAsync"/> refuses one's own
    /// article (string_ui 1547). The page follows the client's order (<see cref="AuctionPackets.SearchKeyComparer"/>),
    /// keyed on each template's English display name and (Require_Level, Item_Lv) from
    /// <see cref="AuctionItemCatalog"/>.
    /// The class checkbox (<see cref="AuctionPackets.SearchRequest.JobFilter"/>, blob+14) keeps a template
    /// whose ItemInfo Require_Job is 0 or has bit (job - 1) set, the job-bit convention of
    /// StatService.StarterSkillGroups (<c>1u &lt;&lt; (job - 1)</c>; see <see cref="AuctionPackets.TryParseSearch"/>).
    /// </summary>
    public async Task<(long total, List<AuctionArticle> page)> SearchAsync(AuctionPackets.SearchRequest req, GameDataStore store)
    {
        var now = DateTime.UtcNow;
        IReadOnlyCollection<uint>? ids = req.ItemIds;
        bool anyHigh = req.HighCategory == AuctionPackets.AnyCategory;
        bool anyMedium = req.MediumCategory is 0 or AuctionPackets.AnyCategory;
        bool anyLow = req.LowCategory is 0 or AuctionPackets.AnyCategory;
        bool byJob = AuctionPackets.IsJobFilter(req.JobFilter);
        if (!anyHigh || !anyMedium || !anyLow || byJob)
        {
            var named = req.ItemIds != null ? new HashSet<uint>(req.ItemIds) : null;
            var matching = new List<uint>();
            foreach (var id in await _repo.GetActiveItemIdsAsync(now))
            {
                if (named != null && !named.Contains(id)) continue;
                var d = store.GetItem(id);
                if (d == null) continue;
                if (!anyHigh && d.High_Category != req.HighCategory) continue;
                if (!anyMedium && d.Medium_Category != req.MediumCategory) continue;
                // A template the catalog does not know is not filtered out (the tables may be missing).
                if (!anyLow && AuctionItemCatalog.LowCategory(id) is { } low && low != req.LowCategory) continue;
                if (byJob && !AuctionPackets.PassesJobFilter(d.Require_Job, req.JobFilter)) continue;
                matching.Add(id);
            }
            ids = matching;
        }

        int page = Math.Max(1, (int)req.Page);
        return await SearchActivePageAsync(ids, req.Order,
            (page - 1) * AuctionPackets.PageSize, AuctionPackets.PageSize, now,
            id => AuctionItemCatalog.DisplayName(id, store),
            id => AuctionItemCatalog.Level(id, store));
    }

    /// <summary>
    /// One page of the public search: the sort keys of EVERY matching article are ordered in memory before the
    /// page is cut (the client orders by display name, which no stored field holds), and only that page's documents
    /// are then read in full. Sorting after a database Skip/Limit would show the right order on the wrong records.
    /// The price key is entry+134 (<see cref="AuctionPackets.SearchKeyOf"/>, the top bid once there is one);
    /// ExpiresAt is in the projection for the remaining-time column.
    /// </summary>
    private async Task<(long total, List<AuctionArticle> page)> SearchActivePageAsync(IReadOnlyCollection<uint>? itemIds,
        AuctionPackets.SearchOrder order, int skip, int take, DateTime nowUtc, Func<uint, string> displayName,
        Func<uint, (int RequireLevel, int ItemLv)> level)
    {
        if (itemIds is { Count: 0 }) return (0, new List<AuctionArticle>());

        var projected = await _repo.GetActiveSortRowsAsync(itemIds, nowUtc);
        var keys = projected.Select(AuctionPackets.SearchKeyOf).ToList();

        var pageIds = AuctionPackets.OrderSearchKeys(keys, order, displayName, level)
            .Skip(Math.Max(0, skip)).Take(Math.Max(1, take))
            .Select(k => k.Id).ToList();
        if (pageIds.Count == 0) return (keys.Count, new List<AuctionArticle>());

        var docs = await _repo.GetByIdsAsync(pageIds);
        var byId = docs.ToDictionary(d => d.Id);
        // An article closed between the two reads drops out of this page rather than being shown stale.
        var page = pageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        return (keys.Count, page);
    }

    // ---------------------------------------------------------------------------------------------
    // Cancel (1405)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Closes the seller's own active article and mails the item back. Null if it is gone. An article with a
    /// bid can be cancelled too (the Tab3 cancel button confirms 8586 and sends 1405 without looking at +150/+202),
    /// and the top bidder's escrowed zeny goes back
    /// by mail kind 8 with 2693 type 8, the pair whose client text is exactly that case (8657 "The auction of
    /// item @1 was cancelled. Your bid money is returned."; <see cref="AuctionBidMailKind.CancelRefund"/>).
    /// The refund reads the bid from the document the cancel closed, so a bid that landed first is the one
    /// refunded and a bid that lands after the close fails its own Active condition.
    /// </summary>
    public async Task<AuctionArticle?> CancelAsync(PlayerState seller, ulong articleUid)
    {
        var now = DateTime.UtcNow;
        var closed = await _repo.TryCancelAsync(articleUid, seller.CharacterId, now);
        if (closed == null) return null;
        await ReturnItemToSellerAsync(closed, AuctionMailKind.Cancelled, now);
        if (closed.TopBidderCharId != 0)
            await RefundTopBidAsync(closed, AuctionBidMailKind.CancelRefund, AuctionPackets.NotifyCancelledWithBid, notifyBidder: true, now);
        _log.LogInformation("Auction {Uid} cancelled by {Seller}; item {ItemId} x{Count} mailed back",
            closed.Uid, seller.Name, closed.ItemId, closed.Count);
        return closed;
    }

    // ---------------------------------------------------------------------------------------------
    // Instant buy (1406)
    // ---------------------------------------------------------------------------------------------

    /// <summary><see cref="Gone"/>: the article is sold, expired or cancelled. <see cref="NotEnoughMoney"/>: the
    /// buyer's wallet is below the instant price. <see cref="Refused"/>: an article the buyer cannot buy
    /// (their own, or one with no instant price). Every result gets a 2461 (AuctionBuySellingPriceHandler).</summary>
    public enum BuyResult { Bought, Gone, Refused, NotEnoughMoney }

    /// <summary>
    /// Buys an active article at its instant price: the buyer pays exactly that price, the item goes to the
    /// buyer's mailbox and the
    /// proceeds (<see cref="SellerProceeds"/>) to the seller's. The wallet is changed only after the article
    /// was closed atomically, so an article can be paid for once.
    /// An instant buy also ends the bidding: the top bidder's escrowed zeny goes back by mail kind 6 with
    /// 2693 type 6, whose client text is that case (8013 "Another user purchased @1 using the Instant Purchase
    /// option."). No kind is reserved for it; kind 6's text (8656 "A higher bid for item @1 has appeared. Your
    /// bid is returned.") is the refund text that fits a higher payment (<see cref="AuctionBidMailKind"/>).
    /// A top bidder who instant-buys the article themselves is refunded the same way: every displaced bid
    /// leaves by mail, as string 1571 says bid zeny does.
    /// </summary>
    public async Task<(BuyResult result, AuctionArticle? article)> BuyAsync(PlayerState buyer, AuctionPackets.BuyRequest req,
        ICharacterRepository characters)
    {
        var now = DateTime.UtcNow;
        var article = await _repo.GetByUidAsync(req.ArticleUid);
        if (article == null || article.State != AuctionArticleState.Active) return (BuyResult.Gone, null);
        if (article.ExpiresAt <= now)
        {
            var expired = await _repo.TryExpireAsync(article.Uid, now);
            if (expired != null) await SettleExpiredNowAsync(expired, now);
            return (BuyResult.Gone, null);
        }
        // "You can't bid on your own item in an auction." (string_ui 1547)
        if (article.SellerCharId == buyer.CharacterId) return Refuse("own article");
        if (article.BuyoutPrice <= 0) return Refuse("article has no instant price");
        if ((ulong)article.BuyoutPrice != req.Price)
            _log.LogWarning("Auction {Uid}: {Buyer} offered {Offer}, instant price is {Price}; charging the stored price",
                article.Uid, buyer.Name, req.Price, article.BuyoutPrice);
        // The price is taken atomically BEFORE the article is sold; handed back when the article is gone.
        if (!buyer.TryDebit(article.BuyoutPrice)) return Refuse($"wallet {buyer.Gold} below {article.BuyoutPrice}", BuyResult.NotEnoughMoney);

        AuctionArticle? sold;
        try { sold = await _repo.TrySellAsync(article.Uid, buyer.CharacterId, now); }
        catch { buyer.Credit(article.BuyoutPrice); throw; }
        if (sold == null)
        {
            buyer.Credit(article.BuyoutPrice);
            return (BuyResult.Gone, null);
        }

        await characters.UpdateGoldAsync(buyer.CharacterId, buyer.Gold);

        await DeliverAsync(buyer.CharacterId, buyer.Name, sold, AuctionMailKind.Purchased, withItem: true, gold: 0, now);

        // Seller side, in order (2693 type 1, then 2449). A 1-zeny article pays (long)float32(0.95) = 0, so it
        // gets the notice but no empty mail.
        long proceeds = SellerProceeds(sold.BuyoutPrice);
        if (proceeds > 0)
            await DeliverAsync(sold.SellerCharId, sold.SellerName, sold, AuctionMailKind.Proceeds, withItem: false, gold: proceeds, now);
        await NotifySellerAsync(sold, proceeds);
        if (proceeds > 0)
            await _mailPort.NotifyNewMailAsync(sold.SellerCharId);

        // The bid the purchase displaced, read from the document the purchase closed (see the summary). Its
        // notice 8013 "Another user purchased @1..." is wrong for a top bidder who bought it, so they get the
        // refund mail alone.
        if (sold.TopBidderCharId != 0)
            await RefundTopBidAsync(sold, AuctionBidMailKind.OutbidRefund, AuctionPackets.NotifyInstantBoughtOverBid,
                notifyBidder: sold.TopBidderCharId != buyer.CharacterId, now);

        _log.LogInformation("Auction {Uid} bought by {Buyer} for {Price}; seller {Seller} receives {Proceeds}",
            sold.Uid, buyer.Name, sold.BuyoutPrice, sold.SellerName, proceeds);
        return (BuyResult.Bought, sold);

        (BuyResult, AuctionArticle?) Refuse(string why, BuyResult result = BuyResult.Refused)
        {
            _log.LogInformation("Auction {Uid} buy by {Buyer} refused: {Why}", req.ArticleUid, buyer.Name, why);
            return (result, null);
        }
    }

    /// <summary>2449 OnMailNew to <paramref name="charId"/> if online (unread count), via the mail system.</summary>
    public Task NotifyNewMailAsync(uint charId) => _mailPort.NotifyNewMailAsync(charId);

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>2693 type 1 to the seller, if online: the article, the item template and the proceeds.</summary>
    private Task NotifySellerAsync(AuctionArticle sold, long proceeds)
        => _notifier.SendSystemMessageAsync(sold.SellerCharId, AuctionPackets.NotifySold, sold.Uid, sold.ItemId, proceeds);

    /// <summary>The article's item back to its seller: <see cref="AuctionMailKind.Cancelled"/> or
    /// <see cref="AuctionMailKind.Expired"/>, stamped <paramref name="sentAtUtc"/>.</summary>
    private async Task ReturnItemToSellerAsync(AuctionArticle article, byte kind, DateTime sentAtUtc)
    {
        await DeliverAsync(article.SellerCharId, article.SellerName, article, kind, withItem: true, gold: 0, sentAtUtc);
    }

    /// <summary>
    /// Stores a mail carrying the article's item and/or zeny for <paramref name="receiverCharId"/>. Sender
    /// and title stay empty: auction mails carry empty sender and title strings and let the client
    /// write the text from the list-row kind (<see cref="AuctionMailKind"/>) and the item template at rec+58,
    /// which is the article's template on every such row, the item-less proceeds mail included.
    /// Every kind also keeps the article's instant price (<see cref="MailMessage.AuctionPrice"/>), the
    /// value the client's 2446 shows at record+173 on each of them — on the proceeds mail too, where it is the
    /// price the article sold at, not the zeny the mail carries.
    /// </summary>
    private async Task DeliverAsync(uint receiverCharId, string receiverName, AuctionArticle article, byte kind, bool withItem, long gold,
        DateTime sentAtUtc)
    {
        await _mailPort.SendAuctionMailAsync(receiverCharId, receiverName, kind, article.BuyoutPrice, gold,
            withItem ? article.ToStackSnapshot() : null, article.ItemId, sentAtUtc);
    }
}
