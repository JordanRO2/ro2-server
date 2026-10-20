using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builder for RMI 2962 NfySkillStartError, the broadcast that goes out with every refused 2715
/// AnsUseSkillStart.
/// </summary>
/// <remarks>
/// WHY IT EXISTS. 2715 is unicast, so only the caster learns that the cast was refused. Everyone else
/// in range needs 2962 to learn it too.
/// </remarks>
public static class SkillStartErrorPackets
{
 /// <summary>Wire message tag of RMI 2962.</summary>
    internal const ushort MsgTagSkillStartError = 58498;

 /// <summary>
 /// Body length. The client reads it with a single fixed <c>Blob&lt;18&gt;</c>, and selfLen at +0
 /// holds the same value.
 /// </summary>
    internal const int SkillStartErrorBodyLen = 18;

 /// <summary>Builds RMI 2962 NfySkillStartError.</summary>
 /// <param name="resultCode">The same non-zero code sent in the paired 2715.</param>
 /// <param name="skillId">The skill whose start was refused, the same one the 2715 echoes.</param>
 /// <param name="casterHandle">The caster's actor handle, the entity id with its type tag.</param>
 /// <remarks>
 /// The fields are read straight from the message (envelope "none"), so the params struct the
 /// consumer indexes is these 18 bytes starting at 0.
 /// </remarks>
    public static byte[] BuildSkillStartError2962(ushort resultCode, uint skillId, uint casterHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt16(SkillStartErrorBodyLen); 
        pw.WriteUInt16(MsgTagSkillStartError);  
        pw.WriteUInt32(0);                      
        pw.WriteUInt16(resultCode);             
        pw.WriteUInt32(skillId);                
        pw.WriteUInt32(casterHandle);           
        return pw.ToArray();                    
    }
}
