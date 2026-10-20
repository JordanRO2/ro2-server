using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for two function-object notifies: 2729 NfyNpcChat (an NPC chat bubble) and 2513
/// OnFuncObject_Out (a function object leaving the client's view). 2729 is sent by <see cref="NpcChatService"/>;
/// 2513 is not yet wired.
/// </summary>
public static class NpcFuncObjNotifyPackets
{



 /// <summary>
 /// Builds RMI 2729 NfyNpcChat, the bubble an NPC shows over its head. The body is a bare 8-byte record, without
 /// a compact-BA envelope.
 /// </summary>
 /// <remarks>
 /// <para>Wire layout (8 bytes):</para>
 /// <code>
 /// [u32 npcHandle] body+0 NPC actor handle
 /// [u32 chatMessageId] body+4 Text_StringID of a row in Npc_Dlg.json
 /// </code>
 /// <para>
 /// <paramref name="chatMessageId"/> is laid out as <c>templatePrefix·10000 + situation·100 + index</c>, so the
 /// speaking NPC template is <c>Text_StringID / 10000</c>. The caller must pass a real row; there is no placeholder.
 /// </para>
 /// <para>
 /// <see cref="NpcChatService"/> sends situations 01 (idle chatter while a player is nearby) and 02 (combat line).
 /// </para>
 /// </remarks>
 /// <param name="npcHandle">Handle of the speaking NPC.</param>
 /// <param name="chatMessageId">Text_StringID of the line to show.</param>
    public static byte[] BuildNfyNpcChat2729(uint npcHandle, uint chatMessageId)
    {
        using var pw = new PacketWriter();
        pw.WriteUInt32(npcHandle);      // body+0
        pw.WriteUInt32(chatMessageId);  // body+4
        return pw.ToArray();
    }




 /// <summary>msgTag of RMI 2513 OnFuncObject_Out: 58351, one above the 0xE3EE of 2512 OnFuncObject_In.</summary>
    internal const ushort MsgTagFuncObjectOut = 0xE3EF; // 58351

 /// <summary>
 /// Value of the u32 at blob+4 of 2513: always zero on the wire.
 /// </summary>
    private const uint FuncObjectOutReserved = 0;

 /// <summary>
 /// Builds RMI 2513 OnFuncObject_Out, which removes function objects from the receiving client's view. Uses the
 /// compact-BA envelope; it is the function-object counterpart of 2507 OnNPC_Out.
 /// </summary>
 /// <remarks>
 /// <para>Wire layout (compact-BA envelope):</para>
 /// <code>
 /// [compactLen][u16 selfLen] blob+0 selfLen == len(blob)
 /// [u16 msgTag = 0xE3EF] blob+2
 /// [u32 0] blob+4
 /// [u16 count] blob+8
 /// [u32 funcObjectHandle] * count blob+10
 /// </code>
 /// <para>
 /// Function object handles carry the actor type tag 0x14 in their low byte (NPC handles use 0x13). The length is
 /// <c>10 + 4*count</c> bytes. The client reads the count with a bounded vector reader, so an empty
 /// list is rejected and must not be sent.
 /// </para>
 /// <para>
 /// Not yet wired: the server does not track function objects in view, so there is nothing to remove. When an
 /// interest registry exists, send this with <see cref="BroadcastService.BroadcastToMapAsync"/>.
 /// </para>
 /// </remarks>
 /// <param name="funcObjectHandles">Handles of the function objects leaving the view.</param>
    public static byte[] BuildFuncObjectOut2513(IReadOnlyList<uint> funcObjectHandles)
    {
        if (funcObjectHandles.Count == 0)
            throw new ArgumentException(
                "2513 never carries an empty list: count is >= 1 in every measured frame, and an " +
                "empty body would make the client's bounded vector reader abandon the read.",
                nameof(funcObjectHandles));

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagFuncObjectOut);                // blob+2
            w.WriteUInt32(FuncObjectOutReserved);              // blob+4
            w.WriteUInt16((ushort)funcObjectHandles.Count);    // blob+8
            foreach (var h in funcObjectHandles)
                w.WriteUInt32(h);                              // blob+10 + 4*i
        });
        return pw.ToArray();
    }

 /// <summary>Builds RMI 2513 OnFuncObject_Out for a single function object.</summary>
    public static byte[] BuildFuncObjectOut2513(uint funcObjectHandle)
        => BuildFuncObjectOut2513(new[] { funcObjectHandle });
}
