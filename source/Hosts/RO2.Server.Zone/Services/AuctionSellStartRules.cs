using System;
using RO2.Domain.Entities;
using RO2.Domain.GameData;

namespace RO2.Server.Zone.Services;

// 1403 ReqAuctionSellStart -> 2458 OnAuctionSellStart: the listing rules, the registration fee and the refusal
// answer. The flow itself is AuctionService.StartSaleAsync (AuctionHouse.cs).

public static partial class AuctionPackets
{
    // 2458 result codes of a refused listing, each with the text the client shows for it. 16302 (2114 "Not enough
    // money in your inventory.") is ResultNotEnoughMoney (AuctionBidding.cs).

    /// <summary>16301: 8606 "Failed purchase registration due to an internal system error."</summary>
    public const ushort ResultSellInternalError = 16301;

    /// <summary>16303: 8603 "Can only register an item if buyout price is higher than the starting bid price."</summary>
    public const ushort ResultSellBuyoutBelowStart = 16303;

    /// <summary>
    /// 16304: 8607 "Failed purchase registration due to incorrect item info." The general item-refusal code: the
    /// item is not in the seller's bag (for example the client sent the same 1403 twice and the first copy had
    /// already listed the item), or fails one of the other item checks of
    /// <see cref="AuctionService.CheckSellStart"/>.
    /// </summary>
    public const ushort ResultSellItemMismatch = 16304;

    /// <summary>
    /// The listing durations the client offers, in hours (1, 3 or 7 days: the days byte of the sell tab x 24, see
    /// <see cref="AuctionService.RegistrationFeeRate"/>). Any other byte used to list at the 1-day fee rate for up
    /// to 255 h.
    /// </summary>
    public static readonly byte[] AllowedSellHours = [24, 72, 168];

    /// <summary>16307: 8604 "Cannot register for purchase because the item quantity limit for auction has been exceeded."</summary>
    public const ushort ResultSellQuantityLimit = 16307;

    /// <summary>
    /// Live articles one seller may have: the client refuses with 8631 "Exceeded the item quantity limit a player
    /// can sell." when the seller's article total is not below 100.
    /// </summary>
    public const int MaxLiveArticles = 100;

    /// <summary>
    /// 2458 refusal, blob 239: <c>[u16 239][u16 58215]
    /// [u32 handle @4][i16 result @8][210 zero bytes @10][u64 CURRENT wallet @220][u64 0 @228][u8 0 @236]
    /// [u8 0 @237][u8 0 @238]</c>. The wallet is the seller's current balance, unchanged because no fee is
    /// charged. On a nonzero result at @8 the client clears the auction manager's request-pending flag, shows
    /// the message for the code and reads nothing else. The answer matters more than its fields: the client sets
    /// that flag when it sends the 1403 and gates search, paging, bid, buyout and list paging on it being clear,
    /// so an unanswered 1403 locks the auction window.
    /// </summary>
    public static byte[] BuildSellStartRefusal2458(uint handle, ushort result, long wallet)
        => BuildSellStart2458Core(handle, result, new byte[EntryLength], wallet,
            bagItemUid: 0, bagSlot: 0, countLeft: 0);
}

public sealed partial class AuctionService
{
    /// <summary>Inventory <c>StorageType</c> of the character's bag (1 = worn equipment, 2 = bank).</summary>
    private const byte SellBagStorageType = 0;

    /// <summary>Hours in a day: the 1403 carries the listing duration in hours, the fee tiers are per day.</summary>
    private const int HoursPerDay = 24;

    /// <summary>Registration fee rate of a 1-day listing, and the default for any other duration.</summary>
    private const float FeeRateOneDay = 0.03f;

    /// <summary>Registration fee rate of a 3-day listing.</summary>
    private const float FeeRateThreeDays = 0.07f;

    /// <summary>Registration fee rate of a 7-day listing.</summary>
    private const float FeeRateSevenDays = 0.15f;

    // IEEE 754 single-precision layout, used to read a rate as significand x 2^-scale.
    private const int SingleExponentShift = 23;
    private const uint SingleExponentMask = 0xFF;
    private const uint SingleFractionMask = 0x7F_FFFF;
    private const uint SingleImplicitLeadingBit = 0x80_0000;
    /// <summary>Exponent bias (127) plus the 23 fraction bits: <c>rate == significand * 2^(SingleScaleBase - biasedExponent)</c>.</summary>
    private const int SingleScaleBase = 150;
    /// <summary>Significant bits of a single-precision value, implicit leading bit included.</summary>
    private const int SingleSignificandBits = 24;
    /// <summary>A 32-bit product at or above this reads as negative when taken as a signed int.</summary>
    private const uint SignedInt32SignBit = 0x8000_0000u;

