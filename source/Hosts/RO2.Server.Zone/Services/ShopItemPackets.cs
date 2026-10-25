using RO2.Network.Packets;
using RO2.Network.Protocol;

using RO2.Economy.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builders for the shop / item-operation S2C notifies. Every opcode here is a compact-BA
/// (COMPACT_INNER) message: the body is
/// <c>[compactLen marker][u16 selfLen][u16 msgTag][fields]</c> and the client's compact-length
/// decoder rejects any body whose first byte is not a marker in {1,2,4,8}, hands the per-opcode
/// stub a NULL ByteArray, and crashes on the deref. The previous handlers wrote raw fields with no
/// marker, so each was a live remote crash the moment its handler ran.
/// </summary>
/// <remarks>
/// <para>
/// Every msgTag below is the single msgTag its opcode carries (band 52001..58526). For all 14 opcodes the
/// envelope invariant <c>selfLen == len(body) - markerPrefix</c> holds, and the marker is always 1 or 2.
/// </para>
/// <para>
/// ⚠️ NOTE ON THE 2-BYTE MARKER. Opcodes 2375/2376/2377/2379/2899/2997/3009 use the 2-byte compact-length
/// marker (marker==2), whose prefix is 3 bytes, so their msgTag sits at <c>body+5</c>, not <c>body+4</c>.
/// Reading a u16 at a fixed body+4 straddles the selfLen/msgTag boundary and yields a bogus out-of-band
/// value. Realigned to the marker width, each has a clean in-band msgTag
/// (58049/58051/58053/58055/58491/58508/58520). They are ordinary compact-BA item-result records and are
/// WRAPPED, not suppressed.
/// </para>
/// <para>
/// FIELD FIDELITY IS DELIBERATELY CONSERVATIVE. Only the envelope, the msgTag, and — for the opcodes that
/// carry one — the leading actor handle (low byte 0x11 PC type tag) are modelled. The item-record payloads of
/// the larger messages are variable, per-opcode PODs whose field layout is not derived here, so the remaining
/// bytes are sent as zero. That is a benign "empty result" rather than a guess at record contents, and it is
/// SAFE: each body is built to a length the client accepts for that opcode (the fixed length, or the shortest
/// valid length for the variable ones), so every fixed-offset read the client makes stays inside the
/// allocated ByteArray. A working-but-sparse result panel is strictly better than a crash; where a field IS
/// modelled it is called out.
/// </para>
/// </remarks>
public static class ShopItemPackets
{
    // ---- Shared wire values ----

    /// <summary>A slot that is always zero on the wire: reserved, unread by the client, or a documented zero.</summary>
    private const int ReservedZero = 0;

    /// <summary>Result code 0: success. A non-zero result is a refusal the client shows and then stops reading.</summary>
    private const int ResultSuccess = 0;

    /// <summary>
    /// Largest body length whose compact-length marker is the 1-byte form (selfLen &lt; 0x80, i.e. a prefix of
    /// marker + 1 length byte). Longer bodies use the 2-byte form.
    /// </summary>
    private const int MaxBodyLenOneByteLength = 129;

    /// <summary>Envelope bytes in front of the msgTag with the 1-byte length: [marker][len][u16 selfLen].</summary>
    private const int EnvelopeLenOneByteLength = 4;

    /// <summary>Envelope bytes in front of the msgTag with the 2-byte length: [marker][u16 len][u16 selfLen].</summary>
    private const int EnvelopeLenTwoByteLength = 5;

    // ---- Total body length (envelope included) the client accepts for each opcode ----
    private const int BodyLenChangeHideFlag = 16;           // 2380
    private const int BodyLenAckMakeCraftItem = 12;         // 2539
    private const int BodyLenNotifyMakeCraftItem = 19;      // 2540
    private const int BodyLenAckMakeCraftItemInfo = 141;    // 2541: blob 138 = 12 + 126
    private const int BodyLenBeginRefineShop = 18;          // 2999
    private const int BodyLenNfyBeginRefineShop = 22;       // 3003
    private const int BodyLenRandomRefineCommission = 18;   // 3006
    private const int BodyLenItemEnforceResult = 23;        // 2360
    private const int BodyLenUpdateInvenSize = 11;          // 2378
    private const int BodyLenDestroyItem = 141;             // 2379: blob 138 = 12 + 126
    private const int BodyLenCardEnforceItem = 141;         // 2899: blob 138
    private const int BodyLenRefineItem = 145;              // 2997
    private const int BodyLenRefineProbability = 24;        // 2996
    private const int BodyLenRandomRefineItem = 139;        // 3009: blob 136 = 10 + 126
    private const int BodyLenBreakDownItemShortest = 25;    // 2417: the shortest of its variable lengths

