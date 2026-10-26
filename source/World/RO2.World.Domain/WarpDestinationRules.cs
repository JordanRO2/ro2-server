using RO2.GameData;

namespace RO2.Worlds.Domain;

/// <summary>Target position of a warp.</summary>
public readonly record struct WarpDestination(uint MapId, float X, float Y, float Z);

/// <summary>
/// Pure destination lookups of the warps: the item warps (Butterfly Wing, Dungeon Wing, town warp scrolls), the NPC gate
/// functions, the multi-destination teleporter and the save point. Each takes its table data through
/// <see cref="IWorldCatalog"/> and decides only where; the departure and the move itself stay in the host.
/// </summary>
public static class WarpDestinationRules
{
 /// <summary>Butterfly Wing item skill: returns to the bind point (<see cref="ButterflyWingDestination"/>).</summary>
    public const uint ButterflyWing = 9100101;
 /// <summary>Dungeon Wing item skill: leaves the current map for its Map_List return point (<see cref="DungeonWingDestination"/>).</summary>
    public const uint DungeonWing = 9110001;
 /// <summary>Town warp scroll item skills with a fixed destination (<see cref="TownWarpDestination"/>).</summary>
    public const uint PronteraWarp = 9100201, AlbertaWarp = 9100301, MorrocWarp = 9100701, TrainingGroundWarp = 9100801,
        JawaiiWarp = 9101001;

 /// <summary>Position_Info rows of the Training Ground and Jawaii destinations (see <see cref="TownWarpDestination"/>).</summary>
    public const uint TrainingGroundPosition = 294, JawaiiPosition = 410;

 /// <summary>Map_Type of an instanced dungeon map.</summary>
    public const byte MapTypeInstance = 1;
 /// <summary>Map_Type of an open field or town map.</summary>
    public const byte MapTypeField = 0;

 /// <summary>NPCInfo Function_Type of the multi-destination teleporter; its Function_ID is the group.</summary>
    public const int MultiTeleportFunctionType = 39;

 /// <summary>Slots of a multi-destination teleporter are numbered from 1: slot n selects entry n-1 of a group.</summary>
    public const int FirstTeleportSlot = 1;

 /// <summary>
 /// Destination group (NPCInfo Function_ID of the type-39 function) → Position_Info id per 1-based slot:
 /// <list type="bullet">
 /// <item>1 (NPC 17260000, map 1100): 274, 325, 326, 327 — fortress 3101.</item>
 /// <item>2 (NPC 28950000, map 1100): 275, 276 — fortress 3101 west/east gate.</item>
 /// <item>7 (NPC 31120000, map 1300): 370, 371, 372 — fortress 3201. Slot 3 is gated by siege ownership.</item>
 /// <item>8 (NPC 31130000, map 1300): 378, 365 — fortress 3201.</item>
 /// <item>12 (NPCs 31030000/31030001, the citadels): 381, 383, 384, 385 — map 9103.</item>
 /// </list>
 /// The order also matches Position_Info's own labels (group 1: interior, east, west, south fortress; group 12: the 7, 6,
 /// 9 and 3 o'clock entrances).
 /// </summary>
    public static readonly IReadOnlyDictionary<uint, uint[]> MeasuredDestinations = new Dictionary<uint, uint[]>
    {
        [1] = new uint[] { 274, 325, 326, 327 },
        [2] = new uint[] { 275, 276 },
        [7] = new uint[] { 370, 371, 372 },
        [8] = new uint[] { 378, 365 },
        [12] = new uint[] { 381, 383, 384, 385 },
    };

