using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the combat-skill notifications: 2496 OnChangeAbnormalSP, 2585
/// OnSkillChannelingCancelNotify, 2708 AnsSkillCastingStart, 2710 AnsSkillCastingCancel and 2717 AnsSkillError.
/// </summary>
/// <remarks>
/// <para>
/// 2495 OnDotDamageNotify is built by <see cref="SkillDamagePackets.BuildDotDamageNotify2495"/>.
/// </para>
/// <para>
/// 2496 and 2585 use the compact envelope written by <see cref="PacketWriter.WriteCompactBABody"/>. 2708, 2710 and
/// 2717 are fixed-length raw bodies.
/// </para>
/// </remarks>
public static class CombatSkillNotifyPackets
{
    /// <summary>Result code of a successful skill answer.</summary>
    private const ushort ResultSuccess = 0;

    /// <summary>Value of an unused envelope handle slot and of reserved fields.</summary>
    private const uint Reserved = 0;

    /// <summary>Message tag of 2496 (58366, 0xE3FE).</summary>
    private const ushort MsgTagChangeAbnormalSP = 58366;

    /// <summary>
    /// Builds RMI 2496 OnChangeAbnormalSP: an SP change caused by an abnormal state such as a buff or debuff.
    /// Compact envelope, 28-byte body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blob layout, all little-endian:
    /// </para>
    /// <code>
    /// blob+0 u16 selfLen = 26
    /// blob+2 u16 msgTag = 58366
    /// blob+4 u32 envHandle = 0
    /// blob+8 u32 targetHandle the actor whose SP changed
    /// blob+12 u32 sourceHandle the actor that caused it (equal to the target for self-sourced changes)
    /// blob+16 u32 reserved = 0
    /// blob+20 i32 spDrain the wire sign is inverted: a drain is sent positive
    /// blob+24 u16 targetCurSp the target's SP after the change
    /// </code>
    /// <para>
    /// The sign is converted here, so callers pass the server-side delta with negative meaning drain. Every value is
    /// supplied by the caller. The target's resulting SP is required so clients never show an empty gauge.
    /// </para>
    /// </remarks>
    /// <param name="targetHandle">The actor whose SP changed.</param>
    /// <param name="sourceHandle">The actor that caused the change. 0 means self.</param>
    /// <param name="spDelta">Signed SP change on the server (negative means drain). Negated on the wire.</param>
    /// <param name="targetCurSp">The target's SP after the change.</param>
    public static byte[] BuildChangeAbnormalSP2496(uint targetHandle, uint sourceHandle, int spDelta,
        ushort targetCurSp)
    {
        // A self-sourced change is the common case, so an unset source means the target itself rather than a null
        // handle the client could not resolve.
        if (sourceHandle == 0) sourceHandle = targetHandle;

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagChangeAbnormalSP); // blob+2  msgTag
            p.WriteUInt32(Reserved);               // blob+4  envHandle
            p.WriteUInt32(targetHandle);           // blob+8  actor whose SP changed
            p.WriteUInt32(sourceHandle);           // blob+12 actor that caused it
            p.WriteUInt32(Reserved);               // blob+16 reserved
            p.WriteInt32(-spDelta);                // blob+20 spDrain: the client computes newSp = prevSp - this
            p.WriteUInt16(targetCurSp);            // blob+24 resulting SP
        });
        return pw.ToArray();
    }

    /// <summary>Constant two-byte tail of the 2708 body, a fixed pair of flag bytes (0x01, 0x01).</summary>
    private const ushort CastAckTail = 0x0101;

    /// <summary>
    /// Builds RMI 2708 AnsSkillCastingStart, the caster's own acknowledgement that a cast started. Fixed 49-byte raw
    /// body, no compact envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout, all little-endian: <c>[u16 result 0][u32 skillId][u64 castId][f32 castSeconds][u8 hasTarget]
    /// [u32 targetHandle][f32 x][f32 y][f32 z][u32 0][u32 0][u32 0][u16 tail]</c>. The cast position is stale when a
    /// target is set.
    /// </para>
    /// <para>
    /// Sent unicast to the caster, paired with the broadcast 2709 NfySkillCastingStart.
    /// </para>
    /// </remarks>
    public static byte[] BuildSkillCastingStartAck2708(uint skillId, ulong castId, float castSeconds,
        uint targetHandle, float x, float y, float z, ushort result = ResultSuccess)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(result);                              // @0  result
        pw.WriteUInt32(skillId);                             // @2  skill id
        pw.WriteUInt64(castId);                              // @6  cast id echoed from the request
        pw.WriteSingle(castSeconds);                         // @14 cast bar duration
        pw.WriteByte(targetHandle != 0 ? (byte)1 : (byte)0); // @18 hasTarget, derived from targetHandle
        pw.WriteUInt32(targetHandle);                        // @19 target handle
        pw.WriteSingle(x);                                   // @23 cast position
        pw.WriteSingle(y);                                   // @27
        pw.WriteSingle(z);                                   // @31
        pw.WriteUInt32(Reserved);                            // @35 reserved
        pw.WriteUInt32(Reserved);                            // @39 reserved
        pw.WriteUInt32(Reserved);                            // @43 reserved
        pw.WriteUInt16(CastAckTail);                         // @47 constant tail
        return pw.ToArray();
    }

    /// <summary>
    /// Builds RMI 2710 AnsSkillCastingCancel, the caster's answer to a cast-cancel request. Fixed 6-byte raw body:
    /// <c>[u16 result][u32 skillId]</c>, where the skill id echoes the request.
    /// </summary>
    /// <remarks>
    /// The result is 0 in every answer. The caster also receives a 2711 NfySkillCastingCancel for the same skill.
    /// </remarks>
    public static byte[] BuildSkillCastingCancelAck2710(uint skillId)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(ResultSuccess); // @0 result
        pw.WriteUInt32(skillId);       // @2 skill id echoed from the request
        return pw.ToArray();
    }

    /// <summary>
    /// Builds RMI 2717 AnsSkillError, the caster's unicast refusal of a skill request. Fixed 6-byte raw body:
    /// <c>[u16 resultCode][u32 skillId]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result code is a message id of the same kind as the one used by 2715. The caller chooses it, because the
    /// mapping from failure to id is not established.
    /// </para>
    /// </remarks>
    public static byte[] BuildSkillError2717(ushort resultCode, uint skillId)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(resultCode);  // @0 message id of the refusal
        pw.WriteUInt32(skillId);     // @2 refused skill (Skill_Group * 100 + level)
        return pw.ToArray();
    }

    /// <summary>Message tag of 2585 (58288, 0xE3B0).</summary>
    private const ushort MsgTagSkillChannelingCancelNotify = 58288;

    /// <summary>
    /// Builds RMI 2585 OnSkillChannelingCancelNotify: a channelled skill of <paramref name="casterHandle"/> ended early.
    /// Compact envelope, 18-byte body: <c>[u16 selfLen=16][u16 msgTag][u32 0][u32 casterHandle][u32 skillId]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The skill id is in the <c>Skill_Group * 100 + level</c> encoding. A wrong id matches no channelled skill on the
    /// client, so the caller must pass the id of the skill being cancelled.
    /// </para>
    /// </remarks>
    public static byte[] BuildSkillChannelingCancelNotify2585(uint casterHandle, uint skillId)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSkillChannelingCancelNotify); // blob+2  msgTag
            p.WriteUInt32(Reserved);                          // blob+4  envHandle
            p.WriteUInt32(casterHandle);                      // blob+8  actor whose channel ended
            p.WriteUInt32(skillId);                           // blob+12 cancelled skill (Skill_Group * 100 + level)
        });
        return pw.ToArray();
    }
}