    /// <summary>
    /// Builds a compact-BA body: <c>[compactLen][u16 selfLen][u16 msgTag][fields][zero pad]</c>,
    /// padded so the final body length equals <paramref name="totalBodyLen"/> (a length the client accepts
    /// for this opcode). Guarantees a valid marker first byte via
    /// <see cref="PacketWriter.WriteCompactBABody"/>.
    /// </summary>
    private static byte[] Build(ushort msgTag, int totalBodyLen, System.Action<PacketWriter>? writeFields = null)
    {
        byte[] fields;
        if (writeFields != null)
        {
            using var fp = new PacketWriter(64);
            writeFields(fp);
            fields = fp.ToArray();
        }
        else
        {
            fields = System.Array.Empty<byte>();
        }

        // marker=1 (2-byte prefix) while selfLen < 0x80, i.e. totalBodyLen <= MaxBodyLenOneByteLength; else
        // marker=2 (3-byte prefix). payload = [u16 msgTag][fields][pad]; body = prefix + 2(selfLen) + payload.
        int payloadLen = totalBodyLen <= MaxBodyLenOneByteLength
            ? totalBodyLen - EnvelopeLenOneByteLength
            : totalBodyLen - EnvelopeLenTwoByteLength;
        int pad = payloadLen - 2 - fields.Length;
        if (pad < 0)
            throw new System.ArgumentException(
                $"fields ({fields.Length}B) exceed payload for msgTag {msgTag} at len {totalBodyLen}");

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(msgTag);
            p.WriteBytes(fields);
            for (int i = 0; i < pad; i++) p.WriteByte(ReservedZero);
        });
        return pw.ToArray();
    }

    // ---- msgTags, one per opcode ----
    internal const ushort MsgTagBuyItem = 58049;              // 2375, 0xE2C1
    internal const ushort MsgTagSellItem = 58051;             // 2376, 0xE2C3
    internal const ushort MsgTagCancelSellItem = 58053;       // 2377, 0xE2C5
    internal const ushort MsgTagUpdateInvenSize = 58054;      // 2378, inferred (between 2377=58053 and 2379=58055; the client does not validate the S2C msgTag)
    internal const ushort MsgTagDestroyItem = 58055;          // 2379, 0xE2C7
    internal const ushort MsgTagItemEnforceResult = 58164;    // 2360, 0xE334
    internal const ushort MsgTagChangeHideFlag = 58401;       // 2380, 0xE421
    internal const ushort MsgTagBreakDownItem = 58106;        // 2417, 0xE2FA
    internal const ushort MsgTagAckMakeCraftItem = 58390;     // 2539, 0xE416
    internal const ushort MsgTagNotifyMakeCraftItem = 58392;  // 2540, 0xE418
    internal const ushort MsgTagAckMakeCraftItemInfo = 58393; // 2541, 0xE419
    internal const ushort MsgTagCardEnforceItem = 58491;      // 2899, 0xE47B
    internal const ushort MsgTagRefineItem = 58508;           // 2997, 0xE48C
    internal const ushort MsgTagRefineProbability = 58507;    // 2996, best-effort (the client does not validate the response msgTag)
    internal const ushort MsgTagBeginRefineShop = 58510;      // 2999, 0xE48E
    internal const ushort MsgTagNfyBeginRefineShop = 58516;   // 3003, 0xE494
    internal const ushort MsgTagRandomRefineCommission = 58525; // 3006, 0xE49D
    internal const ushort MsgTagRandomRefineItem = 58520;     // 3009, 0xE498

    /// <summary>
    /// RMI 2380 OnChangeEquipItemHideFlag. Body 16 bytes, msgTag 58401. Layout:
    /// <c>[msgTag][u32 0][u32 actorHandle][u16 hideFlag]</c>. The actor handle sits at blob+8 (low byte 0x11 PC tag);
    /// blob+4 is a constant zero slot; blob+12 is the u16 costume-hide flag.
    /// </summary>
    public static byte[] BuildChangeHideFlag2380(uint actorHandle, ushort hideFlag) =>
        Build(MsgTagChangeHideFlag, BodyLenChangeHideFlag, p =>
        {
            p.WriteUInt32(ReservedZero);
            p.WriteUInt32(actorHandle);
            p.WriteUInt16(hideFlag);
        });

    /// <summary>
    /// RMI 2539 OnAckMakeCraftItem, the answer to 1349 ReqCraftItem. Body 12 bytes, msgTag 58390. Layout:
    /// <c>[msgTag][u32 crafterHandle@4][i16 result@8]</c>, the handle the crafter's own (low byte 0x11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Result 0 is the craft START, sent to the crafter only: a started 1349 is answered 2539 → 2540 phase 1 →
    /// 2398 in the same tick, and a 1350 cancel is not. The client puts the player into the crafting state on 0.
    /// </para>
    /// <para>
    /// A non-zero result is a refusal: the client shows the result code message and returns, changing no
    /// state, and nothing else is sent with it. Codes in use: 22627 (string 7695 "Crafting conditions are not
    /// satisfied."), the answer to a second 1349 while the first one's batch runs; 22628 (7696 "Have not
    /// learned yet.") and 22630 (7698 "You don't have enough material."); see CraftItemHandler.
    /// </para>
    /// </remarks>
    public static byte[] BuildAckMakeCraftItem2539(uint actorHandle, short result = ResultSuccess) =>
        Build(MsgTagAckMakeCraftItem, BodyLenAckMakeCraftItem, p =>
        {
            p.WriteUInt32(actorHandle);  // blob+4
            p.WriteInt16(result);        // blob+8, 0 = started
        });

    /// <summary>2540 craft phase at blob+10: the batch started (the 1349 reply).</summary>
    internal const byte CraftPhaseBegin = 1;
    /// <summary>2540 craft phase at blob+10: the batch finished, after its last item.</summary>
    internal const byte CraftPhaseComplete = 2;
    /// <summary>2540 craft phase at blob+10: the batch was stopped (the 1350 reply); it carries recipe 0.</summary>
    internal const byte CraftPhaseTerminate = 3;

    /// <summary>
    /// RMI 2540 OnNotifyMakeCraftItem: a crafter's batch began, completed or was stopped. Blob 17 =
    /// <c>[msgTag 58392][u32 0@4][i16 result@8][u8 phase@10][u32 crafterHandle@11][u16 recipeId@15]</c>.
    /// </summary>
    /// <remarks>
    /// The client reads only the signed result at +8, the phase at +10 and the unaligned handle at +11. Phase and
    /// recipe: (1, recipe), (2, recipe), (3, 0), no other combination. The recipe is the Craft_ItemList ID the
    /// 1349 named. It is sent to the crafter AND to the players around it: they receive the crafter's phase-1 and
    /// phase-2 2540s together with its 2398, but none of its 2539/2379/2541/2536.
    /// </remarks>
    public static byte[] BuildNotifyMakeCraftItem2540(uint crafterHandle, byte phase, ushort recipeId) =>
        Build(MsgTagNotifyMakeCraftItem, BodyLenNotifyMakeCraftItem, p =>
        {
            p.WriteUInt32(ReservedZero);  // blob+4  always 0, unread
            p.WriteInt16(ResultSuccess);  // blob+8  result (sign-extended); 0
            p.WriteByte(phase);           // blob+10 1 begin / 2 complete / 3 terminate
            p.WriteUInt32(crafterHandle); // blob+11 the crafter
            p.WriteUInt16(recipeId);      // blob+15 Craft_ItemList.ID, 0 with phase 3
        });

    /// <summary>
    /// RMI 2541 OnAckMakeCraftItemInfo: one crafted item landed in the bag. Blob 138 =
    /// <c>[msgTag 58393][u32 crafterHandle@4][i16 result@8][u16 currentSp@10][ItemRecord126@12]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client adds blob+12 to the bag as an item record, so the record is the product's stack AS IT NOW
    /// STANDS: the same uid comes back once per crafted item with its count going 1, 2, 3 ... over a batch.
    /// </para>
    /// <para>
    /// blob+10 goes into the player's current-SP slot (the CharVitals slot the 2716 SP bar is written to). It
    /// stays constant per character across its craft ticks (Craft_NeedSp is 0 on every recipe) and equals the SP
    /// carried by the character's 2498 at blob+12. So the crafter's current SP goes there; a 0 would empty the SP
    /// bar on every crafted item.
    /// </para>
    /// </remarks>
    public static byte[] BuildAckMakeCraftItemInfo2541(uint crafterHandle, ushort currentSp, byte[] itemRecord126)
    {
        RequireRecord(itemRecord126, ZoneS2COpcodes.OnAckMakeCraftItemInfo);
        return Build(MsgTagAckMakeCraftItemInfo, BodyLenAckMakeCraftItemInfo, p =>
        {
            p.WriteUInt32(crafterHandle); // blob+4
            p.WriteInt16(ResultSuccess);  // blob+8  result; always 0
            p.WriteUInt16(currentSp);     // blob+10 -> CharVitals current SP
            p.WriteBytes(itemRecord126);  // blob+12..137
        });
    }

    /// <summary>Byte length of the 2536 payload after its u32 length: u16 count + u16 recipe + u32 coolTime + u32 zero.</summary>
    private const uint CoolTimeWaitInnerLen = 12;

    /// <summary>Recipes carried by one 2536 as built here.</summary>
    private const ushort CoolTimeWaitRecipeCount = 1;

    /// <summary>
    /// RMI 2536 OnNotifyCoolTimeWaitCraftIndex for one recipe that has just finished a batch. Not a
    /// compact body: a u32 length, then <c>[u16 count = 1][u16 recipeId][u32 coolTime][u32 0]</c>, 16 bytes.
    /// </summary>
    /// <remarks>
    /// The answer names the recipe just crafted and comes right after the batch's last 2541 and before its
    /// phase-2 2540. The u32 after the recipe is Craft_ItemList.CoolTime_Min (1 on every row), so it is written
    /// from that column; a recipe does not come back within a minute, which is what a one-minute recipe cooldown
    /// looks like. The trailing u32 is 0.
    /// </remarks>
    public static byte[] BuildCoolTimeWaitCraftIndex2536(ushort recipeId, uint coolTimeMin)
    {
        using var pw = new PacketWriter(16);
        pw.WriteUInt32(CoolTimeWaitInnerLen);   // byte length of what follows
        pw.WriteUInt16(CoolTimeWaitRecipeCount);// one recipe
        pw.WriteUInt16(recipeId);     // Craft_ItemList.ID
        pw.WriteUInt32(coolTimeMin);  // Craft_ItemList.CoolTime_Min
        pw.WriteUInt32(ReservedZero);
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2999 AnsBeginRefineShop answering a 1670 whose blob+8 carried <paramref name="requestValue"/>.
    /// Layout <c>[msgTag][u32 actorHandle][u32 requestValue][u32 0]</c>, body 18 bytes, msgTag 58510. The handle at
    /// blob+4 is the PC's (low byte 0x11); blob+8 echoes the request's blob+8 (usually 0, sometimes 1, so it is not
    /// padding); blob+12 = 0. The client forwards the blob to the refine-shop window.
    /// </summary>
    public static byte[] BuildBeginRefineShop2999(uint actorHandle, uint requestValue) =>
        Build(MsgTagBeginRefineShop, BodyLenBeginRefineShop, p =>
        {
            p.WriteUInt32(actorHandle);   // blob+4
            p.WriteUInt32(requestValue);  // blob+8  the 1670's blob+8, echoed
            p.WriteUInt32(ReservedZero);  // blob+12
        });

    /// <summary>
    /// RMI 3003 NfyBeginRefineShop. Body 22 bytes, msgTag 58516. Layout:
    /// <c>[msgTag][u32 ownerHandle][u32 ownerHandle][u32 flag][u32 0]</c>.
    /// The SAME owner handle appears twice at blob+4 and blob+8 (low byte 0x11);
    /// blob+12 is a small flag (0/1), sent as 0; blob+16 constant zero.
    /// </summary>
    public static byte[] BuildNfyBeginRefineShop3003(uint ownerHandle) =>
        Build(MsgTagNfyBeginRefineShop, BodyLenNfyBeginRefineShop, p =>
        {
            p.WriteUInt32(ownerHandle);   // blob+4
            p.WriteUInt32(ownerHandle);   // blob+8, the same owner handle again
            p.WriteUInt32(ReservedZero);  // blob+12 flag, sent as 0
            p.WriteUInt32(ReservedZero);  // blob+16
        });

    /// <summary>
    /// RMI 3006 NfyRandomRefineCommissionToBuyer, sent to a BUYER who opened a player's
    /// shop, never to the shop owner as a 1674 reply. Layout <c>[msgTag][u32 buyerHandle][u32 commission][u32 0]</c>,
    /// body 18 bytes, msgTag 58525; blob+4 low byte 0x11; blob+8 is a value (0, 1, 1000 ...) rather than a 0/1 flag,
    /// so it is passed through whole; blob+12 = 0.
    /// </summary>
    /// <remarks>
    /// It follows the receiver's own 1481 ReqOpenPrivateShopBuyerUI (answered 2657, 2658, 3000, 3006 in the same
    /// tick), blob+4 is the RECEIVER's own handle, and a 1674 from the owner is answered with nothing. blob+8 is
    /// the shop owner's random-refine commission as its 1674 set it (the owner sent 1 and the buyer got 1). The
    /// client forwards the blob to a UI window.
    /// </remarks>
    /// <param name="buyerHandle">The handle of the player who opened the shop (the receiver).</param>
    /// <param name="ownerCommission">The owner's 1674 blob+8 value (see RandomRefineCommissionHandler).</param>
    public static byte[] BuildRandomRefineCommission3006(uint buyerHandle, uint ownerCommission) =>
        Build(MsgTagRandomRefineCommission, BodyLenRandomRefineCommission, p =>
        {
            p.WriteUInt32(buyerHandle);      // blob+4
            p.WriteUInt32(ownerCommission);  // blob+8
            p.WriteUInt32(ReservedZero);     // blob+12
        });

    /// <summary>
    /// RMI 2360 OnPCItemEnforceResult. Body 23 bytes, msgTag 58164. blob+4 is a u32 result slot,
    /// sent as 0 (success). The remaining 13 bytes carry an item/container reference (blob+8
    /// low byte 0x15) plus an 8-byte id and a trailing status byte; their exact field breakdown is
    /// not derived, so they are sent as zero — a benign result over the exact length so the
    /// consumer's reads stay in bounds.
    /// </summary>
    public static byte[] BuildItemEnforceResult2360() =>
        Build(MsgTagItemEnforceResult, BodyLenItemEnforceResult, p =>
        {
            p.WriteUInt32(ResultSuccess); // blob+4 result = success
        });

    /// <summary>
    /// RMI 2378 OnUpdateInvenSize — tell the client its total inventory slot count after a bag is equipped
    /// or removed. The client reads a single u8 at blob+8 and resizes the inventory grid, then shows the "inventory
    /// expanded" notice. Layout: <c>[msgTag][u32 actorHandle][u8 count]</c> (count at blob+8). The count is
    /// base + Σ Bag_Size, passed straight through.
    /// </summary>
    public static byte[] BuildUpdateInvenSize2378(uint actorHandle, int count) =>
        Build(MsgTagUpdateInvenSize, BodyLenUpdateInvenSize, p =>
        {
            p.WriteUInt32(actorHandle);   // blob+4
            p.WriteByte((byte)System.Math.Clamp(count, 0, byte.MaxValue)); // blob+8 slot count (u8)
        });

    /// <summary>
    /// RMI 2379 OnDestroyItem carrying a real item record: <c>[msgTag][u32 handle@4][u32 0@8][ItemRecord126@12]</c>,
    /// blob 138 / body 141. The record is the stack AS IT NOW STANDS: count 0 empties the cell, a non-zero
    /// count shrinks the stack in place — a break-down uses exactly that (the stone's record,
    /// 10 -> 9 for a x1 ItemBreakInfo row, 50 -> 40 for a x10 row). blob+8..11 is zero except blob+10 = 2 after
    /// token buys.
    /// </summary>
    public static byte[] BuildDestroyItem2379(uint actorHandle, byte[] itemRecord126) =>
        BuildDestroyItem2379(actorHandle, itemRecord126, DestroyReasonBreakDown);

    /// <summary>2379 blob+10 after a break-down or craft: 0.</summary>
    internal const ushort DestroyReasonBreakDown = 0;

    /// <summary>2379 blob+10 on a token taken by an NPC-shop token buy (Buy_Type 1): 2 on every token 2379 of such
    /// a buy.</summary>
    internal const ushort DestroyReasonTokenBuy = 2;

    /// <summary>
    /// RMI 2379 OnDestroyItem with an explicit reason: <c>[msgTag][u32 handle@4][i16 0@8][u16 reason@10]
    /// [ItemRecord126@12]</c>. Reason 0 is the break-down / craft form above; <see cref="DestroyReasonTokenBuy"/>
    /// is a token stack shrunk by a Buy_Type 1 shop buy (BuyItemHandler).
    /// </summary>
    public static byte[] BuildDestroyItem2379(uint actorHandle, byte[] itemRecord126, ushort reason)
    {
        RequireRecord(itemRecord126, ZoneS2COpcodes.OnDestroyItem);
        return Build(MsgTagDestroyItem, BodyLenDestroyItem, p =>
        {
            p.WriteUInt32(actorHandle);   // blob+4
            p.WriteUInt16(ReservedZero);  // blob+8, always zero
            p.WriteUInt16(reason);        // blob+10, 0 after a break-down, 2 after a token buy
            p.WriteBytes(itemRecord126);  // blob+12..137
        });
    }

    /// <summary>
    /// RMI 2899 OnCardEnforceItem. Envelope, msgTag and handle only: body 141 bytes, msgTag 58491, blob+4 an actor
    /// handle (low byte 0x11). The card record is not field-mapped and is sent as zero over the exact length.
    /// ⚠️ Envelope-only form, kept for the envelope sweep test: its uid-0 record makes the consumer bail,
    /// so no handler may send it. CardEnforceItemHandler sends the full overload below.
    /// </summary>
    public static byte[] BuildCardEnforceItem2899(uint actorHandle) =>
        Build(MsgTagCardEnforceItem, BodyLenCardEnforceItem, p => p.WriteUInt32(actorHandle));

    /// <summary>2899 blob+137 for result 0 (the enforce was rolled).</summary>
    private const byte CardEnforceFlagSucceeded = 1;

    /// <summary>2899 blob+137 for any other result (the enforce was refused).</summary>
    private const byte CardEnforceFlagRefused = 0;

    /// <summary>
    /// RMI 2899 OnCardEnforceItem with the fields its consumer actually reads — the form for a
    /// caller that has really rolled the enforce (result 0, the card's record after the roll) or refuses it
    /// (result 5 with the request's descriptor echoed as the record). Sent by CardEnforceItemHandler (1611).
    /// </summary>
    /// <remarks>
    /// Blob 138: <c>[u32 handle@4][i16 result@8][u8 oldLevel@10][ItemRecord126@11][u8 flag@137]</c>.
    /// The consumer bails unless the record's uid (blob+11, a single u64 fed to the find-by-uid lookup)
    /// is non-zero, compares <c>oldLevel</c> with the record's level byte (record+0x1D = blob+40) to pick
    /// the success or failure text, and writes that byte to the item's level field. The record uid is
    /// the 1611 request's FIRST descriptor uid, the level goes up, down or stays, and blob+137 is 1 for
    /// result 0 and 0 for result 5 — so the flag is derived from the result here rather than taken as a parameter.
    /// </remarks>
    /// <param name="itemRecord126">The enhanced item's record AFTER the roll (126 bytes).</param>
    public static byte[] BuildCardEnforceItem2899(uint actorHandle, short resultCode, byte oldLevel,
        byte[] itemRecord126)
    {
        RequireRecord(itemRecord126, ZoneS2COpcodes.OnCardEnforceItem);
        return Build(MsgTagCardEnforceItem, BodyLenCardEnforceItem, p =>
        {
            p.WriteUInt32(actorHandle);                   // blob+4
            p.WriteInt16(resultCode);                     // blob+8   signed (movsx)
            p.WriteByte(oldLevel);                        // blob+10
            p.WriteBytes(itemRecord126);                  // blob+11..136
            p.WriteByte(resultCode == ResultSuccess ? CardEnforceFlagSucceeded : CardEnforceFlagRefused); // blob+137
        });
    }

    /// <summary>2997 blob+10 outcome: the refine succeeded. 1 = failed (keep or downgrade), 2 = broke (destroyed).</summary>
    internal const uint RefineOutcomeSuccess = 0;

    /// <summary>
    /// RMI 2997 OnRefineItem. Body 145 bytes, msgTag 58508. blob+4 is an actor handle (low byte 0x11).
    /// </summary>
    /// <remarks>
    /// Layout (the refine window's response for notify 66040):
    ///   blob+8  u16 resultCode  (0 = process the outcome; 1-10 = localized error + ResetWindow)
    ///   blob+10 u32 outcome     (0 success / 1 fail-keep or downgrade / 2 break-destroy)
    ///   blob+14 u8  aux         (the OLD/pre-refine level — the "from"/@2 in the "(L -> L+1)" chat; the
    ///                            client reads blob+14 for it, so send the current level or the arrow shows "0 ->")
    ///   blob+15 .. +140  the full updated 126-byte item record (UID@+0 == refined item's KharaID, template
    ///                    @+0x08, bag@+0x0F, slot@+0x10, attr@+0x1B, new refine level@+0x1D). The window's
    ///                    GATE requires the KharaID at blob+15/+19 to be NON-ZERO, else it early-outs and
    ///                    only re-enables the buttons (result not shown, item not updated until relog).
    /// A fully populated record makes the window show the result AND set item+562 (refine level) in place on
    /// both the bag item and the paperdoll — no relog. The stat bonus still rides the 2498/2848 re-push.
    /// </remarks>
    public static byte[] BuildRefineItem2997(uint actorHandle, ushort resultCode = ResultSuccess,
        uint outcome = RefineOutcomeSuccess, byte[]? itemRecord126 = null, byte aux = 0) =>
        Build(MsgTagRefineItem, BodyLenRefineItem, p =>
        {
            p.WriteUInt32(actorHandle);   // blob+4  (inner-header dword; not read by the refine handler)
            p.WriteUInt16(resultCode);    // blob+8  result
            p.WriteUInt32(outcome);       // blob+10 outcome
            p.WriteByte(aux);             // blob+14 aux
            if (itemRecord126 is not null) p.WriteBytes(itemRecord126);   // blob+15..+140 embedded item record
        });

    /// <summary>
    /// RMI 2996 OnRefineProability — the answer to 1669 ReqRefineProbability. This client's consumer reads ONLY
    /// blob+8 (u16 result code); on 0 it forwards to the refine window as notify 66039, which is a NO-OP stub.
    /// The window's success/degrade/break labels are reset blank and NEVER populated from a packet, so this
    /// build cannot show refine rates without a client patch. This builder therefore only sends a well-formed,
    /// enveloped body with result 0 so the client cleanly no-ops (the old raw WriteUInt32(100) had no envelope
    /// and risked the malformed-body error path). The refine roll still uses the real REFINE_RATE tables
    /// server-side (RefineItemHandler); only the % preview is a dead client stub.
    /// </summary>
    public static byte[] BuildRefineProbability2996(uint actorHandle) =>
        Build(MsgTagRefineProbability, BodyLenRefineProbability,
            p => { p.WriteUInt32(actorHandle); p.WriteUInt16(ResultSuccess); });

    /// <summary>
    /// RMI 3009 OnRandomRefineItem. Body 139 bytes, msgTag 58520. blob+4 is an actor handle (low byte 0x11). The
    /// refine result record is not field-mapped and is sent as zero over the exact length.
    /// ⚠️ Envelope-only form, kept for the envelope sweep test: a uid-0 record names no item, so no handler
    /// may send it. RandomRefineItemHandler sends the record overload below.
    /// </summary>
    public static byte[] BuildRandomRefineItem3009(uint actorHandle) =>
        Build(MsgTagRandomRefineItem, BodyLenRandomRefineItem, p => p.WriteUInt32(actorHandle));

    /// <summary>
    /// RMI 3009 OnRandomRefineItem carrying the item's record — after a real re-roll (result 0, the record
    /// after the roll), or with a refusal code and the item's current record. Sent by
    /// RandomRefineItemHandler (1676), which today can only refuse: result 3 when the request names no usable
    /// grinding aid, result 6 otherwise, because no grinding options are stored.
    /// </summary>
    /// <remarks>
    /// Blob 136 = <c>[u32 handle@4][u16 result@8][ItemRecord126@10]</c>. The client reads only the result and
    /// forwards the whole blob to the refine window, so the record is what updates the item. The record carries a
    /// real item uid, equal to the 1676 request's first descriptor uid. What a re-roll changes is confined to
    /// record+73..90. A non-zero result is shown by the client: 3 is chatted as string 11569 "Ask to manager
    /// (@1)" with the code, 5 and 6 are string 11648 "It cannot be honed". Result 3 (a 1676 whose material
    /// descriptor is all zero) carries a descriptor-only record: the target's uid, count, bag and slot,
    /// template 0.
    /// </remarks>
    /// <param name="itemRecord126">The item's record AFTER the re-roll (126 bytes).</param>
    public static byte[] BuildRandomRefineItem3009(uint actorHandle, ushort resultCode, byte[] itemRecord126)
    {
        RequireRecord(itemRecord126, ZoneS2COpcodes.OnRandomRefineItem);
        return Build(MsgTagRandomRefineItem, BodyLenRandomRefineItem, p =>
        {
            p.WriteUInt32(actorHandle);    // blob+4
            p.WriteUInt16(resultCode);     // blob+8
            p.WriteBytes(itemRecord126);   // blob+10..135
        });
    }

    private static void RequireRecord(byte[] record, ushort opcode)
    {
        if (record is null || record.Length != ItemPackets.ItemRecordLength)
            throw new System.ArgumentException(
                $"{opcode} carries {ItemPackets.ItemRecordLength}-byte item records; got {record?.Length ?? 0}. " +
                "A short record makes the client read past the end of the blob.", nameof(record));
    }

    /// <summary>
    /// RMI 2375 OnBuyItem — the shop-buy ACK that ALSO delivers the purchased item and the new wallet
    /// balance. An empty 41-byte header (item count 0) is rejected by the client: it adds nothing to the bag and
    /// never closes the shop's quantity dialog, so the player cannot buy again. Layout (blob 167 = 41-byte
    /// header + one 126-byte item record):
    ///   a1+2  u16 msgTag 0xE2C1
    ///   a1+4  u32 buyer actor handle
    ///   a1+8  u16 result (0 = success; nonzero shows an error and aborts)
    ///   a1+10 u16 message code (0)
    ///   a1+12 u64 NEW wallet balance (client sets the wallet to this; shows spent = old - new)
    ///   a1+28 u8  mode (0 = normal zeny purchase -> deduct/display path)
    ///   a1+29 u8  item count (>=1; 0 is the failed/empty buy)
    ///   a1+32 u8  0 (not the ability/karma special case)
    ///   a1+33 u32 item id
    ///   a1+41 item record, 126 bytes (uid@0, itemId@8) — the client adds it to the bag
    /// </summary>
    public static byte[] BuildBuyItem2375(uint buyerHandle, long newBalance, uint itemId, byte[] itemRecord126) =>
        BuildBuyItem2375(buyerHandle, newBalance, itemId, new[] { itemRecord126 });

    /// <summary>
    /// RMI 2375 OnBuyItem for a zeny purchase that touched SEVERAL stacks. <c>a1+29</c> is the record
    /// count and the loop bound of the client's record walk, so the blob is <c>41 + 126 * count</c>.
    /// </summary>
    /// <remarks>
    /// The multi-record form is used when one purchase exceeds the item's Stack_Max: for example 255 of an item
    /// whose Stack_Max is 99 arrives as three records holding 99 + 99 + 57 under three distinct uids. Each record
    /// is the stack AS IT NOW STANDS (count = its new total).
    /// </remarks>
    public static byte[] BuildBuyItem2375(uint buyerHandle, long newBalance, uint itemId,
        IReadOnlyList<byte[]> itemRecords126) =>
        BuildBuyItem2375(buyerHandle, newBalance, itemId, itemRecords126, BuyModeZeny, NoTokenRow);

    /// <summary>2375 blob+30 of a zeny buy: no BuyTokenInfo row.</summary>
    internal const ushort NoTokenRow = 0;

    /// <summary>Zero bytes at blob+20..27 of 2375.</summary>
    private const int BuyItemUnusedLen = 8;

    /// <summary>2375 blob+28 of a zeny buy (Merchant Buy_Type 0).</summary>
    internal const byte BuyModeZeny = 0;

    /// <summary>2375 blob+28 of a token-item buy (Merchant Buy_Type 1): 1, with the BuyTokenInfo row at blob+30
    /// and the wallet unchanged at blob+12.</summary>
    internal const byte BuyModeToken = 1;

    /// <summary>
    /// RMI 2375 OnBuyItem with the buy's mode and BuyTokenInfo row: blob+28 = <paramref name="mode"/> (the
    /// Merchant row's Buy_Type, <see cref="BuyModeZeny"/> or <see cref="BuyModeToken"/>), blob+30 =
    /// <paramref name="tokenRow"/> (0 for zeny). Every other field is as in the zeny form; for a token buy the
    /// wallet at blob+12 is the unchanged balance, and blob+10, blob+20..27, blob+32 and blob+37 are 0.
    /// </summary>
    public static byte[] BuildBuyItem2375(uint buyerHandle, long newBalance, uint itemId,
        IReadOnlyList<byte[]> itemRecords126, byte mode, ushort tokenRow)
    {
        if (itemRecords126.Count is < 1 or > byte.MaxValue)
            throw new System.ArgumentException(
                $"2375 carries 1..255 item records (a count of 0 is the failed-buy form); got {itemRecords126.Count}.",
                nameof(itemRecords126));
        foreach (var record in itemRecords126) RequireRecord(record, ZoneS2COpcodes.OnBuyItem);

        using var pw = new RO2.Network.Packets.PacketWriter(256);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagBuyItem);                  // a1+2  msgTag
            p.WriteUInt32(buyerHandle);                    // a1+4  buyer handle
            p.WriteUInt16(ResultSuccess);                  // a1+8  result = success
            p.WriteUInt16(ReservedZero);                   // a1+10 message code
            p.WriteUInt64((ulong)System.Math.Max(0, newBalance)); // a1+12 new wallet balance
            p.WriteZeros(BuyItemUnusedLen);                // a1+20..27
            p.WriteByte(mode);                             // a1+28 mode: 0 zeny, 1 token items
            p.WriteByte((byte)itemRecords126.Count);       // a1+29 record count (loop bound)
            p.WriteUInt16(tokenRow);                       // a1+30 BuyTokenInfo row (0 for zeny)
            p.WriteByte(ReservedZero);                     // a1+32 not ability/karma
            p.WriteUInt32(itemId);                         // a1+33 item id
            p.WriteUInt32(ReservedZero);                   // a1+37
            foreach (var record in itemRecords126)
                p.WriteBytes(record);                      // a1+41 + 126*i  the item records
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2375 OnBuyItem answering a buy that delivered nothing: the same 41-byte header as a success with
    /// the code at blob+8, zero wallet fields, mode 0, NO record, and the request's token row and item id
    /// echoed. Sent by BuyItemHandler (1334) for every refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost every failed buy has exactly this shape (Mastery failures also report a balance). The client hands
    /// a non-zero blob+8 to ShowResultCodeMessage and returns before it reads anything else, so nothing past the
    /// code changes the bag or the wallet.
    /// </para>
    /// <para>
    /// The codes in use: 1, the generic failure (token-item rows, Dia rows, zeny rows and row/item mismatches);
    /// 20084 for Mastery-point rows; 20020, the bag-full code of 2438 and 2801; and 20166, a possession limit
    /// (see BuyItemHandler).
    /// </para>
    /// </remarks>
    public static byte[] BuildBuyRefusal2375(uint buyerHandle, ushort resultCode, ushort tokenRow, uint itemId)
    {
        using var pw = new PacketWriter(64);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagBuyItem);   // blob+2
            p.WriteUInt32(buyerHandle);     // blob+4  echoed
            p.WriteUInt16(resultCode);      // blob+8
            p.WriteUInt16(ReservedZero);    // blob+10
            p.WriteUInt64(ReservedZero);    // blob+12 wallet: not touched on a failure
            p.WriteUInt64(ReservedZero);    // blob+20
            p.WriteByte(BuyModeZeny);       // blob+28 mode
            p.WriteByte(ReservedZero);      // blob+29 record count 0: nothing delivered
            p.WriteUInt16(tokenRow);        // blob+30 request +25 echoed
            p.WriteByte(ReservedZero);      // blob+32
            p.WriteUInt32(itemId);          // blob+33 request +20 echoed
            p.WriteUInt32(ReservedZero);    // blob+37
        });
        return pw.ToArray();
    }

    /// <summary>2376 result of a sell that sold nothing (see <see cref="BuildSellItem2376(uint, ushort, long, IReadOnlyList{ulong}, IReadOnlyList{byte[]})"/>).</summary>
    internal const ushort SellResultNothingSold = ShopCodes.SellResultNothingSold;

    /// <summary>
    /// RMI 2376 OnSellItem: <c>[msgTag 58051][u32 sellerHandle@4][u16 result@8][u64 wallet@10]
    /// [10 x u64 buy-back uids@18][u8 k@98][k x ItemRecord126@99]</c>, blob <c>99 + 126k</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Success (result 0): the wallet is the NEW absolute zeny total, the list is the buy-back list after
    /// this sale, and each record is a sold stack as it now stands (count 0 clears the cell). The handle echoes
    /// the request, k equals the request's count and every record uid is in the list.
    /// </para>
    /// <para>
    /// Nothing sold (result 1): the CURRENT wallet, the list unchanged and k = 0 — the wallet and the list equal
    /// the previous 2376's. This answers a repeated sell of a uid already sold (a double click). The one other
    /// failure code, 20083, has the same shape.
    /// </para>
    /// </remarks>
    public static byte[] BuildSellItem2376(uint sellerHandle, ushort resultCode, long wallet,
        IReadOnlyList<ulong> buyBackUids, IReadOnlyList<byte[]> soldRecords126)
    {
        if (soldRecords126.Count > byte.MaxValue)
            throw new System.ArgumentException($"2376 carries at most 255 records; got {soldRecords126.Count}.",
                nameof(soldRecords126));
        foreach (var record in soldRecords126) RequireRecord(record, ZoneS2COpcodes.OnSellItem);

        using var pw = new PacketWriter(128 + ItemPackets.ItemRecordLength * soldRecords126.Count);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSellItem);                           // blob+2 msgTag 58051
            p.WriteUInt32(sellerHandle);                             // blob+4
            p.WriteUInt16(resultCode);                               // blob+8
            p.WriteUInt64((ulong)System.Math.Max(0, wallet));        // blob+10 wallet total
            for (int k = 0; k < BuyBackLedger.Slots; k++)            // blob+18 buy-back uid list, zero-padded
                p.WriteUInt64(k < buyBackUids.Count ? buyBackUids[k] : (ulong)ReservedZero);
            p.WriteByte((byte)soldRecords126.Count);                 // blob+98 count
            foreach (var record in soldRecords126) p.WriteBytes(record); // blob+99 + 126*i
        });
        return pw.ToArray();
    }

    /// <summary>
    /// RMI 2417 OnBreakDownItem. Envelope, msgTag and handle only, VARIABLE length — msgTag 58106; body lengths
    /// 25/152/278/404/530 (a variable material-record list). Built to the SHORTEST length (25 bytes) with
    /// blob+4 = the acting player's handle (low byte 0x11) and zeros elsewhere (empty result).
    /// ⚠️ Envelope-only form, kept for the envelope sweep test: outcome 0 with result 0 prints "Disassembly
    /// failed and the equipment and disassembly stone is consumed", so no handler may send it.
    /// BreakDownItemHandler sends the full overload below.
    /// </summary>
    public static byte[] BuildBreakDownItem2417(uint actorHandle) =>
        Build(MsgTagBreakDownItem, BodyLenBreakDownItemShortest, p => p.WriteUInt32(actorHandle));

    /// <summary>
    /// RMI 2417 OnBreakDownItem for a disassembly the caller has actually resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blob <c>23 + 126k</c>: <c>[u32 handle@4][u16 result@8][u8 outcome@10][u8 k@11][u8 slot@12]
    /// [u8 bag@13][u64 sourceUid@14][u8 remaining@22][k x ItemRecord126@23]</c>. The consumer
    /// tests <c>blob+10</c> to choose between string 7954 ("succeeded ... the disassembled items were
    /// sent to your inventory") and 7956 ("Disassembly failed and the equipment and disassembly stone
    /// is consumed"), and loops <c>blob+11</c> times over the output records. The two bytes are always equal, so
    /// both are written from the record count: an empty list IS the "failed, consumed" answer.
    /// </para>
    /// <para>
    /// slot, bag, sourceUid and remaining echo the 1328 request's blob+12/13/14/22 — in the no-output
    /// form too (k = 0 with the echo filled). With result 0, only send
    /// this once the source item has really been consumed server-side; otherwise the client is told an item
    /// it still holds was destroyed. A non-zero result (for example 22648, k = 0, nothing consumed) is
    /// the refusal: the consumer's result guard shows that code instead of either text.
    /// </para>
    /// </remarks>
    public static byte[] BuildBreakDownItem2417(uint actorHandle, ushort resultCode, byte slot, byte bag,
        ulong sourceUid, byte remaining, IReadOnlyList<byte[]> outputRecords126)
    {
        if (outputRecords126.Count > byte.MaxValue)
            throw new System.ArgumentException($"2417 carries at most 255 records; got {outputRecords126.Count}.",
                nameof(outputRecords126));
        foreach (var record in outputRecords126) RequireRecord(record, ZoneS2COpcodes.OnBreakDownItem);

        using var pw = new PacketWriter(32 + ItemPackets.ItemRecordLength * outputRecords126.Count);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagBreakDownItem);            // blob+2
            p.WriteUInt32(actorHandle);                    // blob+4
            p.WriteUInt16(resultCode);                     // blob+8
            p.WriteByte((byte)outputRecords126.Count);     // blob+10 outcome (non-zero = success text)
            p.WriteByte((byte)outputRecords126.Count);     // blob+11 record count
            p.WriteByte(slot);                             // blob+12
            p.WriteByte(bag);                              // blob+13
            p.WriteUInt64(sourceUid);                      // blob+14
            p.WriteByte(remaining);                        // blob+22
            foreach (var record in outputRecords126)
                p.WriteBytes(record);                      // blob+23 + 126*i
        });
        return pw.ToArray();
    }
}