 /// <summary>
 /// Fixed destination of a town warp, or null for another skill or when the target map or Position_Info row is not loaded.
 /// </summary>
    public static WarpDestination? TownWarpDestination(uint skillId, IWorldCatalog catalog)
    {
        WarpDestination? dest = skillId switch
        {
            PronteraWarp => new WarpDestination(1100, 798.12f, 767.1f, 57.18f),
            AlbertaWarp => new WarpDestination(1200, 722.76f, 547.03f, 55.32f),
            MorrocWarp => new WarpDestination(1300, 434f, 249f, 12f),
            TrainingGroundWarp => FromPosition(TrainingGroundPosition, catalog),
            JawaiiWarp => FromPosition(JawaiiPosition, catalog),
            _ => null,
        };
        return dest is { } d && catalog.GetMap(d.MapId) is not null ? d : null;
    }

    private static WarpDestination? FromPosition(uint positionId, IWorldCatalog catalog)
        => catalog.GetPosition(positionId) is { } p ? new WarpDestination(p.Area_ID, p.Position_X, p.Position_Y, p.Position_Z) : null;

 /// <summary>
 /// Destination of the Dungeon Wing: the current map's Map_List return point, or null when the wing is refused there.
 /// </summary>
 /// <remarks>
 /// The wing is refused on a Map_Type 0 map, on a map with Map_ID 0, and when the return map is not in Map_List.
 /// </remarks>
    public static WarpDestination? DungeonWingDestination(MapData? current, IWorldCatalog catalog)
    {
        if (current is null || current.Map_Type == MapTypeField || current.Map_ID == 0 || catalog.GetMap(current.Map_ID) is null)
            return null;
        return new WarpDestination(current.Map_ID, current.x, current.y, current.z);
    }

 /// <summary>
 /// Destination of the Butterfly Wing: the bind point when its map is loaded, otherwise the new-character spawn.
 /// </summary>
 /// <remarks>
 /// Uses the save point and revival rules of <see cref="ResolveSavePoint"/>, excluding its first rule (the current map's
 /// return point).
 /// </remarks>
    public static WarpDestination ButterflyWingDestination(ushort bindIndex, IWorldCatalog catalog)
    {
        if (bindIndex != 0 && catalog.GetPosition(bindIndex) is { } bind && catalog.GetMap(bind.Area_ID) is not null)
            return new WarpDestination(bind.Area_ID, bind.Position_X, bind.Position_Y, bind.Position_Z);
        return new WarpDestination(SpawnRules.DefaultMapId, SpawnRules.DefaultX, SpawnRules.DefaultY, SpawnRules.DefaultZ);
    }

 /// <summary>
 /// Destination of an answer-time item skill (Butterfly Wing, Dungeon Wing) from the player's current map, or null when
 /// <paramref name="skillId"/> is another skill or the move is refused.
 /// </summary>
    public static WarpDestination? AnswerDestination(uint skillId, ushort bindIndex, uint playerMapId, IWorldCatalog catalog)
        => skillId switch
        {
            ButterflyWing => ButterflyWingDestination(bindIndex, catalog),
            DungeonWing => DungeonWingDestination(catalog.GetMap(playerMapId), catalog),
            _ => null,
        };

 /// <summary>
 /// Whether a move to <paramref name="destMapId"/> stays on the player's current map and does not start from an instance.
 /// </summary>
    public static bool IsInPlace(uint playerMapId, uint destMapId, IWorldCatalog catalog)
        => destMapId == playerMapId && catalog.GetMap(playerMapId) is not { Map_Type: MapTypeInstance };

 /// <summary>
 /// The Function_ID of the first of the NPC's function slots (NPCInfo Function_Type_N / Function_ID_N, N = 1..3) whose
 /// type is <paramref name="functionType"/>. NPCInfo has only three function slots.
 /// </summary>
    public static bool TryResolveFunctionId(IWorldCatalog catalog, uint npcId, uint functionType, out uint functionId)
    {
        functionId = 0;
        if (catalog.GetNpc(npcId) is not { } npc) return false;
        foreach (var (type, id) in new[]
                 {
                     (npc.Function_Type_1, npc.Function_ID_1),
                     (npc.Function_Type_2, npc.Function_ID_2),
                     (npc.Function_Type_3, npc.Function_ID_3),
                 })
        {
            if ((long)type == functionType)
            {
                functionId = id;
                return true;
            }
        }
        return false;
    }

