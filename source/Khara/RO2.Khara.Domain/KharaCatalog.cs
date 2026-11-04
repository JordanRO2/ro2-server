using System.Runtime.CompilerServices;
using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Kharas.Domain;

/// <summary>One Khara from the Khara_Info table: its main row and its condition rows.</summary>
public sealed class KharaDefinition
{
    public KharaDefinition(uint id, KharaInfo main, IReadOnlyList<KharaInfo> conditions)
    {
        Id = id;
        Main = main;
        Conditions = conditions;
    }

    public uint Id { get; }

 /// <summary>Main row of the Khara's active block. It holds the reward, the accept action and the level window.</summary>
    public KharaInfo Main { get; }

 /// <summary>Condition rows that must all be met to complete the Khara, in index order.</summary>
    public IReadOnlyList<KharaInfo> Conditions { get; }

    public bool AutoChallenge => Main.Auto_Challenge != 0;

 /// <summary>
 /// Quest whose completion gates this Khara, or null. Such a Khara is accepted when that quest is completed.
 /// </summary>
    public uint? GateQuest => Main.Condition_Type == KharaRules.ConditionQuestDone ? Main.Condition_Value1 : null;

 /// <summary>True when the level is within the Khara's level window (0 as the upper bound means no limit).</summary>
    public bool LevelAllows(byte level) =>
        level >= Main.MIN_Base_Level && (Main.MAX_Base_Level == 0 || level <= Main.MAX_Base_Level);

 /// <summary>
 /// Whether the Khara's class mask allows a character of <paramref name="combatClass"/> to hold it. A mask of 0 allows every class.
 /// </summary>
    public bool JobAllows(byte combatClass) =>
        Main.PC_Job == 0 || (combatClass is >= 1 and <= 32 && (Main.PC_Job & (1u << (combatClass - 1))) != 0);

    public bool HasCondition(byte completeType, uint value1)
    {
        foreach (var c in Conditions)
            if (c.Complete_Type == completeType && c.Complete_Type_Value1 == value1) return true;
        return false;
    }
}

/// <summary>Khara_Info rows grouped by Khara id, with lookups used by the progress triggers.</summary>
public sealed class KharaCatalog
{
    private static readonly ConditionalWeakTable<IReadOnlyList<KharaInfo>, KharaCatalog> ByTable = new();

    private readonly Dictionary<uint, KharaDefinition> _byId = new();
    private readonly Dictionary<uint, List<KharaDefinition>> _byQuest = new();
    private readonly Dictionary<uint, List<KharaDefinition>> _gatedByQuest = new();
    private readonly Dictionary<(byte, uint), List<KharaDefinition>> _byRow = new();

 /// <summary>The catalog of a loaded store. A reload that replaces the row list gets a new catalog.</summary>
    public static KharaCatalog For(GameDataStore store) =>
        ByTable.GetValue(store.KharaInfo, rows => new KharaCatalog(rows));

