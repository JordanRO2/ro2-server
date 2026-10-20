using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for the NPC interaction and function-object control notifies sent in reply to a client's
/// 1410 <c>ReqFunctionObjectControl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two client actions are handled separately:
/// </para>
/// <list type="bullet">
/// <item><b>Dialog NPCs</b> (quest givers, shopkeepers): the client opens the dialogue window locally after
/// 1698 <c>NfyMeetDialogNPC</c>. The server sends no reply; <see cref="Handlers.Rmi.MeetDialogNPCHandler"/> does nothing.</item>
/// <item><b>Function objects</b> (gathering nodes, siege objects, interactive props): the server answers 1410
/// with 2515 <c>OnFuncObjectControl</c>, then 2516 <c>OnFuncObjectControlNotify</c>. This pair opens the control
/// bar, and it is what <see cref="Handlers.Rmi.NpcInteractHandler"/> sends.</item>
/// </list>
/// <para>
/// Both 2515 and 2516 use the compact-BA envelope (<c>[compactLen][u16 selfLen][blob]</c>) and are built with
/// <see cref="PacketWriter.WriteCompactBABody"/>.
/// </para>
/// </remarks>
public static class NpcDialoguePackets
{
    /// <summary>The reserved u32 at blob+4 (and the trailing pad words) of 2515 and 2516; always zero.</summary>
    private const uint BlobReservedWord = 0;

    /// <summary>msgTag of RMI 2515 OnFuncObjectControl (0xE3F2), blob+2.</summary>
    internal const ushort MsgTagFuncObjectControl = 58354;

    /// <summary>
    /// Builds RMI 2515 OnFuncObjectControl, which starts the control bar the controlling client shows when it begins
    /// interacting with a function object. Sent unicast to the interacting player.
    /// </summary>
    /// <remarks>
    /// <para>Wire layout (18 bytes, compact-BA envelope):</para>
    /// <code>
    /// [compactLen=01 10][u16 selfLen=16] blob+0 selfLen == len(blob)
    /// [u16 msgTag = 0xE3F2] blob+2
    /// [u32 0] blob+4
    /// [u32 funcObjectHandle] blob+8
    /// [u32 0] blob+12
    /// </code>
    /// </remarks>
    /// <param name="funcObjectHandle">Actor handle of the function object being controlled.</param>
    public static byte[] BuildFuncObjectControl2515(uint funcObjectHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagFuncObjectControl); // blob+2
            w.WriteUInt32(BlobReservedWord);        // blob+4
            w.WriteUInt32(funcObjectHandle);        // blob+8
            w.WriteUInt32(BlobReservedWord);        // blob+12
        });
        return pw.ToArray();
    }

    /// <summary>msgTag of RMI 2516 OnFuncObjectControlNotify (0xE3F3), blob+2; one above 2515's tag.</summary>
    internal const ushort MsgTagFuncObjectControlNotify = 58355;

    /// <summary>
    /// Builds RMI 2516 OnFuncObjectControlNotify, which tells nearby players that a PC has started controlling a
    /// function object, so they can show the channel bar on that PC. Sent to nearby players and to the controller.
    /// </summary>
    /// <remarks>
    /// <para>Wire layout (22 bytes, compact-BA envelope):</para>
    /// <code>
    /// [compactLen=01 14][u16 selfLen=20] blob+0 selfLen == len(blob)
    /// [u16 msgTag = 0xE3F3] blob+2
    /// [u32 0] blob+4
    /// [u32 pcHandle] blob+8 controlling player's handle
    /// [u32 funcObjectHandle] blob+12 function object handle
    /// [u32 0] blob+16
    /// </code>
    /// </remarks>
    /// <param name="pcHandle">Actor handle of the controlling player.</param>
    /// <param name="funcObjectHandle">Handle of the function object, as in the paired 2515.</param>
    public static byte[] BuildFuncObjectControlNotify2516(uint pcHandle, uint funcObjectHandle)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagFuncObjectControlNotify); // blob+2
            w.WriteUInt32(BlobReservedWord);              // blob+4
            w.WriteUInt32(pcHandle);                      // blob+8
            w.WriteUInt32(funcObjectHandle);              // blob+12
            w.WriteUInt32(BlobReservedWord);              // blob+16
        });
        return pw.ToArray();
    }





















}
