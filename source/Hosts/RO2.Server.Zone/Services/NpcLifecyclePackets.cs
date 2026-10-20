using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for NPC life-cycle notifies: an NPC appearing (2317 OnRegenNPC) and NPCs leaving view
/// (2507 OnNPC_Out). The death notify 2392 is built by <see cref="ActorVitalsPackets.BuildDeadNotify2392"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both opcodes use the compact-BA envelope: the body starts with a compact-length marker, and the u16 at blob+0
/// equals the blob length. The u32 at blob+4 is always zero.
/// </para>
/// <para>
/// The NPC record is 81 bytes followed by a u16-counted vector of 23-byte extensions, so the unit is 81 + 2 bytes
/// when the vector is empty. The shared writer <see cref="NpcSpawnService.WriteNpcRecord"/> emits the record; the two
/// values that differ per opcode are its arguments.
/// </para>
/// </remarks>
public static class NpcLifecyclePackets
{
    // =====================================================================================
    // 2317 OnRegenNPC
    // =====================================================================================

    /// <summary>msgTag at blob+2 of RMI 2317 OnRegenNPC.</summary>
    internal const ushort MsgTagRegenNpc = 0xE29A; // 58010

    /// <summary>The u32 at blob+4 of both 2317 and 2507: always zero.</summary>
    internal const uint InnerHeaderZero = 0;

    /// <summary>
    /// Value of record+33 in a 2317 NPC record: 0x00. The 2506 list sends a different value there
    /// (<see cref="NpcSpawnService.StateStanding"/>).
    /// </summary>
    internal const byte RegenRecordFlagAt33 = 0x00;

    /// <summary>Body length of 2317 in bytes.</summary>
    /// <remarks>
    /// 8-byte inner header, an 81-byte NPC record and a 2-byte empty extension vector, plus the compact-length marker.
    /// </remarks>
    internal const int RegenNpcBodyLen = 93;

    /// <summary>Size in bytes of the fixed part of an NPC record, before its extension vector.</summary>
    internal const int NpcRecordFixedLen = 81;

    /// <summary>
    /// Builds RMI 2317 OnRegenNPC, which makes one NPC present for the receiving client. It carries the same NPC record
    /// that 2506 OnNPC_In carries in bulk, with the NPC's live position.
    /// </summary>
    /// <remarks>
    /// <para>Wire layout:</para>
    /// <code>
    /// [compactLen 01 5b][u16 selfLen = 91] blob+0
    /// [u16 msgTag = 0xE29A] blob+2
    /// [u32 0] blob+4
    /// [81-byte NPC record] blob+8
    /// [u16 extensionCount = 0] blob+89
    /// </code>
    /// <para>Record fields (offsets inside the 81-byte record):</para>
    /// <code>
    /// r+0  u32 actor handle (low byte 0x13 for NPCs)
    /// r+4  u32 asset index
    /// r+8  u8  level
    /// r+9  i32 x * 100, r+13 i32 y * 100, r+17 i32 navmesh cell; r+21 / r+25 / r+29 repeat them (destination = position)
    /// r+33 u8  flag, 0x00 here
    /// r+34 f32 rotation in degrees
    /// r+38 f32 walk speed
    /// r+42 u32 current HP, r+46 u32 max HP
    /// r+50 u16 current SP, r+54 u16 max SP
    /// r+58 u32 actor link (spawner or summoner), 0 here
    /// </code>
    /// <para>
    /// Each appearance uses a new handle.
    /// </para>
    /// <para>
    /// 2317 is sent when a spawn-table NPC respawns. It is not an NPC-death notice.
    /// </para>
    /// </remarks>
    /// <param name="npc">The NPC. Its live position is used.</param>
    /// <param name="walkSpeed">Walk speed from NpcServerInfo for this NPC's asset. Required.</param>
    public static byte[] BuildRegenNpc2317(NpcRuntimeState npc, float walkSpeed)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagRegenNpc);       // blob+2
            w.WriteUInt32(InnerHeaderZero);      // blob+4
            // blob+8: the 81-byte record plus its empty 2-byte extension vector (count 0, so no effects are passed),
            // written by the shared writer. record+33 is RegenRecordFlagAt33 and record+38 is the asset's walk speed.
            NpcSpawnService.WriteNpcRecord(w, npc, flagAt33: RegenRecordFlagAt33, speedAt38: walkSpeed,
                posX: npc.PosX, posY: npc.PosY);
        });
        return pw.ToArray();
    }

    // =====================================================================================
    // 2507 OnNPC_Out
    // =====================================================================================

    /// <summary>msgTag at blob+2 of RMI 2507 OnNPC_Out.</summary>
    internal const ushort MsgTagNpcOut = 0xE3D1; // 58321

    /// <summary>
    /// Builds RMI 2507 OnNPC_Out, which tells the receiving client that the listed NPCs have left its view, so it can
    /// destroy their actors.
    /// </summary>
    /// <remarks>
    /// <para>Wire layout:</para>
    /// <code>
    /// [compactLen][u16 selfLen] blob+0 selfLen == len(blob)
    /// [u16 msgTag = 0xE3D1] blob+2
    /// [u32 0] blob+4
    /// [u16 count] blob+8
    /// [u32 npcHandle] * count blob+10
    /// </code>
    /// <para>
    /// The length is <c>10 + 4*count</c> bytes. The client rejects a body that ends before the count, so the count
    /// must be sent with the handles.
    /// </para>
    /// </remarks>
    /// <param name="npcHandles">Handles of the NPCs leaving the view.</param>
    public static byte[] BuildNpcOut2507(IReadOnlyList<uint> npcHandles)
    {
        if (npcHandles.Count == 0)
            throw new ArgumentException(
                "2507 never carries an empty list: the count is at least 1, " +
                "and an empty body would make the client abandon the vector read.",
                nameof(npcHandles));

        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagNpcOut);                 // blob+2
            w.WriteUInt32(InnerHeaderZero);              // blob+4
            w.WriteUInt16((ushort)npcHandles.Count);     // blob+8: count, which the client tests
            foreach (var h in npcHandles)
                w.WriteUInt32(h);                        // blob+10 + 4*i: NPC handle (low byte 0x13)
        });
        return pw.ToArray();
    }

    /// <summary>Builds RMI 2507 OnNPC_Out for a single NPC.</summary>
    public static byte[] BuildNpcOut2507(uint npcHandle)
        => BuildNpcOut2507(new[] { npcHandle });


    // 2392 OnDeadNotify is not built here: see ActorVitalsPackets.BuildDeadNotify2392.
}