    /// <summary>
    /// The rules a listing must pass: the client's own checks from its sell tab, in its order, re-applied because
    /// the client never sends these (every real 1403 has start &gt; 0, instant &gt; 0, start &lt;= instant and both
    /// at or below 10,000,000,000). 0 when it passes, else the 2458 code. Each client check maps to the code
    /// whose message text says it:
    /// <list type="bullet">
    /// <item>the item: not the seller's, not in the bag, or a count of 0 or above its stack (client 7966), or a
    ///   duration outside <see cref="AuctionPackets.AllowedSellHours"/>: 16304.</item>
    /// <item>an item <see cref="TradeGate.RefusedAsNotTradeable"/> (Is_Trade 0, or bound): 16304. Defensive;
    ///   applied only when <paramref name="template"/> is given.</item>
    /// <item>start price 0 (7970) or instant price 0 (8182 "Must enter more than 1 Rupee as the buyout price"):
    ///   16304. The server used to accept an instant price of 0; the client never lets one through.</item>
    /// <item>start above instant (7969): 16303, 8603.</item>
    /// <item>start or instant above the auction maximum (8750 / 8751), which the client reads from the
    ///   character field with 10,000,000,000 as its default, i.e. Config_Def type 91
    ///   (<paramref name="priceCap"/>, GuildFundsInHandPackets.WalletCap): 16304.</item>
    /// <item>the fee above the wallet (7968): 16302, 2114.</item>
    /// <item><see cref="AuctionPackets.MaxLiveArticles"/> live articles or more (8631): 16307, 8604.</item>
    /// </list>
    /// </summary>
    public static ushort CheckSellStart(AuctionPackets.SellStartRequest req, InventoryItem? item, uint sellerCharId,
        long priceCap, long fee, long wallet, int liveArticles, ItemData? template = null)
    {
        if (item == null || item.CharacterId != sellerCharId || item.StorageType != SellBagStorageType) return AuctionPackets.ResultSellItemMismatch;
        // Defensive check: a non-tradeable or bound item is refused.
        if (template != null && TradeGate.RefusedAsNotTradeable(template, item)) return AuctionPackets.ResultSellItemMismatch;
        if (req.Count == 0 || req.Count > item.Count || !AuctionPackets.AllowedSellHours.Contains(req.Hours)) return AuctionPackets.ResultSellItemMismatch;
        if (req.StartPrice == 0 || req.BuyoutPrice == 0) return AuctionPackets.ResultSellItemMismatch;
        if (req.StartPrice > req.BuyoutPrice) return AuctionPackets.ResultSellBuyoutBelowStart;
        ulong cap = (ulong)Math.Max(0, priceCap);
        if (req.StartPrice > cap || req.BuyoutPrice > cap) return AuctionPackets.ResultSellItemMismatch;
        if (fee > wallet) return AuctionPackets.ResultNotEnoughMoney;
        if (liveArticles >= AuctionPackets.MaxLiveArticles) return AuctionPackets.ResultSellQuantityLimit;
        return 0;
    }

    /// <summary>
    /// The registration fee rate for a listing of <paramref name="hours"/>. The client derives it from the days
    /// byte of the sell tab (1, 3 or 7 for the three duration choices; the 1403 carries hours): 0.03 for 1 day (and
    /// the default for any other value), 0.07 for 3 days, 0.15 for 7 days.
    /// </summary>
    public static float RegistrationFeeRate(int hours) => (hours / HoursPerDay) switch
    {
        3 => FeeRateThreeDays,
        7 => FeeRateSevenDays,
        _ => FeeRateOneDay,
    };

    /// <summary>
    /// Registration fee, charged when the article is created (2458 blob+220 is the wallet after it). It mirrors
    /// the client's own formula: basis = ItemInfo Price_Buy × the item's count, as a 32-bit multiply read back
    /// unsigned; times <see cref="RegistrationFeeRate"/>; the product rounded half up (<c>frac &lt; 0.5</c>
    /// truncates, else <c>+1.0</c> then truncates); at least 1. The client's floating-point unit runs at 24-bit
    /// precision (see <see cref="AuctionPackets.MinimumBid"/>), so the product, and the <c>+1.0</c>, are rounded
    /// to a single's significand first; that is done here on exact integers. The 32-bit wrap of the basis only
    /// matters above 4,294,967,295, which a few ItemInfo templates reach at a 255 stack. What the client shows
    /// and checks against the wallet is this value, so it is also what is charged.
    /// </summary>
    public static long RegistrationFee(ItemData? template, int count, int hours)
    {
        uint units = (uint)Math.Clamp(count, 0, ushort.MaxValue); // the item record's u16 count
        uint basis = unchecked((template?.Price_Buy ?? 0u) * units);

        uint rateBits = BitConverter.SingleToUInt32Bits(RegistrationFeeRate(hours));
        int rateExponent = (int)((rateBits >> SingleExponentShift) & SingleExponentMask);
        UInt128 rateSignificand = (rateBits & SingleFractionMask) | SingleImplicitLeadingBit;
        int scale = SingleScaleBase - rateExponent; // rate == rateSignificand · 2^-scale

        // Convert the 32-bit product to a float, adding 2^32 (a 24-bit add) when it read negative.
        UInt128 exactBasis = basis >= SignedInt32SignBit ? RoundToSingleSignificand(basis) : basis;
        UInt128 product = RoundToSingleSignificand(exactBasis * rateSignificand); // single-precision multiply
        UInt128 one = UInt128.One << scale;
        UInt128 whole = product >> scale;
        UInt128 frac = product & (one - UInt128.One);
        if (frac >= (one >> 1))
            whole = RoundToSingleSignificand(product + one) >> scale; // add 1.0, then truncate

        // The client keeps the low 32 bits as a signed int and returns 1 for anything <= 1.
        int fee = unchecked((int)(uint)whole);
        return fee <= 1 ? 1 : fee;
    }

    /// <summary><paramref name="n"/> rounded to 24 significant bits, half to even, as a single-precision result.</summary>
    private static UInt128 RoundToSingleSignificand(UInt128 n)
    {
        int bits = 128 - (int)UInt128.LeadingZeroCount(n);
        if (bits <= SingleSignificandBits) return n;
        int shift = bits - SingleSignificandBits;
        UInt128 q = n >> shift;
        UInt128 rem = n & ((UInt128.One << shift) - UInt128.One);
        UInt128 half = UInt128.One << (shift - 1);
        if (rem > half || (rem == half && (q & UInt128.One) == UInt128.One)) q++;
        return q << shift;
    }
}
