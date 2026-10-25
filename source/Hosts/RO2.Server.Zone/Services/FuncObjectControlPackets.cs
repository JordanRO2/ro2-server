using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the 2398 OnChangeState pair of a function-object control: the start sent to the controller, and the
/// state-0 reset that ends it. Also resolves the target both need.
/// </summary>
/// <remarks>
/// The start carries a state byte derived from the type of the target object. The reset names the target and is
/// sent when the control ends.
/// </remarks>
public static class FuncObjectControlPackets
{
 /// <summary>Trailing value (blob+19) of every control 2398 this server sends.</summary>
    internal const uint ControlStateTrailer = 2;

 /// <summary>The state an NPC (0x13) target puts its controller in.</summary>
    internal const byte NpcControlState = 0x14;

    /// <summary>Actor-handle tag (low byte) of an NPC; a 1410 on one puts the controller in <see cref="NpcControlState"/>.</summary>
    private const byte NpcActorTag = NpcStateManager.NpcActorTag;

    // 2398 state byte (blob+12) per FuncObject_Type of the controlled object. The state selects the controller's
    // gather/interact animation; a state-0 2398 ends each of them. Type 8 (portals, dungeon entrances) sends none.
    private const byte StateFuncObjectType1 = 0x0D;
    private const byte StateFuncObjectType2Gather = 0x0E;
    private const byte StateFuncObjectType4 = 0x10;
    private const byte StateFuncObjectType5Siege = 0x11;

    private static readonly ConditionalWeakTable<GameDataStore, FrozenDictionary<uint, FuncObjectInfo>> InfoByStore = new();

 /// <summary>
 /// The 2398 state a 1410 on a function object of <paramref name="funcObjectType"/> sends for the controller,
 /// or null when no 2398 is sent.
 /// </summary>
    public static byte? ControlStateFor(byte funcObjectType) => funcObjectType switch
    {
        1 => StateFuncObjectType1,
        2 => StateFuncObjectType2Gather,
        4 => StateFuncObjectType4,
        5 => StateFuncObjectType5Siege,
        _ => null,
    };

 /// <summary>
 /// The 2398 state a 1410 on <paramref name="target"/> sends for the controller: <see cref="NpcControlState"/> for
 /// an NPC, <see cref="ControlStateFor(byte)"/> of its template for a function object of <paramref name="mapId"/>,
 /// null for anything else (a handle this server did not spawn there).
 /// </summary>
    public static byte? ControlStateFor(GameDataStore store, uint mapId, uint target)
    {
        if ((target & 0xFF) == NpcActorTag) return NpcControlState;
        return TryGetTemplate(store, mapId, target, out var info) ? ControlStateFor(info.FuncObject_Type) : null;
    }

 /// <summary>
 /// The FuncObjectInfo row of the function object <paramref name="handle"/> stands for on <paramref name="mapId"/>
 /// (FuncObjectRegistry.ForMap, the same list the 1304 burst's 2512 sent).
 /// </summary>
    public static bool TryGetTemplate(GameDataStore store, uint mapId, uint handle, out FuncObjectInfo info)
    {
        info = null!;
        if (!TryGetObject(store, mapId, handle, out var obj)) return false;
        return Info(store).TryGetValue(obj.TemplateId, out info!);
    }

 /// <summary>The registry row of <paramref name="handle"/> on <paramref name="mapId"/>.</summary>
    public static bool TryGetObject(GameDataStore store, uint mapId, uint handle, out FuncObject obj)
    {
        obj = null!;
        if ((handle & 0xFF) != FuncObjectRegistry.FuncObjectActorTag) return false;
        foreach (var o in FuncObjectRegistry.ForMap(store, mapId))
        {
            if (o.Handle == handle)
            {
                obj = o;
                return true;
            }
        }
        return false;
    }

 /// <summary>
 /// Builds the control start 2398 for the controller, carrying <paramref name="state"/>. The body is 29 bytes:
 /// <c>[u16 msgTag 58090][u32 pc][u32 pc][u8 state][u8 0][u8 0][u32 0][u32 2][u32 0]</c> (no target named).
 /// </summary>
    public static byte[] BuildControlState2398(uint pcHandle, byte state)
        => CombatPackets.BuildChangeState2398(pcHandle, state, 0, ControlStateTrailer);

 /// <summary>
 /// Builds the control end 2398 (state 0) that names <paramref name="target"/>. The body is 29 bytes:
 /// <c>[u16 msgTag 58090][u32 pc][u32 pc][u8 0][u8 0][u8 0][u32 0][u32 2][u32 target]</c>.
 /// </summary>
    public static byte[] BuildControlReset2398(uint pcHandle, uint target)
        => CombatPackets.BuildChangeState2398(pcHandle, CombatPackets.ActorState.PcNormal, target, ControlStateTrailer);

    private static FrozenDictionary<uint, FuncObjectInfo> Info(GameDataStore store)
        => InfoByStore.GetValue(store, static s =>
        {
            var d = new Dictionary<uint, FuncObjectInfo>();
            foreach (var row in s.FuncObjectInfo) d.TryAdd(row.ID, row);
            return d.ToFrozenDictionary();
        });
}
