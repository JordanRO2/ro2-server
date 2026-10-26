using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends NPC/monster spawn data to players when they enter a map.
/// Uses ZoneSpawn.json (30K+ entries from spawn.tbl files).
/// Integrates with NpcStateManager for runtime HP tracking and wire-handle allocation.
/// </summary>
public class NpcSpawnService
{
    private readonly GameDataStore _data;
    private readonly NpcStateManager _npcState;
    private readonly ILogger<NpcSpawnService> _logger;

    /// <summary>NPCInfo.Camp of a friendly NPC.</summary>
    internal const int CampFriendly = 1;

    /// <summary>NPCInfo.Camp of a monster.</summary>
    internal const int CampMonster = 2;

    /// <summary>NPCInfo.Camp 3: the camp that, like <see cref="CampFriendly"/>, maps to camp class 1 (see <see cref="CampByte"/>).</summary>
    internal const int CampThree = 3;

    /// <summary>Value of the reserved u32 at blob+4 of 2318, 2506 and 2319: always 0.</summary>
    private const uint ReservedBlobWord = 0;

    /// <summary>Record +58, the spawn-linked actor (spawner or summoner): none for a spawn-table NPC.</summary>
    private const uint NoLinkedActor = 0;

    /// <summary>Value of the record's reserved u16 words (+52 and +56) and of an empty extension count (+81).</summary>
    private const ushort ReservedWord = 0;

    // Zero runs inside the 81-byte record, named by the first offset they cover.
    private const int ReservedBytesAt62 = 4;   // +62..65
    private const int ReservedBytesAt67 = 5;   // +67..71 (+68 is the PK/WoE team flag, 0 here)
    private const int ReservedBytesAt73 = 8;   // +73..80 (+73..75 are the high bytes of the camp dword at +72)

    /// <summary>Record +66 values: the corpse-gatherable flag is a plain 0 / 1 byte.</summary>
    private const byte FlagSet = 1;
    private const byte FlagClear = 0;

    /// <summary>NPC level is written as one byte at record+8.</summary>
    private const int MaxWireLevel = byte.MaxValue;

    /// <summary>NPC coordinates go on the wire as hundredths of a world unit.</summary>
    private const float WireCoordScale = 100.0f;

    /// <summary>Degrees in a full turn; the record's rotation is wrapped into [0, this).</summary>
    private const float FullTurnDegrees = 360f;

    /// <summary>Camp class (record +72 low byte) of camps 1 and 3, and of camp 0.</summary>
    private const byte CampClassLow = 1;

    /// <summary>Camp class (record +72 low byte) of camps 2, 4 and 5.</summary>
    private const byte CampClassHigh = 3;

    public NpcSpawnService(GameDataStore data, NpcStateManager npcState, ILogger<NpcSpawnService> logger)
    {
        _data = data;
        _npcState = npcState;
        _logger = logger;
    }

    /// <summary>
    /// Send all NPCs/monsters on a map to the player as batched RMI 2506 (OnNPC_In) frames.
    /// Handles come from NpcStateManager.AllocateHandle and carry the 0x13 NPC actor tag.
    /// Only sends for valid maps (non-zero mapId that exists in Map_List).
    /// Initializes NpcStateManager for the map if not already done.
    /// </summary>
    public async Task SendMapNpcsAsync(ProudSession session, uint mapId)
    {
        // Only send spawns for valid, non-zero maps
        if (mapId == 0)
        {
            _logger.LogDebug("Session {Id}: Skipping NPC spawns for mapId 0", session.SessionId);
            return;
        }

        var mapData = _data.GetMap(mapId);
        if (mapData == null)
        {
            _logger.LogDebug("Session {Id}: Map {MapId} not found in Map_List, skipping NPC spawns",
                session.SessionId, mapId);
            return;
        }

        // Initialize NPC states for this map (idempotent)
        _npcState.InitializeMap(mapId);

        var spawns = _data.GetMapSpawns(mapId);
        if (spawns.Length == 0)
        {
            _logger.LogDebug("Session {Id}: No spawns for map {MapId}", session.SessionId, mapId);
            return;
        }

        // ONE batched 2506 per map load, not one frame per NPC. Layout: BuildOnNpcIn2506.
        var alive = new List<NpcRuntimeState>();
        var monsterCount = 0;
        var noTemplate = 0;
        foreach (var spawn in spawns)
        {
            var npcRuntime = _npcState.GetNpc(_npcState.AllocateHandle(mapId, spawn.Index));
            if (npcRuntime == null) continue;
            var announcement = SpawnVisibilityUseCase.ClassifyAnnouncement(npcRuntime.IsDead, HasClientTemplate(spawn.AssetIndex));
            if (announcement == NpcAnnouncement.Skip) continue;
            if (announcement == NpcAnnouncement.NoTemplate) { noTemplate++; continue; }
            if (npcRuntime.Camp != CampFriendly) monsterCount++;   // Camp 1 = friendly NPC, 2 = monster
            alive.Add(npcRuntime);
        }

        var npcCount = alive.Count;
        if (npcCount == 0)
        {
            _logger.LogDebug("Session {Id}: No live NPCs for map {MapId}", session.SessionId, mapId);
            return;
        }

        // Split into frames of at most MaxRecordsPerFrame records.
        foreach (var chunk in Chunk(alive, MaxRecordsPerFrame))
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnNPC_In, BuildOnNpcIn2506(chunk, WalkSpeedOf));

