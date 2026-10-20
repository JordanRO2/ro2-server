using System;
using System.Collections.Generic;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Byte-exact S2C builders for the SIEGE notify family. Two envelope shapes appear here, and mixing
/// them crashes the client: • NONE-family (2934 / 2939 / 2940): the client reads a FIXED Blob&lt;N&gt;
/// straight from the message, no length prefix.
/// </summary>
public static class SiegePackets
{
 /// <summary>
 /// 2910 NfySiegeUnitPosition inner msgTag (blob+2, the u16 after innerSize). Do not invent.
 /// </summary>
    private const ushort TagNfySiegeUnitPosition = 58492; 

 /// <summary><paramref name="operatorHandle"/> = 0 means the seat was vacated.</summary>
    public static byte[] BuildSiegeArmsUserChanged(uint siegeArmsHandle, uint operatorHandle)
    {
        using var w = new PacketWriter(8);
        w.WriteUInt32(siegeArmsHandle);
        w.WriteUInt32(operatorHandle);
        return w.ToArray();
    }

 /// <summary>
 /// 2937 AnsSiegeArmsUseStart, the seated answer to 1637: VarBlob32 (U32 family) <c>[u32 innerLen =
 /// 8 + 12n][u16 result 0][u32 armHandle][u16 n][n × {u32 armSkillId, u32 remainingMs, u32
 /// totalMs}]</c>. Each entry is the arm's running cooldown: the skill of the arm's last successful
 /// shot, the time left, and Skill_Table_SkillLevel.Cooldown_Time × 1000.
 /// </summary>
    public static byte[] BuildSiegeArmsUseStart2937(uint siegeArmsHandle, IReadOnlyList<SiegeService.ArmCooldownEntry> cooldowns)
    {
        using var w = new PacketWriter(12 + 12 * cooldowns.Count);
        w.WriteUInt32((uint)(8 + 12 * cooldowns.Count)); 
        w.WriteUInt16(0);                                
        w.WriteUInt32(siegeArmsHandle);                  
        w.WriteUInt16((ushort)cooldowns.Count);          
        foreach (var c in cooldowns)
        {
            w.WriteUInt32(c.SkillId);
            w.WriteUInt32(c.RemainingMs);
            w.WriteUInt32(c.TotalMs);
        }
        return w.ToArray();
    }

 /// <summary>
 /// 2943 result for a shot fired while the arm's cooldown group is still running: 20032, which
 /// ShowResultCodeMessage shows as string_ui 2549 "Do not have enough time to cast this skill to
 /// use.".
 /// </summary>
    public const ushort ResultArmSkillCoolingDown = 20032;

 /// <summary>
 /// 2943 AnsSiegeArmsUseSkill refusal: NONE family, <c>[u16 result][u32 armSkillId 0][f32 cooldown
 /// 0]</c>, 10 bytes.
 /// </summary>
    public static byte[] BuildSiegeArmsUseSkillRefusal2943(ushort result)
    {
        using var w = new PacketWriter(10);
        w.WriteUInt16(result);
        w.WriteUInt32(0);   
        w.WriteSingle(0f);  
        return w.ToArray();
    }

 /// <summary>
 /// 2934 NfySiegeHPStatus: raw NONE-family <c>[u32 siegeNpcId][u32 maxHp][u32 curHp]</c>, length 12.
 /// This is the HP of the siege OBJECTIVES (Emperiums and gates), NOT of a siege arm: • blob+0 is a
 /// <c>Siege_NPC.ID</c>.
 /// </summary>
    public static byte[] BuildSiegeHPStatus(uint siegeNpcId, uint maxHp, uint curHp)
    {
        using var w = new PacketWriter(12);
        w.WriteUInt32(siegeNpcId);
        w.WriteUInt32(maxHp);
        w.WriteUInt32(curHp);
        return w.ToArray();
    }

 /// <summary>
 /// 2939 NfySiegeArmsSettingChanged: raw NONE-family <c>[u32 siegeArmsHandle][u8 setting]</c>,
 /// length 5. It precedes the arm's first use; nothing in the rotation path produces it.
 /// </summary>
    public static byte[] BuildSiegeArmsSettingChanged(uint siegeArmsHandle, byte setting)
    {
        using var w = new PacketWriter(5);
        w.WriteUInt32(siegeArmsHandle);
        w.WriteByte(setting);
        return w.ToArray();
    }

 /// <summary>
 /// One element of a 2910 NfySiegeUnitPosition body: <c>[u32 groupId][f32 x][f32 y][u8 a][u8 b][u8
 /// c]</c> = 15 bytes. It is not an actor handle.
 /// </summary>
    public readonly record struct SiegeUnitEntry(uint GroupId, float X, float Y, byte A, byte B, byte C);

 /// <summary>
 /// 2910 NfySiegeUnitPosition: COMPACT_INNER body <c>compactBA{ u16 innerSize, u16 msgTag=58492, u32
 /// 0, u16 count, count×SiegeUnitEntry }</c>. It is a PERIODIC broadcast, not a reply: consecutive
 /// 2910 on one connection are 10.0 s apart.
 /// </summary>
    public static byte[] BuildSiegeUnitPosition(IReadOnlyList<SiegeUnitEntry> units)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(TagNfySiegeUnitPosition);
            p.WriteUInt32(0u); 
            p.WriteUInt16((ushort)units.Count);
            foreach (var u in units)
            {
                p.WriteUInt32(u.GroupId);
                p.WriteSingle(u.X);
                p.WriteSingle(u.Y);
                p.WriteByte(u.A);
                p.WriteByte(u.B);
                p.WriteByte(u.C);
            }
        });
        return pw.ToArray();
    }

 /// <summary>A single-element 2910 body with flags (0,0,1), the modal one-element form.</summary>
    public static byte[] BuildSiegeUnitPositionSingle(uint groupId, float x, float y)
        => BuildSiegeUnitPosition(new[] { new SiegeUnitEntry(groupId, x, y, 0, 0, 1) });

 /// <summary>
 /// 2933 NfySiegeStatusInfo — BUILDER_ONLY. U32-family body <c>[u32 innerLen][payload]</c>.
 /// </summary>
    public static byte[] BuildSiegeStatusInfo()
        => Convert.FromHexString("2900000000000050555555a53f00000000f77f000001000200000002000000000000000000dac4c901dac4c901");
}
