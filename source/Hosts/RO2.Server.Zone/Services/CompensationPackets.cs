using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builder for RMI 2393 OnCompensation, which carries a kill's EXP state for the receiving player:
/// base level, EXP within the level and zeny wallet.
/// </summary>
/// <remarks>
/// <para>
/// The body is 27 bytes: <c>[01 19]</c> followed by a 25-byte blob
/// <c>[u16 innerSize=25][u16 msgTag][u32 receiver handle][u8 base level @blob+8][u64 EXP within level @blob+9][u64 zeny @blob+17]</c>.
/// </para>
/// <para>
/// The client moves its EXP bar and shows the EXP and zeny gain messages from this record. It is sent on the
/// receiver's own session, after the kill's EXP grant has been applied.
/// </para>
/// </remarks>
public static class CompensationPackets
{
 /// Message tag of 2393 (58056).
    internal const ushort MsgTag = 58056;

 /// Body length: the 2-byte compact length plus the 25-byte blob.
    internal const int BodyLength = 27;

 /// <summary>
 /// Base level at which kills stop paying EXP. Matches the cap used by the EXP level-up loop in
 /// <c>CombatService.GrantExpAsync</c>.
 /// </summary>
    internal const int LevelCap = 50;

 /// <summary>Builds the 27-byte 2393 body.</summary>
 /// <param name="receiverHandle">Receiver's PC handle.</param>
 /// <param name="level">Base level, 1 to 50.</param>
 /// <param name="expWithinLevel">EXP accumulated within the current level.</param>
 /// <param name="zeny">Zeny wallet balance.</param>
    public static byte[] BuildCompensation2393(uint receiverHandle, byte level, ulong expWithinLevel, ulong zeny)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTag);            
            p.WriteUInt32(receiverHandle);    
            p.WriteByte(level);               
            p.WriteUInt64(expWithinLevel);    
            p.WriteUInt64(zeny);              
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the 2393 for <paramref name="player"/> as it stands after an EXP grant: its handle, level, EXP
 /// within the level and wallet. A negative wallet is sent as 0.
 /// </summary>
    public static byte[] BuildFor(PlayerState player)
        => BuildCompensation2393(player.EntityId, player.Level, player.Exp, (ulong)Math.Max(0L, player.Gold));

 /// <summary>
 /// Sends the 2393 for <paramref name="player"/> on its own session once its kill EXP grant has returned.
 /// Sent only when the player was below <see cref="LevelCap"/> before the grant. Nothing is sent without a session.
 /// </summary>
    public static async Task SendAfterKillGrantAsync(ProudSession? session, PlayerState player, int levelBeforeGrant)
    {
        if (session is null || levelBeforeGrant >= LevelCap) return;
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnCompensation, BuildFor(player));
    }
}