        _logger.LogDebug("Session {Id}: Sent {Total} NPC spawns for map {MapId} ({Map}) - {Monsters} monsters, {Friendly} friendly, {Skipped} skipped (no client template)",
            session.SessionId, npcCount, mapId, mapData.Map_Name, monsterCount, npcCount - monsterCount, noTemplate);
    }

    /// <summary>
    /// True when the client can actually instantiate an actor for this spawn's asset.
    /// </summary>
    /// <remarks>
    /// THIS GATE IS NOT COSMETIC: it is the predicate the client applies, and announcing a spawn
    /// that fails it produces an infinite ReqActorViewData (1306) retry loop.
    /// <para>
    /// A 2506 OnNPC_In record whose asset has no NPCInfo row cannot be instantiated: the client discards the
    /// record and the actor never exists on its side. When the server later broadcasts 2318 OnMoveNPC for that
    /// handle, the client's 2318 handler looks the handle up, misses, and sends 1306, forever, since nothing can
    /// make the lookup succeed.
    /// </para>
    /// <para>
    /// A big map announces hundreds of NPCs, some of them without an NPCInfo row. Those that also wander
    /// (Spawn.HoverRadius &gt; 0) get a 2318, and they are exactly the handles the client requests via 1306.
    /// NPCs that have a template are never requested, and neither are template-less NPCs that never wander.
    /// </para>
    /// <para>
    /// This gate stops the server ADVERTISING a spawn the client cannot build; NpcMovementService applies the
    /// same predicate before it MOVES one, because a 2318 for an unregistered handle is the actual trigger.
    /// </para>
    /// <para>
    /// A share of ZoneSpawn's distinct assets have no NPCInfo row. NPCInfo and NpcServerInfo select the same
    /// ids, so either table works as the oracle; NPCInfo is used because it is the client-facing NPC table.
    /// </para>
    /// </remarks>
    private bool HasClientTemplate(uint assetIndex) => _data.Npcs.ContainsKey(assetIndex);

    /// <summary>
    /// Send a single NPC respawn to all players on a map: 2317 OnRegenNPC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to send a one-record 2506 batch. 2317 is the message for a single NPC becoming present — the
    /// singular counterpart of a 2506 list, carrying the same 81-byte record — and it is now built from the
    /// shared record writer, so the two cannot drift.
    /// </para>
    /// <para>
    /// No 2507 goes before it: each appearance gets a fresh handle, so the dead actor leaves only through its
    /// own corpse 2507. <paramref name="npc"/> arrives here already re-keyed by NpcStateManager.Respawn, so this
    /// 2317 names a handle no client holds, and its handler creates the actor. The corpse keeps the old handle
    /// until its own 2507 (LootService.DespawnCorpseAsync), which may come before or after this one. Clients
    /// that arrived while the NPC was dead never had it (SendMapNpcsAsync skips dead NPCs, and the slot's handle
    /// is the fresh one).
    /// </para>
    /// <para>
    /// BROADCAST TO MAP with NO exclusion: 2317 creates the actor in every receiver's
    /// registry, and there is no "source session" for an NPC event to exclude.
    /// <c>BroadcastToNearbyAsync</c> would be wrong here for that reason alone.
    /// </para>
    /// <para>
    /// 2819 NfyNpcEffect accompanies 2317, but its trigger is an open question, so it is deliberately NOT
    /// emitted here. See <c>EffectPackets.BuildNpcEffect2819</c>.
    /// </para>
    /// </remarks>
    public async Task SendNpcRespawnAsync(BroadcastService broadcast, NpcRuntimeState npc)
    {
        var body = NpcLifecyclePackets.BuildRegenNpc2317(npc, WalkSpeedOf(npc));
        await broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.OnRegenNPC,
            writer => writer.WriteBytes(body));
    }

    /// <summary>msgTag carried at blob+2 by RMI 2506 OnNPC_In.</summary>
    /// <remarks>Constant for every 2506 frame.</remarks>
    internal const ushort MsgTagOnNpcIn = 0xE3D0; // 58320

    /// <summary>msgTag of 2318 OnMoveNPC, constant for every 2318 frame.</summary>
    internal const ushort MsgTagOnMoveNpc = 0xE29B; // 58011

    /// <summary>msgTag carried at blob+2 by RMI 2319 OnNPCViewData.</summary>
    /// <remarks>
    /// 58008 for every 2319, with blob+4 zero. The tag sits in a contiguous family with the request and the
    /// PC-side reply, which is what the msgTag space does for request/response pairs elsewhere in this
    /// protocol: 1306 ReqActorViewData = 58006, 2315 OnPCViewData = 58007, 2319 OnNPCViewData = 58008.
    /// </remarks>
    internal const ushort MsgTagOnNpcViewData = 0xE298; // 58008

    /// <summary>
    /// Farthest a wander step may travel, in world units. Short enough that the spawn's navmesh cell stays valid — see
    /// <c>BuildOnMoveNpc2318</c>.
    /// </summary>
    internal const float MaxWanderStep = 3.0f;

    /// <summary>Bytes per NPC record inside a 2506 frame.</summary>
    internal const int NpcRecordLen = 83;

    /// <summary>Records per frame, chosen to keep each frame to a size the client handles.</summary>
    /// <remarks>
    /// Large maps are split because a single 2506 frame must stay small: 100 records is 8,310 bytes. The count
    /// field is a u16 and would allow far more, but the client is only known to accept small frames. ZoneSpawn.json's largest map has 4,189 spawns, so the
    /// chunking is required.
    /// </remarks>
    internal const int MaxRecordsPerFrame = 100;

    private static IEnumerable<List<NpcRuntimeState>> Chunk(List<NpcRuntimeState> src, int size)
    {
        for (var i = 0; i < src.Count; i += size)
            yield return src.GetRange(i, Math.Min(size, src.Count - i));
    }

    /// <summary>Encode a world coordinate the way NPC records do: truncated hundredths.</summary>
    /// <remarks>
    /// TRUNCATION, not rounding. Note this is NOT how player coordinates are sent:
    /// RMI 2311 carries the player position as three f32. Two coordinate encodings coexist and
    /// mixing them up silently misplaces every actor.
    /// </remarks>
    internal static int ToWireCoord(float units) => (int)(units * WireCoordScale);

    /// <summary>
    /// Clamps a navmesh cell to the range the client accepts, so an out-of-range value
    /// is structurally impossible on the wire.
    /// </summary>
    /// <remarks>
    /// This is a guard, not a formatter. The client hands this straight to PathEngine, which uses it
    /// as an unchecked array subscript — an out-of-range cell is a remote access violation in the
    /// client, which is exactly how the first live test of 2506 ended. A missing cell becomes 0
    /// rather than -1 on purpose: 0 is a valid cell, whereas -1 makes the client short-circuit and report
    /// position (0,0,0) for that actor.
    /// </remarks>
    internal static int ToWireNavCell(int? cell)
        => cell is int c && c >= 0 && c <= GameDataStore.MaxKnownNavCell ? c : 0;

    /// <summary>Body length of 2318 OnMoveNPC: 42 bytes.</summary>
    internal const int MoveNpcBodyLen = 42;

    /// <summary>
    /// Builds RMI 2318 OnMoveNPC: one NPC moving from an origin to a destination at a given speed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout:
    /// <code>
    ///   [u8 1][u8 40]        compact length, width 1, value 40
    ///   [u16 40]             selfLen
    ///   [u16 0xE29B]         msgTag
    ///   [u32 0]
    ///   [u32 handle]         low byte 0x13, the NPC actor tag
    ///   [i32 ox][i32 oy]     origin, trunc(units*100)
    ///   [i32 oCell]          origin navmesh cell
    ///   [i32 dx][i32 dy]     destination, same encoding
    ///   [i32 dCell]          destination navmesh cell
    ///   [f32 speed]
    /// </code>
    /// Total 42 bytes.
    /// </para>
    /// <para>
    /// THE THIRD COMPONENT IS A NAVMESH CELL, exactly as in 2506 — it is not a Z coordinate. That
    /// distinction is not cosmetic: writing trunc(PosZ*100) into 2506's equivalent slot crashed the
    /// real client inside PathEngine (see <see cref="BuildOnNpcIn2506"/>). Both of this message's cell slots live in
    /// the same value space as 2506's.
    /// </para>
    /// <para>
    /// WHY A WANDER MAY REUSE THE SPAWN'S CELL. We can only look a cell up for a spawn point, so a
    /// destination elsewhere would have an unknown cell. For a short move the destination cell almost always
    /// equals the origin cell, so capping a wander step at <see cref="MaxWanderStep"/> and reusing the spawn
    /// cell is sound, and it avoids needing a point-in-face solver over the map's navmesh.
    /// </para>
    /// <para>
    /// Destination cell -1 is valid here but is NOT valid for an origin nor for a 2506 spawn record. We never
    /// emit it.
    /// </para>
    /// </remarks>
    internal static byte[] BuildOnMoveNpc2318(uint handle, float ox, float oy, float dx, float dy,
        int? cell, float speed)
        => BuildOnMoveNpc2318(handle, ox, oy, cell, dx, dy, cell, speed);

    /// <summary>
    /// <see cref="BuildOnMoveNpc2318(uint,float,float,float,float,int?,float)"/> with the origin and destination
    /// navmesh cells given SEPARATELY. A chase step crosses cells as the monster leaves its spawn, and the
    /// position-&gt;cell solver (GameDataStore.GetNavCellAt) resolves each one; the single-cell overload above
    /// reuses one cell for both, which a wander keeps doing because its step stays in the spawn's cell.
    /// </summary>
    internal static byte[] BuildOnMoveNpc2318(uint handle, float ox, float oy, int? originCell,
        float dx, float dy, int? destCell, float speed)
    {
        using var pw = new RO2.Network.Packets.PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagOnMoveNpc);
            w.WriteUInt32(ReservedBlobWord);
            w.WriteUInt32(handle);
            w.WriteInt32(ToWireCoord(ox));
            w.WriteInt32(ToWireCoord(oy));
            w.WriteInt32(ToWireNavCell(originCell));
            w.WriteInt32(ToWireCoord(dx));
            w.WriteInt32(ToWireCoord(dy));
            w.WriteInt32(ToWireNavCell(destCell));
            w.WriteSingle(speed);
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Builds one batched RMI 2506 OnNPC_In body carrying <paramref name="npcs"/> records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Envelope: compactLen == selfLen == len(blob), msgTag 0xE3D0, blob+4 zero, and the u16 at blob+8 is a
    /// record count. The payload is count * 83 bytes.
    /// </para>
    /// <para>
    /// The frame may carry an optional 23-byte block after the records; none is emitted.
    /// </para>
    /// <para>
    /// +33 and +38 are not constants: +33 takes several small values (default 1) and +38 is a speed-like float
    /// (default 0.0). The defaults are emitted.
    /// </para>
    /// <code>
    ///   +0  u32 actor handle      low byte 0x13 (NPC tag)
    ///   +4  u32 assetIndex        ZoneSpawn AssetIndex
    ///   +8  u8  NPC level          == NpcServerInfo.Level
    ///   +9  i32 trunc(x * 100)    see the position note below
    ///   +13 i32 trunc(y * 100)    see the position note below
    ///   +17 i32 third component   see the caveat below
    ///   +21 i32 trunc(x * 100)    byte-identical copy of +9
    ///   +25 i32 trunc(y * 100)    byte-identical copy of +13
    ///   +29 i32 third component   copy of +17
    ///   +33 u8  see note          NOT a constant: 0,1,2,3,5,6 (default 1)
    ///   +34 f32 rotation          ZoneSpawn Rot
    ///   +38 f32 see note          NOT a constant: 0.0, 1.0, 1.5, 1.7, 4.0, 10.0 (default 0.0)
    ///   +42 u32 hp,  +46 u32 maxHp
    ///   +50 u32 sp,  +54 u32 maxSp
    ///   +58 u32 0
    ///   +62..71 zero, +72 u8 0x01, +73..82 zero    (superseded: +66 is the
    ///                             corpse-gatherable flag and +72 the camp class, see WriteNpcRecord)
    /// </code>
    /// <para>
    /// The duplicated position block is the origin/destination pair this actor family uses (the same
    /// shape as 2318 OnMoveNPC); for a stationary spawn the two are identical, which is what
    /// the frame carries.
    /// </para>
    /// <para>
    /// POSITION NOTE. The record carries LIVE positions, not spawn-table ones: an NPC that has not moved sits at
    /// its spawn point, one that has wandered does not. Rotation at +34 is the ZoneSpawn Rot, and positions are
    /// truncated, not rounded.
    /// </para>
    /// <para>
    /// SO THIS BUILDER WRITES <see cref="NpcRuntimeState.PosX"/>/<see cref="NpcRuntimeState.PosY"/>,
    /// the live position, not <c>Spawn.PosX/PosY</c>. The two differ only once an NPC has wandered,
    /// and a 2506 for a wandered NPC is not rare: every later map load (another player, or the same
    /// one returning) re-announces the map's NPCs from the same NpcStateManager state. Writing the spawn point
    /// would place a wandered NPC back at its spawn for the newly arrived client while 2317, 2318 and 2319 all
    /// carry the live position, and the next 2318's origin (also the live position) would then jump it.
    /// </para>
    /// <para>
    /// +17 IS NOT A HEIGHT — it is a PathEngine navmesh CELL INDEX, and getting this wrong crashes the client.
    /// The record carries no Z at all: the client COMPUTES it from +9 and +13 (divided by 100 for X/Y) and the
    /// navmesh height at that position within this cell. PathEngine indexes a per-face array with the cell and
    /// does NOT bounds-check, so an out-of-range cell reads unmapped memory (ACCESS_VIOLATION).
    /// </para>
    /// <para>
    /// An earlier version wrote trunc(PosZ*100) here, on the wrong premise that the field was elevation. The
    /// cell values the client accepts span roughly 0..2,382; the elevation values were far outside that range,
    /// in every record.
    /// </para>
    /// <para>
    /// The cell comes from GameDataStore.NpcNavCells, built per spawn from the map's navmesh
    /// (BACKGROUND/ZONE/&lt;map&gt;/navidata/ground.tok), where the cell is just the index of the polygon
    /// containing the spawn. See GameDataStore.LoadNavCellsAsync. A miss means a map with no navmesh at all. On
    /// a miss we emit 0, which is in range for any non-empty mesh; the cost is a wrong ground height for that
    /// NPC, not a fault. Do NOT emit -1 as a production value: -1 makes the client return (0,0,0) for the whole
    /// position, stacking every NPC at the world origin.
    /// </para>
    /// </remarks>
    /// <param name="walkSpeed">
    /// Per-NPC <c>NpcServerInfo.Walk_Speed</c> lookup for record+38. Optional only so the byte-layout
    /// tests can build a record without a <c>GameDataStore</c>; production callers must pass
    /// <see cref="WalkSpeedOf"/>, because 0.0 is correct only for rows with no walk speed.
    /// </param>
    internal static byte[] BuildOnNpcIn2506(IReadOnlyList<NpcRuntimeState> npcs,
        Func<NpcRuntimeState, float>? walkSpeed = null)
    {
        using var pw = new RO2.Network.Packets.PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagOnNpcIn);       // blob+2
            w.WriteUInt32(ReservedBlobWord);    // blob+4 zero
            w.WriteUInt16((ushort)npcs.Count);  // blob+8 record count
            foreach (var npc in npcs)   // live position, not the spawn point: see the remarks
                WriteNpcRecord(w, npc, flagAt33: StateStanding, speedAt38: walkSpeed?.Invoke(npc) ?? 0f,
                    posX: npc.PosX, posY: npc.PosY);
        });
        return pw.ToArray();
    }

    /// <summary>
    /// Builds RMI 2319 OnNPCViewData: the reply to a client's 1306 ReqActorViewData for an NPC.
    /// </summary>
    /// <remarks>
    /// WHAT 2319 IS: the reply to a client's 1306 ReqActorViewData for an NPC. It is never pushed unprompted;
    /// every 2319 answers a request. 1306 is only sent for NPC handles (tag 0x13), so 2319 is its only answer.
    /// <para>
    /// A 1306 for an NPC handle is answered by a 2319 carrying the requested handle verbatim.
    /// </para>
    /// <para>
    /// Wire shape: msgTag 58008 at blob+2; blob+4 zero; the blob is 91 bytes = the 8-byte
    /// inner header + one 83-byte NPC record, identical in layout to a 2506 record. Longer forms add the same
    /// optional 23-byte extension as 2506. This server emits the 91-byte shape, exactly as <see cref="BuildOnNpcIn2506"/> emits the extension-free
    /// shape.
    /// </para>
    /// <para>
    /// 2315 OnPCViewData is NOT the reply for an NPC: its blob+8 actor handle carries the 0x11 PC tag. 2317
    /// OnRegenNPC carries the 0x13 NPC tag and the same 83-byte record, but it is a respawn announcement, not a
    /// reply.
    /// </para>
    /// <para>
    /// THE RECORD DESCRIBES THE NPC'S CURRENT WALK, not a standing actor at the walk's end. The client
    /// asks 1306 about an actor it was just told to move (the 2318 is the trigger, see
    /// ActorViewDataHandler), so what it needs back is where that walk has the NPC NOW. Compared with the last
    /// 2318 sent for the same handle:
    /// </para>
    /// <list type="bullet">
    /// <item>A MID-WALK record (+9 != +21) carries the 2318's destination at +21/+25, the current point +9/+13 on
    /// the 2318's segment (the interpolated point, equal to the 2318's origin only while no step has been taken),
    /// both cells +17/+29 equal to the 2318's, and the 2318's speed float at +38. +33 is 2 (walking) or 5
    /// (running at the asset's Run_Speed).</item>
    /// <item>A STANDING record has +33 = 1 and stands at the 2318's destination with its speed at +38.</item>
    /// </list>
    /// <para>
    /// The client's handling of the record is why the current point matters: it places the actor at +9, stores
    /// +33 and +38 as the actor's move mode and speed, and pushes +21/+25/+29 onto the actor's path — so a
    /// mid-walk record makes it continue the walk from where the NPC is, while a standing-at-destination record
    /// would make the NPC pop to the end of it.
    /// </para>
    /// <para>
    /// SO, from <see cref="NpcRuntimeState.LastMove"/> (the walk NpcMovementService last sent):
    /// while it is in progress, +9/+13 = <see cref="NpcMove.Position"/> at <paramref name="nowUtc"/>,
    /// +21/+25 = its destination, +33 = 2 and +38 = its speed. +33 = 2 and never 5: this server's
    /// only NPC 2318 is NpcMovementService's wander, which is a walk (NpcCombatService sends no chase
    /// 2318). Both cells are <see cref="NpcRuntimeState.NavCell"/>, which is what that 2318 carried in
    /// both of its cell slots. Once the walk is covered the record stands at the destination
    /// (<see cref="NpcRuntimeState.PosX"/>/<see cref="NpcRuntimeState.PosY"/>, where NpcMovementService
    /// already put the NPC) with +33 = 1 and STILL the walk's speed at +38: the last walk's speed beats the table
    /// column. With no walk sent
    /// since the NPC (re)spawned, or once it is dead, the record is today's standing one: +33 = 1 at
    /// the live position and <paramref name="walkSpeed"/> at +38, as 2506 and 2317 carry it.
    /// </para>
    /// <para>
    /// VITALS: the record's HP and SP are the NPC's own (<see cref="NpcRuntimeState.MaxHp"/>,
    /// <see cref="NpcRuntimeState.WireMaxSp"/>), which NpcStateManager takes from the per-asset vitals table
    /// (<see cref="NpcRetailVitals"/>) where the table has the asset; see <see cref="WriteNpcRecord"/>.
    /// </para>
    /// <para>
    /// ACTIVE EFFECTS. The longer forms carry the NPC's active effects after the 81-byte record: record+81 is
    /// <c>[u16 n]</c> followed by n 23-byte records in exactly the PC record's VEC23 format
    /// (<see cref="PlayerSpawnService.WriteEffectRecords"/>): effect id, value, stack, remaining time in ms, source
    /// skill and the caster's handle. The 93-byte form is the count-0 one; two effects need the 2-byte compact
    /// length form (<c>02 8900</c>), the width <c>WriteCompactBABody</c> also picks for a blob over 127 bytes. The
    /// list is what EffectService holds on the NPC (ActorViewDataHandler passes it).
    /// </para>
    /// </remarks>
    /// <param name="npc">The NPC the 1306 named.</param>
    /// <param name="walkSpeed">
    /// record+38 of a standing NPC that has not walked since it (re)spawned: the asset's
    /// <c>NpcServerInfo.Walk_Speed</c>. See <see cref="BuildOnNpcIn2506"/>.
    /// </param>
    /// <param name="nowUtc">
    /// The moment the reply describes; <see cref="DateTime.UtcNow"/> when omitted. ActorViewDataHandler
    /// passes the time the 1306 is answered, the tests a fixed instant.
    /// </param>
    /// <param name="effects">
    /// The NPC's active effects (EffectService.NpcEffectsSnapshot), written as the record's extension list; null
    /// or empty writes count 0, the 93-byte form. A dead NPC's are never written.
    /// </param>
    internal static byte[] BuildOnNpcViewData2319(NpcRuntimeState npc, float walkSpeed = 0f, DateTime? nowUtc = null,
        IReadOnlyList<ActiveEffect>? effects = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        // A dead NPC is not walking (NpcMovementService moves only live ones); its record stands.
        var walk = npc.IsDead ? null : npc.LastMove;

        byte state = StateStanding;
        float speed = walkSpeed;
        float posX = npc.PosX, posY = npc.PosY;
        float destX = npc.PosX, destY = npc.PosY;
        if (walk is not null)
        {
            speed = walk.Speed;                 // the 2318's speed, walking or arrived (see remarks)
            if (walk.InProgress(now))
            {
                state = StateWalking;
                (posX, posY) = walk.Position(now);
                destX = walk.ToX;
                destY = walk.ToY;
            }
        }

        using var pw = new RO2.Network.Packets.PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagOnNpcViewData); // blob+2
            w.WriteUInt32(ReservedBlobWord);    // blob+4, zero
            // blob+8, the record 2506 and 2317 also carry: current point at +9, walk end at +21
            WriteNpcRecord(w, npc, flagAt33: state, speedAt38: speed,
                posX: posX, posY: posY, destX: destX, destY: destY,
                effects: npc.IsDead ? null : effects, nowUtc: now);
        });
        return pw.ToArray();
    }

    /// <summary>record+33 of a standing NPC: 1 (also 2506's mode).</summary>
    internal const byte StateStanding = 0x01;

    /// <summary>record+33 of an NPC on a walk: 2 (walking at Walk_Speed); 5 would be running at Run_Speed, which
    /// this server never does.</summary>
    internal const byte StateWalking = 0x02;

    /// <summary>
    /// Normalises a spawn-table rotation into the range the client expects.
    /// </summary>
    /// <remarks>
    /// ZoneSpawn.json's <c>Rot</c> column spans −140…2400 while record+34 is expected within about −180…360. An
    /// out-of-range value is dangerous for the client, as for the cell at record+17. The field is an angle in
    /// DEGREES (the client divides it by 180/π), so wrapping it into [0,360) is value-preserving rather than a
    /// clamp that loses information.
    /// </remarks>
    internal static float ToWireRotationDegrees(float degrees)
    {
        if (!float.IsFinite(degrees))
            return 0f;
        var wrapped = degrees % FullTurnDegrees;
        if (wrapped < 0f)
            wrapped += FullTurnDegrees;
        return wrapped;
    }

    /// <summary>
    /// Writes one 83-byte NPC record — the 81-byte record plus its empty extension vector — which
    /// is the unit shared by 2506 OnNPC_In, 2317 OnRegenNPC and 2319 OnNPCViewData. Field
    /// provenance is documented on <see cref="BuildOnNpcIn2506"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE SINGLE RECORD WRITER. <c>NpcLifecyclePackets</c> briefly carried a second copy for 2317;
    /// it was folded back in here at integration, because two writers for one 81-byte struct is
    /// exactly how the +33/+38 constants drifted in the first place. The two per-opcode differences
    /// are parameters rather than forks:
    /// </para>
    /// <list type="bullet">
    /// <item><b><paramref name="flagAt33"/></b> — 0x01 for 2506/2319 (its overall mode), but 0x00
    /// for 2317.</item>
    /// <item><b><paramref name="speedAt38"/></b> — this is the NPC's WALK SPEED, not a hostility
    /// flag. It equals <c>NpcServerInfo.Walk_Speed</c> for the row keyed by the asset at record+4 (compared as
    /// float32). The old <c>hostile ? 1f : 0f</c> was right only for the rows whose walk speed happens to be
    /// 1.0.</item>
    /// </list>
    /// <para>
    /// HP at +42/+46 and SP at +50/+54 are the NPC's own: <see cref="NpcRuntimeState.Hp"/> /
    /// <see cref="NpcRuntimeState.MaxHp"/> and <see cref="NpcRuntimeState.WireCurSp"/> /
    /// <see cref="NpcRuntimeState.WireMaxSp"/>. NpcStateManager fills them from the per-asset vitals table
    /// (<see cref="NpcRetailVitals"/>) because the other sources are wrong: NpcServerInfo.HP is synthetic, and SP
    /// used to mirror HP. SP is independent of HP, one value per (asset, level), with cur == max. An asset the
    /// table does not cover keeps NpcStateManager's stated fallback, and a state whose SP was never resolved
    /// still mirrors HP (<see cref="NpcRuntimeState.WireMaxSp"/>). The SP values are CLAMPED TO 16 BITS: the
    /// client reads them narrow, and record+52/53 is constant zero; an unclamped value would put a nonzero byte
    /// at an offset that is always zero.
    /// </para>
    /// <para>
    /// EXTENSION LIST (record+81): <c>[u16 n][n x 23]</c>, the NPC's active effects in the PC record's VEC23
    /// format (<see cref="PlayerSpawnService.WriteEffectRecords"/>, the one writer of that record). Only 2319
    /// carries effects (<paramref name="effects"/>); 2317 is always the count-0 form,
    /// and the optional 23-byte extension of 2506 is not emitted, so both pass none.
    /// </para>
    /// <para>
    /// Offsets written as zero that are NOT always zero: +68, +73 and +76 are LIVE fields the client reads
    /// (they feed actor state, as does the dword at +72). Zero is the usual value at each, so emitting zero
    /// is safe.
    /// </para>
    /// <para>
    /// record+66 is the CORPSE-GATHERABLE flag, <c>NPCInfo.AfterDeath_FuncObject_ID != 0</c>
    /// (<see cref="NpcRuntimeState.CorpseGatherable"/>).
    /// </para>
    /// <para>
    /// record+68 / the dword at +72 come in two variants. With +68 == 0, +72 is the camp class
    /// (<see cref="CampByte"/>) with zero high bytes. With +68 == 1 it is the PK/WoE team id
    /// (1000..1007, 1069, 1070), carried by camp-1 NPCs on PK/WoE maps. That variant needs a PK/WoE team system
    /// this server lacks, so +68 stays 0 (BLOCKED on that system) and those records keep the default form.
    /// +58 = 0 is right for a spawn-table NPC (see <see cref="BuildRegenNpc2317"/>).
    /// </para>
    /// </remarks>
    /// <param name="flagAt33">record+33. 0x01 for 2506 and a standing 2319, 0x02 for a 2319 caught
    /// mid-walk, 0x00 for 2317.</param>
    /// <param name="speedAt38">record+38: the asset's <c>NpcServerInfo.Walk_Speed</c>, or for a 2319 of
    /// an NPC that has walked, that walk's 2318 speed.</param>
    /// <param name="posX">World X to encode at record+9, the actor's current point.</param>
    /// <param name="posY">World Y to encode at record+13.</param>
    /// <param name="destX">World X of the walk's end at record+21; <paramref name="posX"/> when omitted,
    /// which is the standing record 2506 and 2317 always send.</param>
    /// <param name="destY">World Y at record+25; <paramref name="posY"/> when omitted.</param>
    /// <param name="effects">The NPC's active effects for the extension list (2319 only); null writes count 0.</param>
    /// <param name="nowUtc">The moment the effects' remaining time is counted from; only read with
    /// <paramref name="effects"/>.</param>
    internal static void WriteNpcRecord(RO2.Network.Packets.PacketWriter w, NpcRuntimeState npc,
        byte flagAt33, float speedAt38, float posX, float posY, float? destX = null, float? destY = null,
        IReadOnlyList<ActiveEffect>? effects = null, DateTime? nowUtc = null)
    {
        var x = ToWireCoord(posX);
        var y = ToWireCoord(posY);
        var toX = ToWireCoord(destX ?? posX);
        var toY = ToWireCoord(destY ?? posY);
        var cell = ToWireNavCell(npc.NavCell);  // NOT a height -- see BuildOnNpcIn2506

        // The client reads SP narrow; see the remarks. Clamping keeps record+52/53 zero.
        var curSp = npc.WireCurSp;
        var maxSp = npc.WireMaxSp;

        w.WriteUInt32(npc.EntityOid);   // +0  handle (carries the 0x13 tag)
        w.WriteUInt32(npc.Spawn.AssetIndex); // +4
        w.WriteByte((byte)Math.Clamp(npc.Level, 0, MaxWireLevel)); // +8 NPC level
        w.WriteInt32(x);                // +9
        w.WriteInt32(y);                // +13
        w.WriteInt32(cell);             // +17 navmesh cell, NOT a height
        w.WriteInt32(toX);              // +21 walk end: == +9 standing, the 2318 destination mid-walk
        w.WriteInt32(toY);              // +25
        w.WriteInt32(cell);             // +29 the 2318 carries the same cell in both slots
        w.WriteByte(flagAt33);          // +33 per-opcode; see the remarks
        w.WriteSingle(ToWireRotationDegrees(npc.Spawn.Rot)); // +34 degrees, normalised
        w.WriteSingle(speedAt38);       // +38 NpcServerInfo.Walk_Speed
        w.WriteUInt32((uint)Math.Max(0, npc.Hp));    // +42
        w.WriteUInt32((uint)Math.Max(0, npc.MaxHp)); // +46
        w.WriteUInt16(curSp);           // +50 cur sp == max sp (see remarks)
        w.WriteUInt16(ReservedWord);     // +52 zero
        w.WriteUInt16(maxSp);           // +54 max sp, per (asset, level) from NpcRetailVitals
        // +56: zero except for one special asset (max HP 1), which carries 0xFFFF here. The SP is read as a 16-bit value, so
        // only that asset's record differs here.
        w.WriteUInt16(ReservedWord);
        w.WriteUInt32(NoLinkedActor);   // +58 spawn-linked actor (spawner/summoner); none for a spawn-table NPC, see BuildRegenNpc2317
        w.WriteZeros(ReservedBytesAt62);  // +62..65 zero
        w.WriteByte(npc.CorpseGatherable ? FlagSet : FlagClear); // +66 NPCInfo.AfterDeath_FuncObject_ID != 0 (see remarks)
        w.WriteZeros(ReservedBytesAt67);  // +67..71; +68 is the PK/WoE team flag, 0 here (no team system, see remarks)
        w.WriteByte(CampByte(npc));     // +72 u32 camp class: low byte here, the three high bytes in the next zeros
        w.WriteZeros(ReservedBytesAt73);  // +73..80 (+73..75 the camp dword's high bytes; +76 zero)
        if (effects is { Count: > 0 })  // +81 [u16 n][n x 23]: the NPC's active effects (2319 only)
            PlayerSpawnService.WriteEffectRecords(w, effects, nowUtc ?? DateTime.UtcNow);
        else
            w.WriteUInt16(ReservedWord);
    }

    /// <summary>
    /// record+72 (a u32 whose high bytes are zero) when +68 == 0: the camp class. Camp 2, 4 and 5 → 3;
    /// camp 1 and 3 → 1 (and camp 0 keeps 1).
    /// </summary>
    /// <remarks>
    /// Joined on the asset's <c>NPCInfo.Camp</c>: camp 1 → 1; camp 2 → 3; camp 3 → 1; camp 4 → 3; camp 5 → 3.
    /// The old <c>Camp &gt;= 2 → 3</c> was wrong for camp 3, and "only camp 2 → 3" would be wrong for camps 4
    /// and 5. Camp 0 keeps 1. Records with +68 != 0 are the PK/WoE
    /// team variant (see <see cref="WriteNpcRecord"/>), which this server does not produce. The field is reproduced as-is; its meaning is not named here.
    /// </remarks>
    internal static byte CampByte(NpcRuntimeState npc) => npc.Camp is CampMonster or > CampThree ? CampClassHigh : CampClassLow;

    /// <summary>
    /// <c>NpcServerInfo.Walk_Speed</c> for this NPC's asset, or 0 when the row has none.
    /// </summary>
    /// <remarks>
    /// 0.0 is a legitimate table value — many NpcServerInfo rows carry it —
    /// so falling back to it on a lookup miss stays inside the oracle's own range rather than
    /// inventing a speed.
    /// </remarks>
    internal float WalkSpeedOf(NpcRuntimeState npc) => _data.GetNpcStats(npc.Spawn.AssetIndex)?.Walk_Speed ?? 0f;
}