    public KharaCatalog(IReadOnlyList<KharaInfo> rows)
    {
        var grouped = new Dictionary<uint, List<KharaInfo>>();
        var order = new List<uint>();
        foreach (var row in rows)
        {
            if (!grouped.TryGetValue(row.Quest_ID, out var list))
            {
                grouped[row.Quest_ID] = list = new List<KharaInfo>();
                order.Add(row.Quest_ID);
            }
            list.Add(row);
        }
        foreach (var id in order)
        {
            var block = RetailBlock(grouped[id]);
            var def = new KharaDefinition(id, block[0], ConditionRows(block));
            _byId[id] = def;
            if (def.GateQuest is { } gate)
            {
                if (!_gatedByQuest.TryGetValue(gate, out var gated))
                    _gatedByQuest[gate] = gated = new List<KharaDefinition>();
                gated.Add(def);
            }
            foreach (var c in def.Conditions)
            {
                var key = (c.Complete_Type, KharaRules.RowSubject(c));
                if (!_byRow.TryGetValue(key, out var rowRefs))
                    _byRow[key] = rowRefs = new List<KharaDefinition>();
                if (!rowRefs.Contains(def)) rowRefs.Add(def);

                if (c.Complete_Type != KharaRules.CompleteQuestDone) continue;
                if (!_byQuest.TryGetValue(c.Complete_Type_Value1, out var refs))
                    _byQuest[c.Complete_Type_Value1] = refs = new List<KharaDefinition>();
                if (!refs.Contains(def)) refs.Add(def);
            }
        }
        foreach (var refs in _byQuest.Values)
            refs.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var refs in _gatedByQuest.Values)
            refs.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var refs in _byRow.Values)
            refs.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

 /// <summary>Khara ids with a condition row of <paramref name="completeType"/> whose subject is <paramref name="subject"/>.</summary>
    public IReadOnlyList<KharaDefinition> WithRow(byte completeType, uint subject) =>
        _byRow.TryGetValue((completeType, subject), out var refs) ? refs : Array.Empty<KharaDefinition>();

    public KharaDefinition? Get(uint kharaId) => _byId.GetValueOrDefault(kharaId);

 /// <summary>NationEnable bit of the service that the Khara block is used for.</summary>
    public const int RetailNationBit = 4;

 /// <summary>The one retail-block rule shared by the catalog, the title grants and the title traits: a row whose
 /// NationEnable is 0 or carries the service bit is the one the service uses.</summary>
    public static bool IsRetail(uint nationEnable) => nationEnable == 0 || (nationEnable & RetailNationBit) != 0;

 /// <summary>
 /// The active block of one Khara's rows. Each block starts at a row with a non-zero Quest_Type and runs to the next such row.
 /// The block whose main row has NationEnable 0 or the service bit is used. The first block is used when none matches.
 /// </summary>
    public static IReadOnlyList<KharaInfo> RetailBlock(IReadOnlyList<KharaInfo> rowsOfOneId)
    {
        List<KharaInfo>? first = null, current = null, retail = null;
        foreach (var row in rowsOfOneId)
        {
            if (current is null || row.Quest_Type != 0)
            {
                if (retail is not null) break;
                current = new List<KharaInfo>();
                first ??= current;
                if (IsRetail(row.NationEnable)) retail = current;
            }
            current.Add(row);
        }
        return (IReadOnlyList<KharaInfo>?)retail ?? first ?? (IReadOnlyList<KharaInfo>)Array.Empty<KharaInfo>();
    }

 /// <summary>Khara ids with a Complete_Type 13 row naming <paramref name="questId"/>.</summary>
    public IReadOnlyList<KharaDefinition> ReferencingQuest(uint questId) =>
        _byQuest.TryGetValue(questId, out var refs) ? refs : Array.Empty<KharaDefinition>();

 /// <summary>Khara ids whose main row is gated on <paramref name="questId"/>.</summary>
    public IReadOnlyList<KharaDefinition> GatedByQuest(uint questId) =>
        _gatedByQuest.TryGetValue(questId, out var refs) ? refs : Array.Empty<KharaDefinition>();

 /// <summary>
 /// Condition rows of one block: its rows up to the next block start, keeping rows with a non-zero Complete_Type.
 /// </summary>
    public static IReadOnlyList<KharaInfo> ConditionRows(IReadOnlyList<KharaInfo> rowsOfOneId)
    {
        var result = new List<KharaInfo>(rowsOfOneId.Count);
        for (int i = 0; i < rowsOfOneId.Count; i++)
        {
            var row = rowsOfOneId[i];
            if (i > 0 && row.Quest_Type != 0) break;
            if (row.Complete_Type != 0) result.Add(row);
        }
        return result;
    }
}

/// <summary>Character facts that a Khara check needs besides the Khara state.</summary>
public sealed class KharaFacts
{
    public byte Level { get; init; }

 /// <summary>Number of items of a template the character holds, in the bag and worn gear; null when the inventory could not be read, which leaves held-item rows untouched.</summary>
    public Func<uint, int?> HeldCount { get; init; } = _ => 0;

    public Func<uint, bool> IsQuestCompleted { get; init; } = _ => false;

 /// <summary>True when the character is in a guild.</summary>
    public bool InGuild { get; init; }

 /// <summary>Profession of the character, or 0 when unknown.</summary>
    public byte Profession { get; init; }

 /// <summary>Combat class of the character, or 0 when unknown.</summary>
    public byte CombatClass { get; init; }

 /// <summary>Profession level of the character.</summary>
    public byte ProfessionLevel { get; init; }
}

/// <summary>One message the Khara engine produced, in the order it must be sent.</summary>
public readonly record struct KharaFrame(ushort Opcode, byte[] Body);
