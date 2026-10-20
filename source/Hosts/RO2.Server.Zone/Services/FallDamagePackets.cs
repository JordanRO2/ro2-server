using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builder and damage rule for RMI 2815 NfyFallDamage, the server's answer to C2S 1565
/// ReqFallDamage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why 2815 and not 2401.</b> A fall is answered with 2815, not 2401 OnActorAutoHeal. The 2815 follows the
/// 1565 for the same handle almost immediately, and it is also sent for other PCs' falls. 2815 is the only fall
/// message that sets the HP bar and shows a floating damage number; 2401 has no damage field at all, so
/// answering with it could never show a fall costing anything.
/// </para>
/// <para>
/// <b>Layout</b> (blob coordinates; with the 1-byte compact marker blob+X == body+X+2):
/// <code>
///   [compactLen 01 14][u16 selfLen=20][u16 msgTag=58480]
///   [u32 pcHandle]   blob+4   never read by the client; equals blob+8 in every retail frame
///   [u32 pcHandle]   blob+8   CActorRegistry lookup
///   [u32 curHp]      blob+12  HP AFTER the fall; the client stores it as current HP
///   [i32 damage]     blob+16  floating combat number; the consumer skips it unless &gt; 0
/// </code>
/// Body is 22 bytes, unencrypted, marker 01 and selfLen 20, msgTag 58480, with blob+4 == blob+8, low byte 0x11
/// (PC) and damage never negative. The field roles match 2495 OnDotDamageNotify argument for argument.
/// </para>
/// <para>
/// The handle is written twice because retail does so in every frame. The client never reads
/// blob+4, so the first copy exists only to match retail's bytes. Nothing here separates "second
/// handle field" from "unused slot the server fills with the handle". Either way, sending the same
/// handle is correct.
/// </para>
/// </remarks>
public static class FallDamagePackets
{
    /// <summary>msgTag of 2815 NfyFallDamage: 58480 (0xE470).</summary>
    internal const ushort MsgTagFallDamage = 58480; // 0xE470

    /// <summary>Body length of 2815 in bytes.</summary>
    internal const int FallDamageBodyLen = 22;

    /// <summary>
    /// Fall magnitude at and below which no damage is charged. The <c>(v - 10)</c> term in
    /// <see cref="RetailFallDamage"/> is where the rule's zero sits: a fall of v &lt;= 10 takes 0 damage and any
    /// v &gt; 10 takes some.
    /// </summary>
    internal const float SafeFallMagnitude = 10f;

    /// <summary>
    /// Fraction of max HP charged for each unit of fall magnitude past <see cref="SafeFallMagnitude"/> (5%).
    /// </summary>
    internal const double MaxHpFractionPerFallUnit = 0.05;

    /// <summary>
    /// Defensive ceiling on a client-reported fall magnitude. Real falls stay well below it, so 200 sits above
    /// every real one. UNVERIFIED: the number is a guard, not a game rule; the server has no authoritative Z delta
    /// to compare the f32 with (the position is client-driven), so finiteness plus this ceiling is the honest limit
    /// on a forged +Inf / 3e38 self-kill.
    /// </summary>
    internal const float MaxFallMagnitude = 200f;

    /// <summary>
    /// False for a magnitude no real fall produces (NaN, +-Inf, negative): the 1565 is ignored. Otherwise
    /// <paramref name="clamped"/> is the magnitude limited to <see cref="MaxFallMagnitude"/>.
    /// </summary>
    internal static bool TryGuardMagnitude(float fallMagnitude, out float clamped)
    {
        clamped = 0f;
        if (!float.IsFinite(fallMagnitude) || fallMagnitude < 0f) return false;
        clamped = Math.Min(fallMagnitude, MaxFallMagnitude);
        return true;
    }

    /// <summary>
    /// Retail's fall damage for the magnitude the client reported in 1565.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>round(maxHp * (v - 10) * 0.05)</c>, and 0 for v &lt;= 10. Each unit of fall past the safe
    /// height costs 5% of max HP. The rounding mode at an exact .5 midpoint is not established, and this
    /// server's max HP is an integer.
    /// </para>
    /// <para>
    /// The result is <b>not capped at max HP</b>: a fall can cost more than max HP. The damage field shows that
    /// raw number; <see cref="ApplyFallDamage"/> floors the HP at 0.
    /// </para>
    /// </remarks>
    public static int RetailFallDamage(float fallMagnitude, int maxHp)
    {
        // A non-finite or negative magnitude is rejected (0), and a huge one is clamped to MaxFallMagnitude (a guard,
        // not a game rule: see MaxFallMagnitude). Every real magnitude is below it, so real values are unchanged.
        if (!TryGuardMagnitude(fallMagnitude, out var v)) return 0;
        if (!(v > SafeFallMagnitude) || maxHp <= 0) return 0;
        var dmg = Math.Round(maxHp * (v - (double)SafeFallMagnitude) * MaxHpFractionPerFallUnit);
        return dmg >= int.MaxValue ? int.MaxValue : (int)dmg;
    }

    /// <summary>
    /// HP after a fall of <paramref name="damage"/>, and whether it kills.
    /// </summary>
    /// <remarks>
    /// HP is floored at 0 and a fall that reaches 0 kills, inside the faller's death sequence (see
    /// FallDamageHandler). There is no clamp at 1 HP. A fall of 0 (at or under the safe height) leaves HP
    /// as it is, and a PC already at 0 HP is not killed again.
    /// </remarks>
    public static (int HpAfter, bool Lethal) ApplyFallDamage(int hp, int damage)
    {
        if (damage <= 0 || hp <= 0) return (hp, false);
        long after = (long)hp - damage;
        return after <= 0 ? (0, true) : ((int)after, false);
    }

    /// <summary>
    /// Builds RMI 2815 NfyFallDamage.
    /// </summary>
    /// <param name="pcHandle">The falling PC's actor handle (<c>PlayerState.EntityId</c>).</param>
    /// <param name="hpAfter">The PC's current HP AFTER the fall. The client stores it as current HP.</param>
    /// <param name="damage">The number to float over the PC. 0 shows nothing, since the client
    /// only displays values above 0. Among self falls, a 0 means exactly a fall at or under the safe
    /// height.</param>
    /// <remarks>
    /// RECIPIENTS: the faller and every PC that can see them. The 2815 is also sent for a PC other than the one
    /// that just sent a 1565, so it must be broadcast, not unicast to the sender.
    /// </remarks>
    public static byte[] BuildFallDamage2815(uint pcHandle, int hpAfter, int damage)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagFallDamage);
            p.WriteUInt32(pcHandle);                  // blob+4, never read
            p.WriteUInt32(pcHandle);                  // blob+8, registry lookup
            p.WriteUInt32((uint)Math.Max(0, hpAfter)); // blob+12, current HP
            p.WriteInt32(Math.Max(0, damage));        // blob+16, floating number
        });
        return pw.ToArray();
    }
}
