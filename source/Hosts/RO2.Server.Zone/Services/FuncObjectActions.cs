using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>What a function object does when its control ends (a 1412), from its FuncAction row.</summary>
public enum FuncObjectActionKind
{
 /// <summary>No action this server carries out after the end.</summary>
    None,

 /// <summary>FuncAction Action_Type 7: move the player to Position_Info[Action_Value1].</summary>
    Portal,

 /// <summary>FuncAction Action_Type 31: open the level window of DungeonGroupID Action_Value1 (2741).</summary>
    DungeonEntrance,
}

/// <summary>The action a 1412 on a function object resolves to.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Value">FuncAction.Action_Value1: the Position_Info id of a portal, the DungeonGroupID of an entrance.</param>
public readonly record struct FuncObjectAction(FuncObjectActionKind Kind, uint Value);

/// <summary>
/// Resolves function-object templates to their FuncAction and FuncReward data, and rolls their rewards.
/// </summary>
/// <remarks>
/// Action type 7 is a portal (move to a position), 31 a dungeon entrance (opens its level window) and 11 a
/// profession-exp gather. Other action types have no server-side effect.
/// </remarks>
public static class FuncObjectActions
{
 /// <summary>FuncAction.Action_Type of a portal.</summary>
    public const byte PortalActionType = 7;

 /// <summary>FuncAction.Action_Type of a dungeon entrance.</summary>
    public const byte DungeonEntranceActionType = 31;

 /// <summary>FuncAction.Action_Type of a gather that grants profession (job) exp, Action_Value1 of it.</summary>
    public const byte ProfessionExpActionType = 11;

 /// <summary>FuncReward.Rate is out of this: 10,000 is certain.</summary>
    internal const int RateScale = 10_000;

    private static readonly ConditionalWeakTable<GameDataStore, FrozenDictionary<uint, FuncAction>> ActionsByStore = new();
    private static readonly ConditionalWeakTable<GameDataStore, FrozenDictionary<uint, FuncReward[]>> RewardsByStore = new();

 /// <summary>The action of <paramref name="templateId"/> in <paramref name="store"/>'s FuncAction table.</summary>
    public static FuncObjectAction Resolve(GameDataStore store, uint templateId)
        => Resolve(ActionsByStore.GetValue(store, static s => IndexActions(s.FuncAction)), templateId);

 /// <summary>The action of <paramref name="templateId"/> in <paramref name="byId"/> (see <see cref="IndexActions"/>).</summary>
    public static FuncObjectAction Resolve(IReadOnlyDictionary<uint, FuncAction> byId, uint templateId)
    {
        if (!byId.TryGetValue(templateId, out var row)) return new FuncObjectAction(FuncObjectActionKind.None, 0);
        return row.Action_Type switch
        {
            PortalActionType => new FuncObjectAction(FuncObjectActionKind.Portal, row.Action_Value1),
            DungeonEntranceActionType => new FuncObjectAction(FuncObjectActionKind.DungeonEntrance, row.Action_Value1),
            _ => new FuncObjectAction(FuncObjectActionKind.None, row.Action_Value1),
        };
    }

 /// <summary>The base profession exp a gather of <paramref name="templateId"/> grants: the Action_Value1 of its
 /// FuncAction row when that row is Action_Type 11, else 0.</summary>
    public static uint ProfessionExpOf(GameDataStore store, uint templateId)
        => ActionsByStore.GetValue(store, static s => IndexActions(s.FuncAction)).TryGetValue(templateId, out var row)
           && row.Action_Type == ProfessionExpActionType
            ? row.Action_Value1
            : 0;

 /// <summary>Indexes FuncAction rows by ID. When an ID has several rows, the first one is kept.</summary>
    public static FrozenDictionary<uint, FuncAction> IndexActions(IEnumerable<FuncAction> rows)
    {
        var d = new Dictionary<uint, FuncAction>();
        foreach (var r in rows) d.TryAdd(r.ID, r);
        return d.ToFrozenDictionary();
    }

 /// <summary>The FuncReward rows of <paramref name="templateId"/>, in table order (empty when it has none).</summary>
    public static IReadOnlyList<FuncReward> RewardsOf(GameDataStore store, uint templateId)
        => RewardsByStore.GetValue(store, static s => IndexRewards(s.FuncReward)).TryGetValue(templateId, out var rows)
            ? rows
            : Array.Empty<FuncReward>();

 /// <summary>FuncReward rows grouped by ID, table order kept within each group.</summary>
    public static FrozenDictionary<uint, FuncReward[]> IndexRewards(IEnumerable<FuncReward> rows)
        => rows.GroupBy(r => r.ID).ToFrozenDictionary(g => g.Key, g => g.ToArray());

 /// <summary>
 /// Rolls one gather's payout from <paramref name="rows"/>: each row pays independently, with probability
 /// Rate / 10,000, a count drawn uniformly from Min_Num to Max_Num.
 /// </summary>
    public static List<(uint ItemId, int Count)> Roll(IReadOnlyList<FuncReward> rows, Random random)
    {
        var paid = new List<(uint ItemId, int Count)>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Item_ID == 0 || row.Rate == 0) continue;
            if (row.Rate < RateScale && random.Next(RateScale) >= row.Rate) continue;
            int min = Math.Max(1, (int)row.Min_Num);
            int max = Math.Max(min, (int)row.Max_Num);
            paid.Add((row.Item_ID, random.Next(min, max + 1)));
        }
        return paid;
    }
}
