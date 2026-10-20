using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the skill-start pair: RMI 2715 AnsUseSkillStart and RMI 2716 NfyUseSkillStart.
/// </summary>
/// <remarks>
/// THE TWO ARE NOT INTERCHANGEABLE AND THEY DO NOT SHARE A SHAPE. 2715 is the answer to the caster and
/// is a BARE 29-byte struct with no compact-length envelope; 2716 is the broadcast and carries the
/// standard <c>[compactLen][u16 selfLen][u16 msgTag]</c> envelope. Percentages below are over the FULL
/// population of the stated class, never a prefix. 2717 AnsSkillError is NOT part of this flow.
/// </remarks>
public static class SkillPackets
{
    
 /// <summary>Body length of RMI 2715: 29 bytes.</summary>
    internal const int UseSkillStartAckBodyLen = 29;

 /// <summary>
 /// Result code that lets the cast proceed. Non-zero takes the client down the refusal branch.
 /// </summary>
 /// <remarks>This is the field the whole bug turned on.</remarks>
    public const ushort SkillStartOk = 0;

 /// <summary>
 /// Result code 20152, which rejects a cast made while the skill is still on server-tracked
 /// cooldown. The client ends the cast silently.
 /// </summary>
    public const ushort SkillResultOnCooldown = 20152;

 /// <summary>Value sent in the unmodelled +27 slot of RMI 2715.</summary>
    public const ushort SkillVariantModal = 1;

 /// <summary>
 /// Builds RMI 2715 AnsUseSkillStart, the grant that lets the client leave the run animation and
 /// attack.
 /// </summary>
 /// <param name="skillId">The skill the client asked to start, echoed from request +0.</param>
 /// <param name="targetHandle">The target actor handle, echoed from request +16.</param>
 /// <param name="targetX">Request +33, echoed.</param>
 /// <param name="targetY">Request +37, echoed.</param>
 /// <param name="targetZ">Request +41, echoed.</param>
 /// <param name="variant">See <see cref="SkillVariantModal"/>.</param>
 /// <param name="resultCode">See <see cref="SkillStartOk"/>. Non-zero aborts the cast.</param>
 /// <param name="cooldownSeconds">Cooldown in seconds for the skill group, keyed by skillId / 100.</param>
 /// <remarks>The body is bare, with no compact envelope: the reader expects a flat 29-byte body.</remarks>
    public static byte[] BuildUseSkillStartAck2715(
        uint skillId, uint targetHandle, float cooldownSeconds,
        float targetX, float targetY, float targetZ,
        ushort variant = SkillVariantModal, ushort resultCode = SkillStartOk)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(resultCode);     
        pw.WriteUInt32(skillId);        
        pw.WriteUInt32(targetHandle);   
        pw.WriteByte(targetHandle != 0 ? (byte)1 : (byte)0); 
        pw.WriteSingle(cooldownSeconds);
        pw.WriteSingle(targetX);        
        pw.WriteSingle(targetY);        
        pw.WriteSingle(targetZ);        
        pw.WriteUInt16(variant);        
        return pw.ToArray();
    }

    
 /// <summary>Wire message tag of RMI 2716.</summary>
    internal const ushort MsgTagUseSkillStart = 58442;

 /// <summary>
 /// Body length of the most common RMI 2716 shape: a 2-byte envelope plus a 65-byte blob, used when
 /// a cast has no extra targets.
 /// </summary>
    internal const int UseSkillStartNotifyBodyLen = 67;

 /// <summary>
 /// Builds RMI 2716 NfyUseSkillStart — the broadcast half, the message that makes every client in
 /// range play the caster's skill animation.
 /// </summary>
 /// <param name="casterHandle">The actor that performs the animation. Carries its type tag.</param>
 /// <param name="skillId">The skill being started.</param>
 /// <param name="casterCurHp">The caster's current HP, which the client pushes into its HP bar.</param>
 /// <param name="casterCurSp">
 /// The caster's current SP. Clamped to 16 bits because the client keeps only the low half.
 /// </param>
 /// <param name="targetHandle">The target actor, or 0 for a cast with no target.</param>
 /// <param name="variant">See <see cref="SkillVariantModal"/>.</param>
 /// <param name="extraTargets">
 /// Extra affected-actor handles. Leave empty unless you are modelling an area skill.
 /// </param>
 /// <param name="angle">
 /// blob+32: the 1508's f32 at +29 for a player's cast (remarks); 0 when there is no request.
 /// </param>
 /// <param name="x">blob+36: the 1508's f32 at +33.</param>
 /// <param name="y">blob+40: the 1508's f32 at +37.</param>
 /// <param name="z">blob+44: the 1508's f32 at +41.</param>
 /// <remarks>A monster's skill cast is what produces it. TWO COORDINATE SYSTEMS.</remarks>
    public static byte[] BuildUseSkillStart2716(
        uint casterHandle, uint skillId, uint casterCurHp, uint casterCurSp, uint targetHandle,
        ushort variant = SkillVariantModal, IReadOnlyList<uint>? extraTargets = null,
        float angle = 0f, float x = 0f, float y = 0f, float z = 0f)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagUseSkillStart);                  
            p.WriteUInt32(0);                                    
            p.WriteUInt32(skillId);                              
            p.WriteUInt32(casterHandle);                         
            p.WriteUInt32(casterCurHp);                          
            p.WriteUInt32(Math.Min(casterCurSp, ushort.MaxValue));
            p.WriteUInt32(0);                                    
            p.WriteUInt32(0);                                    
            p.WriteSingle(angle);                                
            p.WriteSingle(x);                                    
            p.WriteSingle(y);                                    
            p.WriteSingle(z);                                    
            p.WriteByte(targetHandle != 0 ? (byte)1 : (byte)0);  
            p.WriteUInt32(targetHandle);                         
            p.WriteUInt16(variant);                              
            var extra = extraTargets ?? Array.Empty<uint>();
            p.WriteUInt16((ushort)extra.Count);                  
            foreach (var h in extra)
                p.WriteUInt32(h);
            p.WriteSingle(0f);                                   
            p.WriteSingle(0f);
        });
        return pw.ToArray();
    }

 /// <summary>2744 AnsSkillMoveStart — the ack for 1522 ReqSkillMoveStart.</summary>
    public static byte[] BuildSkillMoveStartAck2744(uint skillId, ushort resultCode = 0)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(resultCode); 
        pw.WriteUInt32(skillId);    
        return pw.ToArray();        
    }
}
