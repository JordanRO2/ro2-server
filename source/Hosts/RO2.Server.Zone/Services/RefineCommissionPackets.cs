using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// RMI 3000 NfyRefineCommissionToBuyer: the refine-service terms of a private shop, sent to a buyer who opens it. It is
/// the third of the four answers to the shop open (2657, 2658, 3000, 3006).
/// </summary>
/// <remarks>
/// The body carries the owner's unit price for one refine step and three refine rates (Trait_Type 100, 101 and 102),
/// which the client shows in the refine window. The rates come from the owner's worn items. This is display only: the
/// shop refine does not yet apply the owner's traits.
/// Blob layout (offsets from the start of the 22-byte blob): +0 u16 inner size (22), +2 u16 msgTag, +4 u32 buyer handle,
/// +8 u64 unit price, +16/+18/+20 the three u16 refine traits. The client divides each trait by 100 and adds it to the
/// catalyst rates it shows in the refine window.
/// </remarks>
internal static class RefineCommissionPackets
{
 /// <summary>3000 msgTag 58511 (0xE48F).</summary>
    internal const ushort MsgTagRefineCommissionToBuyer = 0xE48F;

 /// <summary>
 /// Total wire size: the 2-byte compact prefix (<c>01 16</c>) plus the 22-byte blob (u16 inner size, u16 msgTag, u32 buyer
 /// handle, u64 unit price, three u16 refine traits). Used as the writer's initial capacity.
 /// </summary>
    private const int WireBytes = 24;

 /// <summary>Trait_Type 100: the +2 refine success rate.</summary>
    internal const byte TraitRefineSuccess2Grade = 100;

 /// <summary>Trait_Type 101: the +3 refine success rate.</summary>
    internal const byte TraitRefineSuccess3Grade = 101;

 /// <summary>Trait_Type 102: the reduction of the grade drop on a failed refine.</summary>
    internal const byte TraitRefineProtectDown = 102;

 /// <summary>The three refine traits the owner's worn items give, in wire order (blob+16/18/20).</summary>
    internal readonly record struct RefineRates(ushort Success2Grade, ushort Success3Grade, ushort ProtectDown);

 /// <summary>
 /// The owner's refine traits from its worn-item bonuses: the flat sums of Trait_Type 100, 101 and 102
 /// (the hammers are Trait_Kind 1), each clamped to a u16.
 /// </summary>
    internal static RefineRates RatesOf(StatService.EquipStats worn) => new(
        ToU16(worn.FlatOf(TraitRefineSuccess2Grade)),
        ToU16(worn.FlatOf(TraitRefineSuccess3Grade)),
        ToU16(worn.FlatOf(TraitRefineProtectDown)));

 /// <summary>
 /// The 3000 body, compact prefix included: <c>01 16 [u16 22][u16 58511][u32 buyer][u64 price]
 /// [u16 trait 100][u16 trait 101][u16 trait 102]</c>.
 /// </summary>
 /// <param name="buyerHandle">The receiver's own handle.</param>
 /// <param name="unitPrice">The owner's refine price per step; 0 without a refine service.</param>
 /// <param name="ownerRates">The owner's worn refine traits (<see cref="RatesOf"/>).</param>
    internal static byte[] BuildRefineCommissionToBuyer3000(uint buyerHandle, ulong unitPrice, RefineRates ownerRates)
    {
        using var w = new PacketWriter(WireBytes);
        w.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagRefineCommissionToBuyer);   // blob+2  msgTag
            p.WriteUInt32(buyerHandle);                     // blob+4  the buyer (the receiver)
            p.WriteUInt64(unitPrice);                       // blob+8  u64 unit price per refine step
            p.WriteUInt16(ownerRates.Success2Grade);        // blob+16 trait 100
            p.WriteUInt16(ownerRates.Success3Grade);        // blob+18 trait 101
            p.WriteUInt16(ownerRates.ProtectDown);          // blob+20 trait 102
        });
        return w.ToArray();
    }

    private static ushort ToU16(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);
}