 /// <summary>
 /// Resolves the Position_Info row an NPC's function of type <paramref name="functionType"/> points at (NPCInfo
 /// Function_Type_N / Function_ID_N, N = 1..3). A slot of that type with no Position_Info row is skipped, and the next
 /// slot of the type is tried.
 /// </summary>
    public static bool TryResolveFunctionPosition(IWorldCatalog catalog, uint npcId, uint functionType, out PositionData position)
    {
        position = null!;
        if (catalog.GetNpc(npcId) is not { } npc) return false;
        foreach (var (type, id) in new[]
                 {
                     (npc.Function_Type_1, npc.Function_ID_1),
                     (npc.Function_Type_2, npc.Function_ID_2),
                     (npc.Function_Type_3, npc.Function_ID_3),
                 })
        {
            if ((long)type == functionType && catalog.GetPosition(id) is { } p)
            {
                position = p;
                return true;
            }
        }
        return false;
    }

 /// <summary>
 /// The Position_Info row slot <paramref name="slot"/> of NPC <paramref name="npcId"/> leads to: the NPC's type-39
 /// function gives the group, <see cref="MeasuredDestinations"/> the row. False when any link is missing, including a
 /// destination map absent from Map_List.
 /// </summary>
    public static bool TryResolveMultiTeleportDestination(IWorldCatalog catalog, uint npcId, byte slot, out PositionData destination)
    {
        destination = null!;
        if (catalog.GetNpc(npcId) is not { } info) return false;

        uint? group = null;
        foreach (var (type, id) in new[]
                 {
                     (info.Function_Type_1, info.Function_ID_1),
                     (info.Function_Type_2, info.Function_ID_2),
                     (info.Function_Type_3, info.Function_ID_3),
                 })
        {
            if (type == MultiTeleportFunctionType) { group = id; break; }
        }

        if (group is not uint g || !MeasuredDestinations.TryGetValue(g, out var slots)) return false;
        if (slot < FirstTeleportSlot || slot > slots.Length) return false;
        if (catalog.GetPosition(slots[slot - FirstTeleportSlot]) is not { } pos) return false;
        if (catalog.GetMap(pos.Area_ID) is null) return false;

        destination = pos;
        return true;
    }

 /// <summary>
 /// Where a PC goes when it returns to its save point: the /escape (1677) and a type-0 revival (1372).
 /// </summary>
 /// <remarks>
 /// <para>Three rules, in this order:</para>
 /// <list type="number">
 /// <item>The current map's own return point, when Map_List gives one (Map_ID != 0; the WoE fortresses and the instanced
 /// dungeons): that row's (Map_ID, x, y, z). The return point takes precedence over the bind.</item>
 /// <item>The bind point, <paramref name="bindIndex"/> as a Position_Info id.</item>
 /// <item>No bind: the new-character spawn (<see cref="SpawnRules"/> defaults).</item>
 /// </list>
 /// </remarks>
    public static (uint MapId, float X, float Y, float Z, string Source) ResolveSavePoint(
        uint playerMapId, ushort bindIndex, IWorldCatalog catalog)
    {
        var current = catalog.GetMap(playerMapId);
        if (current is { Map_ID: not 0 } && catalog.GetMap(current.Map_ID) is not null)
            return (current.Map_ID, current.x, current.y, current.z, $"Map_List {playerMapId} return point");

        if (bindIndex != 0
            && catalog.GetPosition(bindIndex) is { } bind
            && catalog.GetMap(bind.Area_ID) is not null)
            return (bind.Area_ID, bind.Position_X, bind.Position_Y, bind.Position_Z, $"bind {bindIndex}");

        return (SpawnRules.DefaultMapId, SpawnRules.DefaultX, SpawnRules.DefaultY, SpawnRules.DefaultZ,
            "no bind: new-character spawn");
    }
}
