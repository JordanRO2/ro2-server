using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the buff and debuff (effect) messages: 2492 OnAddEffect, 2493 OnDelEffectSuccess, 2494 OnDelEffect
/// and 2819 NfyNpcEffect.
/// </summary>
/// <remarks>
/// <para>
/// Each body is enveloped: it opens with a compact-length marker (1, 2, 4 or 8) followed by a u16 self length equal to the
/// blob length. Use <see cref="PacketWriter.WriteCompactBABody"/> for every such body.
/// </para>
/// <para>
/// In 2492 and 2494 the handle and effect id fields appear in opposite order: 2492 carries the target handle before the
/// effect id, and 2494 carries the effect id before the handle.
/// </para>
/// <para>
/// Effect ids are Effect_Table ids. Source skill ids are Skill_Table_SkillLevel ids.
/// </para>
/// </remarks>
public static class EffectPackets
{
    /// <summary>Value of every reserved / constant-zero slot in these bodies.</summary>
    private const uint ReservedZero = 0;

    // ---- 2492 OnAddEffect -------------------------------------------------------------------

    /// <summary>msgTag of 2492 OnAddEffect.</summary>
    internal const ushort MsgTagAddEffect = 58311;      // 0xE3C7

    /// <summary>Body length of 2492 in bytes.</summary>
    internal const int AddEffectBodyLen = 38;

    /// <summary><c>applyMode</c> sent for the usual case; 1 and 2 also occur.</summary>
    internal const byte ApplyModeDefault = 0;

    // ---- 2493 OnDelEffectSuccess ------------------------------------------------------------

    /// <summary>msgTag of 2493 OnDelEffectSuccess.</summary>
    internal const ushort MsgTagDelEffectSuccess = 58312; // 0xE3C8

    /// <summary>Body length of 2493 in bytes.</summary>
    internal const int DelEffectSuccessBodyLen = 22;

    // ---- 2819 NfyNpcEffect ------------------------------------------------------------------

    /// <summary>msgTag of 2819 NfyNpcEffect.</summary>
    internal const ushort MsgTagNpcEffect = 58481;      // 0xE471

    /// <summary>Body length of 2819 in bytes.</summary>
    internal const int NpcEffectBodyLen = 15;

    /// <summary>Value of the <c>kind</c> byte of 2819; it is always 1 on the wire.</summary>
    internal const byte NpcEffectKindDefault = 1;

    /// <summary>Builds RMI 2492 OnAddEffect: one buff or debuff applied to one actor.</summary>
    /// <remarks>
    /// <para>
    /// Layout (blob coordinates): <c>[u16 selfLen][u16 msgTag][u32 0][u32 targetHandle][u32 effectId][u16 effectValue]
    /// [u64 durationMs][u8 stackCount][u32 sourceSkillId][u8 applyMode][u32 casterHandle]</c>.
    /// The fields after blob+16 are not on a 4-byte grid: durationMs starts at blob+18, stackCount at blob+26,
    /// sourceSkillId at blob+27, applyMode at blob+31 and casterHandle at blob+32. Do not realign them.
    /// </para>
    /// <para>
    /// Actor handles are type-tagged: 0x11 for a player and 0x13 for an NPC.
    /// </para>
    /// <para>
    /// <c>durationMs</c> is a 64-bit value in milliseconds, and 0 means permanent. The caller computes it and passes it in.
    /// </para>
    /// <para>
    /// The message is broadcast to everyone who can see the target, including the target itself, so that the target's own buff
    /// bar is populated. Use a map broadcast without exclusion.
    /// </para>
    /// </remarks>
    /// <param name="targetHandle">Actor the effect is applied to; type-tagged.</param>
    /// <param name="effectId">Effect_Table.ID.</param>
    /// <param name="effectValue">Magnitude of the effect, supplied by the caller.</param>
    /// <param name="durationMs">Remaining duration in milliseconds; 0 means permanent.</param>
    /// <param name="stackCount">1 to 100; must not exceed Effect_Table.Stack_Num for the effect.</param>
    /// <param name="sourceSkillId">Skill_Table_SkillLevel.ID, or 0 when the effect is not applied by a skill.</param>
    /// <param name="casterHandle">Actor that applied the effect; type-tagged.</param>
    /// <param name="applyMode">0 for the usual case; 1 and 2 also occur.</param>
    public static byte[] BuildAddEffect2492(
        uint targetHandle, uint effectId, ushort effectValue, ulong durationMs,
        byte stackCount, uint sourceSkillId, uint casterHandle, byte applyMode = ApplyModeDefault)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagAddEffect); // blob+2
            p.WriteUInt32(ReservedZero);    // blob+4
            p.WriteUInt32(targetHandle);    // blob+8  type-tagged handle
            p.WriteUInt32(effectId);        // blob+12 Effect_Table.ID
            p.WriteUInt16(effectValue);     // blob+16 u16, not u32
            p.WriteUInt64(durationMs);      // blob+18 u64, milliseconds
            p.WriteByte(stackCount);        // blob+26
            p.WriteUInt32(sourceSkillId);   // blob+27
            p.WriteByte(applyMode);         // blob+31
            p.WriteUInt32(casterHandle);    // blob+32 type-tagged handle
        });
        return pw.ToArray();
    }

    // 2494 OnDelEffect is not built here; see ActorVitalsPackets.BuildDelEffect2494. Its wire order
    // (effectId, then handle) is the reverse of 2492's.

    /// <summary>
    /// Builds RMI 2493 OnDelEffectSuccess: the unicast acknowledgement that an effect owned by the receiving player has been removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout (blob coordinates): <c>[u16 selfLen=20][u16 msgTag][u32 0][u32 effectId][u64 0]</c>. The message carries no actor handle.
    /// </para>
    /// <para>
    /// It is unicast to the player whose effect was removed, and is sent immediately before the 2494 broadcast for the same
    /// removal. The client applies it to the local player only.
    /// </para>
    /// </remarks>
    public static byte[] BuildDelEffectSuccess2493(uint effectId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagDelEffectSuccess); // blob+2
            p.WriteUInt32(ReservedZero);           // blob+4
            p.WriteUInt32(effectId);               // blob+8  Effect_Table.ID
            p.WriteUInt64(ReservedZero);           // blob+12 trailing 8 zero bytes, never read
        });
        return pw.ToArray();
    }

    /// <summary>Builds RMI 2819 NfyNpcEffect: a notification that names an NPC.</summary>
    /// <remarks>
    /// <para>
    /// Layout (blob coordinates): <c>[u16 selfLen=13][u16 msgTag][u32 0][u32 npcHandle][u8 kind]</c>. The client dismisses the
    /// named NPC's pets. The <c>kind</c> byte is always 1 (<see cref="NpcEffectKindDefault"/>).
    /// </para>
    /// <para>
    /// The subject is always an NPC (handle tag 0x13), and the message is broadcast to everyone who can see it. The event that
    /// triggers it is not established, so use this builder only for a known trigger.
    /// </para>
    /// </remarks>
    public static byte[] BuildNpcEffect2819(uint npcHandle, byte kind = NpcEffectKindDefault)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagNpcEffect); // blob+2
            p.WriteUInt32(ReservedZero);    // blob+4
            p.WriteUInt32(npcHandle);       // blob+8  NPC handle, tag 0x13
            p.WriteByte(kind);              // blob+12
        });
        return pw.ToArray();
    }
}
