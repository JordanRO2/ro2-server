using System.Collections.Concurrent;
using System.Collections.Frozen;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>A function object in the world: its ZoneSpawn row, its runtime handle and its position.</summary>
/// <param name="Handle">Actor handle: a 24-bit serial above the function-object tag <c>0x14</c> in the low byte.</param>
/// <param name="TemplateId">FuncObjectInfo.ID, which is the ZoneSpawn row's AssetIndex.</param>
/// <param name="X">World X coordinate.</param>
/// <param name="Y">World Y coordinate.</param>
/// <param name="Z">World Z coordinate.</param>
/// <param name="Rotation">Rotation in degrees, as in the ZoneSpawn row.</param>
public sealed record FuncObject(uint Handle, uint TemplateId, float X, float Y, float Z, float Rotation, uint MapId, int SpawnIndex);

/// <summary>
/// The function objects of each map, built once per map from the ZoneSpawn data and shared by every player on
/// it, so each object has one handle for everyone.
/// </summary>
/// <remarks>
/// A row becomes a function object when its AssetIndex is a FuncObjectInfo id, except for templates left out of
/// the map (<see cref="RetailUnlistedTemplates"/>, <see cref="RetailUnlistedOnMap"/>) and rows whose template is an
/// NPC. Each object stands at its row's position and rotation. Its handle is kept for the life of the process.
/// </remarks>
public static class FuncObjectRegistry
{
 /// <summary>Low byte of every function-object handle.</summary>
    public const byte FuncObjectActorTag = 0x14;

 /// <summary>Bit width of the actor-type tag in the low byte of a handle; the serial sits above it.</summary>
    private const int ActorTagBits = 8;

 /// <summary>Mask of the 24-bit serial of a function-object handle (0 is never issued).</summary>
    private const uint SerialMask = 0x00FF_FFFF;

 /// <summary>
 /// The FuncObjectInfo ids with ZoneSpawn rows that are left out of every map, because the original server
 /// never lists them.
 /// </summary>
 /// <remarks>
 /// Some of these ids are listed on other maps. The rest only stand on maps where the original server listed
 /// no function objects.
 /// </remarks>
    internal static readonly FrozenSet<uint> RetailUnlistedTemplates = new uint[]
    {
        
        80010252, 80010262, 80010272, 80010282, 80010292, 80010302, 80010620, 80010630, 80011073, 80011100, 80011600,
        80011700, 80012170, 80012180, 80012220, 80013020, 80013090, 80013091, 80013092, 80100080, 80100090, 80100100,
        80100110, 80100320, 80100400, 80100450, 80100470, 80100810, 80100890, 80101090, 80101100, 80190000, 80850000,
        81840000, 82360000, 82370000, 82380000, 82430000, 82460000,
        
        80010241, 80010251, 80010261, 80010271, 80010280, 80010281, 80010290, 80010291, 80010300, 80010301, 80010430,
        80011075, 80012230, 80012250, 80013040, 80013050, 80013080, 80013093, 80013094, 80013095, 80013096, 80101050,
        80101170, 80660000, 80670000, 80690000, 81820000, 82400000, 82420000, 82440000,
    }.ToFrozenSet();

 /// <summary>
 /// The (MapId, FuncObjectInfo id) pairs left out of one map although the original server lists the template on
 /// another. <see cref="Build"/> drops their rows.
 /// </summary>
 /// <remarks>
 /// Dark Whisper - Lower Floor (map 4302) has 74 function-object rows, and the original server lists 11 of them.
 /// Both listed-elsewhere templates are gathering nodes listed on Dayr Desert (map 1301), and never appear on
 /// map 4302.
 /// </remarks>
    internal static readonly FrozenSet<(uint MapId, uint TemplateId)> RetailUnlistedOnMap = new (uint, uint)[]
    {
        (4302, 80010690), (4302, 80010700),
    }.ToFrozenSet();

    private static int _serial;
    private static readonly ConcurrentDictionary<uint, Lazy<IReadOnlyList<FuncObject>>> Maps = new();

 /// <summary>The function objects of <paramref name="mapId"/>, built on first use and the same list afterwards.</summary>
    public static IReadOnlyList<FuncObject> ForMap(GameDataStore store, uint mapId)
    {
        if (mapId == 0) return Array.Empty<FuncObject>();
        return Maps.GetOrAdd(mapId, map => new Lazy<IReadOnlyList<FuncObject>>(() =>
            Build(store.GetMapSpawns(map), ListedTemplateIds(store.FuncObjectInfo),
                template => store.Npcs.ContainsKey(template), NextHandle)))
            .Value;
    }

 /// <summary>
 /// The FuncObjectInfo ids this server lists in a 2512: the id of every row of <paramref name="funcObjectInfo"/>
 /// except <see cref="RetailUnlistedTemplates"/>.
 /// </summary>
    internal static IReadOnlySet<uint> ListedTemplateIds(IEnumerable<FuncObjectInfo> funcObjectInfo)
    {
        var ids = new HashSet<uint>();
        foreach (var row in funcObjectInfo)
            if (!RetailUnlistedTemplates.Contains(row.ID)) ids.Add(row.ID);
        return ids;
    }

 /// <summary>
 /// The function objects of <paramref name="spawns"/> (one map's ZoneSpawn rows), in row order: every row whose
 /// AssetIndex is in <paramref name="funcObjectIds"/> (ForMap passes <see cref="ListedTemplateIds"/>), is not an NPC
 /// template and whose (MapId, AssetIndex) is not in <see cref="RetailUnlistedOnMap"/>, each with the next handle
 /// from <paramref name="nextHandle"/>.
 /// </summary>
 /// <remarks>
 /// The per-map exclusion is applied here rather than through <paramref name="funcObjectIds"/> because it depends on
 /// the row's map, which a set of template ids cannot carry.
 /// </remarks>
    internal static IReadOnlyList<FuncObject> Build(IEnumerable<SpawnEntry> spawns, IReadOnlySet<uint> funcObjectIds,
        Func<uint, bool> isNpcTemplate, Func<uint> nextHandle)
    {
        var list = new List<FuncObject>();
        foreach (var s in spawns)
        {
            if (s.AssetIndex == 0 || !funcObjectIds.Contains(s.AssetIndex) || isNpcTemplate(s.AssetIndex)) continue;
            if (RetailUnlistedOnMap.Contains((s.MapId, s.AssetIndex))) continue;
            list.Add(new FuncObject(nextHandle(), s.AssetIndex, s.PosX, s.PosY, s.PosZ, s.Rot, s.MapId, s.Index));
        }
        return list;
    }

 /// <summary>The next unused handle, <c>(serial &lt;&lt; 8) | 0x14</c> with a 24-bit serial that skips 0.</summary>
    internal static uint NextHandle()
    {
        uint serial;
        do serial = (uint)Interlocked.Increment(ref _serial) & SerialMask;
        while (serial == 0);
        return (serial << ActorTagBits) | FuncObjectActorTag;
    }
}
