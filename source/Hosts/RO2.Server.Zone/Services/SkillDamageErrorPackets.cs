using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builder for RMI 2963 NfySkillDamageError, the broadcast that goes out with every 2717
/// AnsSkillError, i.e. with every refused 1509 ReqSkillDamage.
/// </summary>
/// <remarks>
/// WHY IT EXISTS. 2717 is unicast, so only the caster learns that the hit was refused. The other
/// clients in range learn it from 2963.
/// </remarks>
public static class SkillDamageErrorPackets
{
 /// <summary>Wire message tag of RMI 2963.</summary>
    internal const ushort MsgTagSkillDamageError = 58499;

 /// <summary>
 /// Body length. The client reads it with a single fixed <c>Blob&lt;18&gt;</c>, and selfLen at +0
 /// holds the same value.
 /// </summary>
    internal const int SkillDamageErrorBodyLen = 18;

 /// <summary>Result code for a 1509 whose primary target is an NPC that is already dead.</summary>
    public const ushort ResultPrimaryTargetDead = 24310;

 /// <summary>Builds RMI 2963 NfySkillDamageError.</summary>
 /// <param name="resultCode">The same code sent in the paired 2717.</param>
 /// <param name="skillId">The skill whose hit was refused, the same one the 2717 echoes.</param>
 /// <param name="casterHandle">The caster's actor handle, the entity id with its type tag.</param>
    public static byte[] BuildSkillDamageError2963(ushort resultCode, uint skillId, uint casterHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(SkillDamageErrorBodyLen); 
        pw.WriteUInt16(MsgTagSkillDamageError);  
        pw.WriteUInt32(0);                       
        pw.WriteUInt16(resultCode);              
        pw.WriteUInt32(skillId);                 
        pw.WriteUInt32(casterHandle);            
        return pw.ToArray();                     
    }
}
