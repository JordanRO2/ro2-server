using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The two job (profession) progress messages: 2526 OnNotifyProJobExpChange (experience gained) and 2527
/// OnNotifyProJobLevelUpPC (job level reached). Both are compact-envelope byte arrays written with
/// <see cref="PacketWriter.WriteCompactBABody"/>.
/// </summary>
/// <remarks>
/// 2526 carries the experience within the current level after the gain, and the gain for the chat message. 2527
/// carries the handle of the levelling player and the new job level. The local player also gets a chat message and
/// an experience bar refresh, and nearby players receive the notice too.
/// </remarks>
internal static class ProfessionExpPackets
{
    /// <summary>2526 OnNotifyProJobExpChange message tag (0xE41B), blob+2.</summary>
    internal const ushort MsgTagProJobExpChange = 58395;

    /// <summary>2527 OnNotifyProJobLevelUpPC message tag (0xE41D), blob+2.</summary>
    internal const ushort MsgTagProJobLevelUp = 58397;

    /// <summary>The reserved u32 at blob+4 of both messages; always zero.</summary>
    private const uint BlobReservedWord = 0;

    /// <summary>2526 body: compact envelope, msgTag, reserved u32, then u64 exp and u64 gain.</summary>
    /// <param name="expInLevel">The job exp within the level after the gain (Character.JobExp), blob+8.</param>
    /// <param name="gain">The exp gained, blob+16, shown in string 7815.</param>
    public static byte[] BuildProJobExpChange2526(ulong expInLevel, ulong gain)
    {
        using var pw = new PacketWriter(32);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagProJobExpChange);   // blob+2
            p.WriteUInt32(BlobReservedWord);        // blob+4
            p.WriteUInt64(expInLevel);              // blob+8: job exp within the level after the gain
            p.WriteUInt64(gain);                    // blob+16: exp gained, shown in the chat line
        });
        return pw.ToArray();
    }

    /// <summary>2527 body: compact envelope, msgTag, reserved u32, then u32 handle and u8 level.</summary>
    /// <param name="pcHandle">The levelling PC's actor handle, blob+8.</param>
    /// <param name="newLevel">The job level reached (Character.JobLevel), blob+12.</param>
    public static byte[] BuildProJobLevelUp2527(uint pcHandle, byte newLevel)
    {
        using var pw = new PacketWriter(24);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagProJobLevelUp);     // blob+2
            p.WriteUInt32(BlobReservedWord);        // blob+4
            p.WriteUInt32(pcHandle);                // blob+8: actor handle of the levelling PC
            p.WriteByte(newLevel);                  // blob+12: job level reached
        });
        return pw.ToArray();
    }
}
