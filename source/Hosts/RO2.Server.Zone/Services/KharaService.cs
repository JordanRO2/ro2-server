using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

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
    public uint? GateQuest => Main.Condition_Type == KharaService.ConditionQuestDone ? Main.Condition_Value1 : null;

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
                var key = (c.Complete_Type, KharaService.RowSubject(c));
                if (!_byRow.TryGetValue(key, out var rowRefs))
                    _byRow[key] = rowRefs = new List<KharaDefinition>();
                if (!rowRefs.Contains(def)) rowRefs.Add(def);

                if (c.Complete_Type != KharaService.CompleteQuestDone) continue;
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
                if (row.NationEnable == 0 || (row.NationEnable & RetailNationBit) != 0) retail = current;
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

 /// <summary>Number of items of a template the character holds, in the bag and worn gear.</summary>
    public Func<uint, int> HeldCount { get; init; } = _ => 0;

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

/// <summary>
/// Khara completion rules, applied to a private copy of a character's Khara state. Each method appends the messages to send,
/// in order, to <see cref="Frames"/>.
/// </summary>
/// <remarks>
/// Supported condition types, by Complete_Type:
/// <list type="bullet">
/// <item>2: kill a given NPC a set number of times. Each kill adds one, up to the target.</item>
/// <item>4: hold a set number of a given item. The count follows the held quantity, up to the target.</item>
/// <item>13: a given quest is completed. Completes the Khara and sends no count.</item>
/// <item>40: a given Khara is completed.</item>
/// <item>6, 22, 26, 27, 29, 31, 32: stepped counters. Each event adds one point, up to the target.</item>
/// <item>43: the character is in a guild.</item>
/// <item>14, 15: base level or profession level reached. These rows send no count.</item>
/// <item>30: items repaired at an NPC. The count adds the number of items repaired.</item>
/// </list>
/// Any other Complete_Type never holds, so the Khara stays accepted. Auto-accept: an Auto_Challenge Khara that the
/// character neither holds nor completed is accepted by the server on its first matching event, when its level window,
/// class mask and profession allow it.
/// </remarks>
public sealed class KharaEngine
{
    private readonly KharaCatalog _catalog;
    private readonly KharaFacts _facts;

    public KharaEngine(KharaCatalog catalog, KharaStateDocument state, KharaFacts facts)
    {
        _catalog = catalog;
        State = state;
        _facts = facts;
    }

    public KharaStateDocument State { get; }
    public List<KharaFrame> Frames { get; } = new();
    public bool Changed { get; private set; }

 /// <summary>
 /// Records a Khara the character has just accepted, sends the initial counts, spends the accept cost when the Khara has one,
 /// and completes it when every condition already holds. Returns false, with nothing sent, for an unknown or already
 /// accepted or completed Khara.
 /// </summary>
    public bool Accept(uint kharaId)
    {
        var def = _catalog.Get(kharaId);
        if (def is null || State.FindActive(kharaId) is not null || State.IsCompleted(kharaId)) return false;

        var entry = new KharaActiveDocument { KharaId = kharaId, State = KharaService.StateInProgress };
        for (int i = 0; i < def.Conditions.Count; i++) entry.Counts.Add(0);
        InsertActive(entry);
        Changed = true;

        
        
        
        
        
        
        
        
        if (KharaService.IsTimed(def))
            Frames.Add(new KharaFrame(ZoneS2COpcodes.AnsKharaTimerStart, KharaService.BuildAnsKharaTimerStart2580(kharaId)));

        
        RefreshHeldRows(def, entry, onlyItem: null);
        
        
        RefreshTitleRows(def, entry);

        if (def.Main.Active_Type == KharaService.ActiveOnAccept && def.Main.Action_Type == KharaService.ActionSpendPoints
            && def.Main.Action_Value1 > 0)
        {
            State.Points = State.Points > def.Main.Action_Value1 ? State.Points - def.Main.Action_Value1 : 0;
            Frames.Add(new KharaFrame(ZoneS2COpcodes.OnPcKharaPointChangeNotify, KharaService.BuildPointChange2502(State.Points)));
        }

        if (Satisfied(def, entry)) Complete(def, entry);
        return true;
    }

 /// <summary>
 /// Handles a completed quest: checks the accepted Khara rows that use the quest, auto-accepts the Khara that the quest
 /// unlocks, and accepts the quest-gated Khara the character may take.
 /// </summary>
    public void OnQuestCompleted(uint questId)
    {
        foreach (var def in _catalog.ReferencingQuest(questId))
        {
            var entry = State.FindActive(def.Id);
            if (entry is not null)
            {
                if (Satisfied(def, entry)) Complete(def, entry);
                continue;
            }
            if (!AutoEligible(State, def, _facts)) continue;
            Frames.Add(new KharaFrame(ZoneS2COpcodes.AnsKharaAccept, KharaService.BuildAnsKharaAccept2574(def.Id, 0)));
            Accept(def.Id);
        }
        foreach (var def in _catalog.GatedByQuest(questId))
        {
            if (!AutoEligible(State, def, _facts)) continue;
            Frames.Add(new KharaFrame(ZoneS2COpcodes.AnsKharaAccept, KharaService.BuildAnsKharaAccept2574(def.Id, 0)));
            Accept(def.Id);
        }
    }

 /// <summary>
 /// Handles a level change: checks the accepted Kharas whose conditions are base level or profession level.
 /// </summary>
    public void OnLevelChanged()
    {
        foreach (var entry in State.Active.ToList())
        {
            var def = _catalog.Get(entry.KharaId);
            if (def is null || !State.Active.Contains(entry) || !def.Conditions.Any(c => KharaService.IsLevelRow(c.Complete_Type))) continue;
            if (Satisfied(def, entry)) Complete(def, entry);
        }
    }

 /// <summary>Handles a kill of <paramref name="npcId"/>: advances the matching kill rows and completes Kharas that are now met.</summary>
    public void OnNpcKilled(uint npcId)
    {
        AutoAcceptOn(KharaService.CompleteKillNpc, npcId);
        foreach (var entry in State.Active.ToList())
        {
            var def = _catalog.Get(entry.KharaId);
            
            if (def is null || !State.Active.Contains(entry)) continue;
            bool stepped = false;
            for (int i = 0; i < def.Conditions.Count; i++)
            {
                var c = def.Conditions[i];
                if (c.Complete_Type != KharaService.CompleteKillNpc || c.Complete_Type_Value1 != npcId) continue;
                var count = CountOf(entry, i);
                if (count >= c.Complete_Type_Value2) continue;
                count++;
                SetCount(entry, i, count);
                Emit2577(def.Id, i, npcId, count);
                stepped = true;
            }
            if (stepped && Satisfied(def, entry)) Complete(def, entry);
        }
    }

 /// <summary>
 /// Handles a change in the held count of <paramref name="itemId"/>: updates the matching item rows and completes Kharas that
 /// are now met.
 /// </summary>
    public void OnHeldCountChanged(uint itemId)
    {
        
        
        if (_facts.HeldCount(itemId) > 0) AutoAcceptOn(KharaService.CompleteHoldItem, itemId);
        foreach (var entry in State.Active.ToList())
        {
            var def = _catalog.Get(entry.KharaId);
            if (def is null || !State.Active.Contains(entry)) continue;
            if (RefreshHeldRows(def, entry, itemId) && Satisfied(def, entry)) Complete(def, entry);
        }
    }

 /// <summary>
 /// Handles a counted event of a stepped-counter type about <paramref name="subject"/>. Auto-accepts the Kharas it starts,
 /// then steps each matching accepted row by one, up to its target.
 /// </summary>
    public void OnCountedEvent(byte completeType, uint subject) => OnCountedEvent(completeType, subject, 1);

 /// <summary>
 /// Same as <see cref="OnCountedEvent(byte, uint)"/>, for an event worth <paramref name="amount"/> points, with each row
 /// stepping up to its target.
 /// </summary>
    public void OnCountedEvent(byte completeType, uint subject, uint amount)
    {
        if (amount == 0) return;
        AutoAcceptOn(completeType, subject);
        foreach (var entry in State.Active.ToList())
        {
            var def = _catalog.Get(entry.KharaId);
            if (def is null || !State.Active.Contains(entry)) continue;
            bool stepped = false;
            for (int i = 0; i < def.Conditions.Count; i++)
            {
                var c = def.Conditions[i];
                if (c.Complete_Type != completeType || KharaService.RowSubject(c) != subject) continue;
                var count = CountOf(entry, i);
                var target = KharaService.RowTarget(c);
                if (count >= target) continue;
                count = (uint)Math.Min(target, (ulong)count + amount);
                SetCount(entry, i, count);
                Emit2577(def.Id, i, subject, count);
                stepped = true;
            }
            if (stepped && Satisfied(def, entry)) Complete(def, entry);
        }
    }

 /// <summary>Handles joining a guild: completes accepted Kharas whose guild condition is now met.</summary>
    public void OnGuildJoined()
    {
        foreach (var entry in State.Active.ToList())
        {
            var def = _catalog.Get(entry.KharaId);
            if (def is null || !State.Active.Contains(entry) || !def.Conditions.Any(c => c.Complete_Type == KharaService.CompleteInGuild)) continue;
            if (Satisfied(def, entry)) Complete(def, entry);
        }
    }

 /// <summary>
 /// Auto-accepts the Kharas that a first event of <paramref name="completeType"/> about <paramref name="subject"/> starts.
 /// Each one gets an unsolicited 2574 and the accept rules, before the event itself is counted.
 /// </summary>
    internal void AutoAcceptOn(byte completeType, uint subject)
    {
        foreach (var def in _catalog.WithRow(completeType, subject))
        {
            if (!AutoEligible(State, def, _facts)) continue;
            Frames.Add(new KharaFrame(ZoneS2COpcodes.AnsKharaAccept, KharaService.BuildAnsKharaAccept2574(def.Id, 0)));
            Accept(def.Id);
        }
    }

 /// <summary>
 /// Whether an event of <paramref name="completeType"/> about <paramref name="subject"/> would auto-accept a Khara in
 /// <paramref name="state"/>. Uses the same test as <see cref="AutoAcceptOn"/>.
 /// </summary>
    internal static bool AnyAutoCandidate(KharaCatalog catalog, KharaStateDocument state, byte completeType, uint subject,
        KharaFacts facts)
    {
        foreach (var def in catalog.WithRow(completeType, subject))
            if (AutoEligible(state, def, facts)) return true;
        return false;
    }

 /// <summary>
 /// Whether the character may auto-accept <paramref name="def"/>: it is an Auto_Challenge Khara the character neither holds
 /// nor completed, its level window and class mask allow it, its profession matches, and a quest-gated Khara's quest is completed.
 /// </summary>
    internal static bool AutoEligible(KharaStateDocument state, KharaDefinition def, KharaFacts facts)
        => def.AutoChallenge
           && state.FindActive(def.Id) is null && !state.IsCompleted(def.Id) && def.LevelAllows(facts.Level)
           && def.JobAllows(facts.CombatClass)
           && (def.Main.PC_ProJob == 0 || def.Main.PC_ProJob == facts.Profession)
           && (def.GateQuest is not { } gate || facts.IsQuestCompleted(gate));

 /// <summary>Number of completed Kharas whose main row grants a title.</summary>
    internal uint TitledCompletedCount()
    {
        uint n = 0;
        foreach (var id in State.Completed)
            if (_catalog.Get(id)?.Main.Reward_Title_Id is byte t && t != 0) n++;
        return n;
    }

 /// <summary>
 /// Sets the title-count rows of an accepted Khara to the number of titled completions, up to the target. Sends a 2577 for
 /// each row that changed and is still below its target.
 /// </summary>
    private bool RefreshTitleRows(KharaDefinition def, KharaActiveDocument entry)
    {
        bool moved = false;
        uint titled = TitledCompletedCount();
        for (int i = 0; i < def.Conditions.Count; i++)
        {
            var c = def.Conditions[i];
            if (c.Complete_Type != KharaService.CompleteTitles) continue;
            uint target = KharaService.RowTarget(c);
            uint count = Math.Min(titled, target);
            if (count == CountOf(entry, i)) continue;
            SetCount(entry, i, count);
            if (count < target) Emit2577(def.Id, i, 0, count);
            moved = true;
        }
        return moved;
    }

 /// <summary>
 /// Completes an accepted Khara whose every condition now holds. Returns false, with nothing sent, when the Khara is not
 /// accepted or a condition is still missing.
 /// </summary>
    public bool TryComplete(uint kharaId)
    {
        var entry = State.FindActive(kharaId);
        var def = _catalog.Get(kharaId);
        if (entry is null || def is null || !Satisfied(def, entry)) return false;
        Complete(def, entry);
        return true;
    }

 /// <summary>
 /// Sends the completion messages for a Khara, adds its reward to the balance, and moves it to the completed list. Then
 /// completes every accepted Khara that this completion finishes.
 /// </summary>
 /// <remarks>
 /// The reward is the main row's Reward_KHARA_Point.
 /// </remarks>
    private void Complete(KharaDefinition def, KharaActiveDocument entry)
    {
        var pending = new Queue<(KharaDefinition Def, KharaActiveDocument Entry)>();
        pending.Enqueue((def, entry));
        while (pending.Count > 0)
        {
            var (d, e) = pending.Dequeue();
            if (!State.Active.Remove(e)) continue;
            InsertCompleted(d.Id);
            uint reward = d.Main.Reward_KHARA_Point;
            State.Points = (uint)Math.Min(uint.MaxValue, (ulong)State.Points + reward);
            Changed = true;
            Frames.Add(new KharaFrame(ZoneS2COpcodes.NfyKharaComplete, KharaService.BuildNfyKharaComplete2575(d.Id)));
            Frames.Add(new KharaFrame(ZoneS2COpcodes.NfyKharaReward, KharaService.BuildNfyKharaReward2576(reward)));

            foreach (var other in State.Active)
            {
                var od = _catalog.Get(other.KharaId);
                if (od is null || !od.HasCondition(KharaService.CompleteKharaDone, d.Id)) continue;
                if (pending.Any(p => ReferenceEquals(p.Entry, other))) continue;
                if (Satisfied(od, other)) pending.Enqueue((od, other));
            }

            
            
            
            if (d.Main.Reward_Title_Id != 0)
            {
                foreach (var other in State.Active)
                {
                    var od = _catalog.Get(other.KharaId);
                    if (od is null || !od.Conditions.Any(c => c.Complete_Type == KharaService.CompleteTitles)) continue;
                    if (pending.Any(p => ReferenceEquals(p.Entry, other))) continue;
                    if (Satisfied(od, other)) pending.Enqueue((od, other));
                    else RefreshTitleRows(od, other);
                }
            }
        }
    }

 /// <summary>
 /// Whether every condition of an accepted Khara holds now. Held-item rows are judged on the items the character holds at
 /// this moment. The rows are refreshed first, with a 2577 for each that changed.
 /// </summary>
    private bool Satisfied(KharaDefinition def, KharaActiveDocument entry)
    {
        RefreshHeldRows(def, entry, onlyItem: null);
        if (def.Conditions.Count == 0) return false;
        for (int i = 0; i < def.Conditions.Count; i++)
        {
            var c = def.Conditions[i];
            switch (c.Complete_Type)
            {
                case KharaService.CompleteKillNpc:
                case KharaService.CompleteHoldItem:   
                    if (CountOf(entry, i) < c.Complete_Type_Value2) return false;
                    break;
                case KharaService.CompleteQuestDone:
                    if (!_facts.IsQuestCompleted(c.Complete_Type_Value1)) return false;
                    break;
                case KharaService.CompleteKharaDone:
                    if (!State.IsCompleted(c.Complete_Type_Value1)) return false;
                    break;
                case KharaService.CompleteUseItem:
                case KharaService.CompleteDeath:
                case KharaService.CompleteRevived:
                case KharaService.CompleteHarvest:
                case KharaService.CompleteUseSkill:
                case KharaService.CompleteGuardianSummon:
                case KharaService.CompleteRepairCount:
                    if (CountOf(entry, i) < KharaService.RowTarget(c)) return false;   
                    break;
                case KharaService.CompleteBaseLevel:
                    if (_facts.Level < c.Complete_Type_Value1) return false;   
                    break;
                case KharaService.CompleteProfessionLevel:
                    if (_facts.ProfessionLevel < c.Complete_Type_Value1) return false;
                    break;
                case KharaService.CompleteTitles:
                    if (TitledCompletedCount() < KharaService.RowTarget(c)) return false;   
                    break;
                case KharaService.CompleteInGuild:
                    if (!_facts.InGuild) return false;
                    break;
                default:
                    return false;   
            }
        }
        return true;
    }

 /// <summary>
 /// Refreshes the held-item rows of an accepted Khara, all of them or only those for <paramref name="onlyItem"/>, to the
 /// number held, up to the target. Sends a 2577 for each row that changed. Returns true when a row changed.
 /// </summary>
    private bool RefreshHeldRows(KharaDefinition def, KharaActiveDocument entry, uint? onlyItem)
    {
        bool moved = false;
        for (int i = 0; i < def.Conditions.Count; i++)
        {
            var c = def.Conditions[i];
            if (c.Complete_Type != KharaService.CompleteHoldItem) continue;
            if (onlyItem is { } item && c.Complete_Type_Value1 != item) continue;
            var held = HeldTarget(c);
            if (held == CountOf(entry, i)) continue;
            SetCount(entry, i, held);
            Emit2577(def.Id, i, c.Complete_Type_Value1, held);
            moved = true;
        }
        return moved;
    }

    private uint HeldTarget(KharaInfo c)
    {
        long held = Math.Max(0, _facts.HeldCount(c.Complete_Type_Value1));
        return (uint)Math.Min(held, c.Complete_Type_Value2);
    }

    private static uint CountOf(KharaActiveDocument entry, int row) => row < entry.Counts.Count ? entry.Counts[row] : 0;

 /// <summary>
 /// Stores a row's progress and marks the state as changed, so the change is saved and published.
 /// </summary>
    private void SetCount(KharaActiveDocument entry, int row, uint count)
    {
        while (entry.Counts.Count <= row) entry.Counts.Add(0);
        if (entry.Counts[row] == count) return;
        entry.Counts[row] = count;
        Changed = true;
    }

    private void Emit2577(uint kharaId, int row, uint subject, uint count) =>
        Frames.Add(new KharaFrame(ZoneS2COpcodes.NfyKharaInComplete,
            KharaService.BuildNfyKharaInComplete2577(kharaId, (byte)row, subject, (ushort)Math.Min(count, ushort.MaxValue))));

    private void InsertActive(KharaActiveDocument entry)
    {
        int at = State.Active.FindIndex(a => a.KharaId > entry.KharaId);
        if (at < 0) State.Active.Add(entry);
        else State.Active.Insert(at, entry);
    }

    private void InsertCompleted(uint kharaId)
    {
        int at = State.Completed.BinarySearch(kharaId);
        if (at < 0) State.Completed.Insert(~at, kharaId);
    }
}

/// <summary>
/// Server-side Khara progress and completion for each character. Progress is persisted through <see cref="IKharaStateRepository"/>.
/// </summary>
/// <remarks>
/// Triggers are wired from the game systems: accepting a Khara, kills, held-item changes, quest completion, item use, death,
/// revival, harvest, skill use, Guardian summons, repairs, level changes and guild joins. Each trigger sends its messages
/// through <see cref="KharaEngine"/>. The client never requests completion itself.
/// </remarks>
public static class KharaService
{
 /// <summary>Accepted-Khara state value for a Khara in progress.</summary>
    public const uint StateInProgress = KharaActiveDocument.StateInProgress;

    public const byte CompleteKillNpc = 2;
    public const byte CompleteHoldItem = 4;
    public const byte CompleteQuestDone = 13;
    public const byte CompleteKharaDone = 40;

 /// <summary>Khara_Info Condition_Type of a main row gated on a completed quest.</summary>
    public const byte ConditionQuestDone = 8;

 /// <summary>Complete_Type of a row that counts item uses.</summary>
    public const byte CompleteUseItem = 6;
 /// <summary>Complete_Type of a row that counts titled completions.</summary>
    public const byte CompleteTitles = 22;
 /// <summary>Complete_Type of a row that counts deaths.</summary>
    public const byte CompleteDeath = 26;
 /// <summary>Complete_Type of a row that counts revivals.</summary>
    public const byte CompleteRevived = 27;
 /// <summary>Complete_Type of a row that counts harvests.</summary>
    public const byte CompleteHarvest = 29;
 /// <summary>Complete_Type of a row that counts skill uses by skill group.</summary>
    public const byte CompleteUseSkill = 31;
 /// <summary>Complete_Type of a row that counts Guardian summons.</summary>
    public const byte CompleteGuardianSummon = 32;
 /// <summary>Complete_Type of a row that requires guild membership.</summary>
    public const byte CompleteInGuild = 43;
 /// <summary>Complete_Type of a row that requires a base level.</summary>
    public const byte CompleteBaseLevel = 14;
 /// <summary>Complete_Type of a row that requires a profession level.</summary>
    public const byte CompleteProfessionLevel = 15;
 /// <summary>Complete_Type of a row that counts item repairs.</summary>
    public const byte CompleteRepairCount = 30;

 /// <summary>True for the level Complete_Types, which are checked on level change.</summary>
    public static bool IsLevelRow(byte completeType) => completeType is CompleteBaseLevel or CompleteProfessionLevel;

 /// <summary>
 /// Subject that a condition row counts, as carried by the 2577 message: the item for Complete_Type 6, 0 for the types without a
 /// subject, and the row's Complete_Type_Value1 otherwise.
 /// </summary>
    public static uint RowSubject(KharaInfo c) => c.Complete_Type switch
    {
        CompleteUseItem => c.Complete_Type_Value2,
        CompleteTitles or CompleteDeath or CompleteRevived or CompleteHarvest or CompleteGuardianSummon or CompleteInGuild
            or CompleteBaseLevel or CompleteProfessionLevel or CompleteRepairCount => 0,
        _ => c.Complete_Type_Value1,
    };

 /// <summary>Count a condition row must reach to be met.</summary>
    public static uint RowTarget(KharaInfo c) => c.Complete_Type switch
    {
        CompleteUseItem => c.Complete_Type_Value3,
        CompleteKillNpc or CompleteHoldItem or CompleteUseSkill => c.Complete_Type_Value2,
        _ => c.Complete_Type_Value1,
    };

 /// <summary>Active_Type of an action run when a Khara is accepted.</summary>
    public const byte ActiveOnAccept = 1;

 /// <summary>Action_Type of the accept action that spends Khara points.</summary>
    public const byte ActionSpendPoints = 27;

 /// <summary>Fail_Type of a time-limited Khara, whose limit is given in Fail_Type_Value1.</summary>
    public const byte FailTypeTimeLimit = 38;

 /// <summary>True when the Khara is time-limited.</summary>
    public static bool IsTimed(KharaDefinition def)
        => def.Main.Fail_Type == FailTypeTimeLimit || def.Conditions.Any(c => c.Fail_Type == FailTypeTimeLimit);

    private static readonly ConcurrentDictionary<uint, KharaStateDocument> Published = new();
    private static readonly ConcurrentDictionary<uint, SemaphoreSlim> Gates = new();

    
 /// <summary>Builds the 2574 AnsKharaAccept message: the Khara id and the result code.</summary>
    public static byte[] BuildAnsKharaAccept2574(uint kharaId, ushort result)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), result);
        return b;
    }

 /// <summary>Builds the 2575 NfyKharaComplete message: the Khara id.</summary>
    public static byte[] BuildNfyKharaComplete2575(uint kharaId)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        return b;
    }

 /// <summary>Builds the 2576 NfyKharaReward message: the points awarded.</summary>
    public static byte[] BuildNfyKharaReward2576(uint points)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, points);
        return b;
    }

 /// <summary>
 /// Builds the 2577 NfyKharaInComplete message: the progress of one condition row of a Khara.
 /// </summary>
 /// <remarks>
 /// Layout: the Khara id, the row index, the subject, and the count.
 /// </remarks>
    public static byte[] BuildNfyKharaInComplete2577(uint kharaId, byte row, uint subject, ushort count)
    {
        var b = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        b[4] = row;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), subject);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(9), count);
        return b;
    }

 /// <summary>Builds the 2580 AnsKharaTimerStart message: the Khara id, which starts the time-limit window.</summary>
    public static byte[] BuildAnsKharaTimerStart2580(uint kharaId)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, kharaId);
        return b;
    }

 /// <summary>Builds the 2502 OnPcKharaPointChangeNotify message: the character's new Khara point balance.</summary>
    public static byte[] BuildPointChange2502(uint balance)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, balance);
        return b;
    }

 /// <summary>Builds the 2858 NfyDeleteAcceptedKhara message, which removes the listed Khara ids from the accepted list.</summary>
 /// <remarks>
 /// An empty list builds the empty form, which the client ignores.
 /// </remarks>
    public static byte[] BuildNfyDeleteAcceptedKhara2858(IReadOnlyList<uint> kharaIds)
    {
        var b = new byte[4 + 2 + 4 * kharaIds.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(b.Length - 4));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)kharaIds.Count);
        for (int i = 0; i < kharaIds.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6 + 4 * i), kharaIds[i]);
        return b;
    }

    
 /// <summary>Loads the character's Khara state into the cache used by the login writer. Called at login.</summary>
    public static async Task PreloadAsync(uint characterId, IServiceProvider services, CancellationToken ct = default)
        => await GetPublishedAsync(characterId, Repository(services), ct);

 /// <summary>The cached Khara point balance of the character, or 0 when it has no state.</summary>
    public static uint PublishedPoints(uint characterId) =>
        Published.TryGetValue(characterId, out var doc) ? doc.Points : 0u;

 /// <summary>The ids of the Kharas the character has accepted and not completed, in ascending order.</summary>
    public static IReadOnlyList<uint> PublishedActiveIds(uint characterId) =>
        Published.TryGetValue(characterId, out var doc)
            ? doc.Active.Select(a => a.KharaId).Order().ToArray()
            : Array.Empty<uint>();

 /// <summary>
 /// Writes the accepted-Khara and completed-Khara lists of the 2310 login message from the cached state. Both are empty for a
 /// character without state.
 /// </summary>
    public static void WriteLoginVectors(PacketWriter writer, uint characterId, GameDataStore? data)
        => WriteLoginVectors(writer, Published.TryGetValue(characterId, out var doc) ? doc : null,
            data is null ? null : KharaCatalog.For(data));

 /// <summary>
 /// Writes the accepted-Khara and completed-Khara lists of the 2310 login message from a state document.
 /// </summary>
    public static void WriteLoginVectors(PacketWriter writer, KharaStateDocument? state, KharaCatalog? catalog)
    {
        var active = state?.Active ?? new List<KharaActiveDocument>();
        writer.WriteUInt16((ushort)active.Count);
        foreach (var a in active)
        {
            writer.WriteUInt32(a.KharaId);
            writer.WriteUInt32(a.State);
            writer.WriteUInt32(0);
            int rows = catalog?.Get(a.KharaId)?.Conditions.Count ?? a.Counts.Count;
            writer.WriteUInt16((ushort)rows);
            for (int i = 0; i < rows; i++)
                writer.WriteUInt32(i < a.Counts.Count ? a.Counts[i] : 0u);
        }
        var completed = state?.Completed ?? new List<uint>();
        writer.WriteUInt16((ushort)completed.Count);
        foreach (var id in completed)
            writer.WriteUInt32(id);
    }

    
 /// <summary>Handles a 1433 accept request: records the Khara and applies the accept rules.</summary>
    public static Task OnAcceptedAsync(ProudSession session, PlayerState player, uint kharaId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "accept",
            (state, catalog) => catalog.Get(kharaId) is not null && state.FindActive(kharaId) is null && !state.IsCompleted(kharaId),
            engine => engine.Accept(kharaId), ct);

 /// <summary>Handles a kill credited to the player.</summary>
    public static Task OnNpcKilledAsync(ProudSession session, PlayerState player, uint npcId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "kill",
            (state, catalog) => AnyActive(state, catalog, CompleteKillNpc, npcId)
                || KharaEngine.AnyAutoCandidate(catalog, state, CompleteKillNpc, npcId, CheapFactsOf(player)),
            engine => engine.OnNpcKilled(npcId), ct);

 /// <summary>
 /// Handles a change in the player's held count of <paramref name="itemId"/>. Call it after the inventory write and before the
 /// item's own message.
 /// </summary>
    public static Task OnHeldItemChangedAsync(ProudSession session, PlayerState player, uint itemId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "held-item",
            (state, catalog) => AnyActive(state, catalog, CompleteHoldItem, itemId)
                || KharaEngine.AnyAutoCandidate(catalog, state, CompleteHoldItem, itemId, CheapFactsOf(player)),
            engine => engine.OnHeldCountChanged(itemId), ct);

 /// <summary>Handles a quest the player completed. Call it at the end of quest reward payment.</summary>
    public static Task OnQuestCompletedAsync(ProudSession session, PlayerState player, uint questId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "quest",
            (state, catalog) => catalog.ReferencingQuest(questId).Any(d =>
                    state.FindActive(d.Id) is not null || KharaEngine.AutoEligible(state, d, CheapFactsOf(player)))
                
                
                || catalog.GatedByQuest(questId).Any(d => state.FindActive(d.Id) is null && !state.IsCompleted(d.Id)),
            engine => engine.OnQuestCompleted(questId), ct);

 /// <summary>Handles an item the player used up. Call it after the item is consumed and before its use notification.</summary>
    public static Task OnItemUsedAsync(ProudSession session, PlayerState player, uint itemId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteUseItem, itemId, "item-use", services, log, ct);

 /// <summary>Handles the player's death. Call it after the death notifications.</summary>
    public static Task OnDeathAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteDeath, 0, "death", services, log, ct);

 /// <summary>Handles the player's revival.</summary>
    public static Task OnRevivedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteRevived, 0, "revival", services, log, ct);

 /// <summary>Handles a harvest by the player.</summary>
    public static Task OnHarvestAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteHarvest, 0, "harvest", services, log, ct);

 /// <summary>Handles a skill that the player landed. Call it after the skill's own messages.</summary>
    public static Task OnSkillUsedAsync(ProudSession session, PlayerState player, uint skillId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteUseSkill, skillId / 100, "skill-use", services, log, ct);

 /// <summary>Handles the player summoning its Guardian.</summary>
    public static Task OnGuardianSummonedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => OnCountedAsync(session, player, CompleteGuardianSummon, 0, "guardian", services, log, ct);

 /// <summary>Handles the player joining a guild.</summary>
    public static Task OnGuildJoinedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "guild-join",
            (state, catalog) => AnyActive(state, catalog, CompleteInGuild, 0),
            engine => engine.OnGuildJoined(), ct);

 /// <summary>
 /// Handles a base level or profession level change. Call it at the end of the level-up messages, after the player's level is updated.
 /// </summary>
    public static Task OnLevelChangedAsync(ProudSession session, PlayerState player,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "level",
            (state, catalog) => AnyActive(state, catalog, CompleteBaseLevel, 0) || AnyActive(state, catalog, CompleteProfessionLevel, 0),
            engine => engine.OnLevelChanged(), ct);

 /// <summary>Handles an NPC repair of <paramref name="itemCount"/> items. Call it after the repair's result message.</summary>
    public static Task OnRepairedAsync(ProudSession session, PlayerState player, int itemCount,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => itemCount <= 0
            ? Task.CompletedTask
            : OnCountedAsync(session, player, CompleteRepairCount, 0, (uint)itemCount, "repair", services, log, ct);

    private static Task OnCountedAsync(ProudSession session, PlayerState player, byte completeType, uint subject,
        string trigger, IServiceProvider services, ILogger? log, CancellationToken ct)
        => OnCountedAsync(session, player, completeType, subject, 1, trigger, services, log, ct);

    private static Task OnCountedAsync(ProudSession session, PlayerState player, byte completeType, uint subject,
        uint amount, string trigger, IServiceProvider services, ILogger? log, CancellationToken ct)
        => RunAsync(session, player, services, log, trigger,
            (state, catalog) => AnyActive(state, catalog, completeType, subject)
                || KharaEngine.AnyAutoCandidate(catalog, state, completeType, subject, CheapFactsOf(player)),
            engine => engine.OnCountedEvent(completeType, subject, amount), ct);

 /// <summary>Handles a 1437 completion request: completes the Khara when it is accepted and every condition is met.</summary>
    public static Task RequestCompletionAsync(ProudSession session, PlayerState player, uint kharaId,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
        => RunAsync(session, player, services, log, "complete-request",
            (state, _) => state.FindActive(kharaId) is not null,
            engine =>
            {
                if (!engine.TryComplete(kharaId))
                    log?.LogInformation("{Name}: 1437 for Khara {Id} refused, a condition is not met", player.Name, kharaId);
            }, ct);

 /// <summary>
 /// Removes the accepted Kharas bound to another profession after a profession change, and returns the removed ids in ascending order.
 /// </summary>
 /// <remarks>
 /// A Khara is profession-bound when its main row names a profession. Removal is applied to the accepted list only; completed
 /// Kharas are kept. The removed ids are sent in the 2858 message.
 /// </remarks>
    public static async Task<IReadOnlyList<uint>> DropProfessionKharasAsync(PlayerState player, byte newProfession,
        IServiceProvider services, ILogger? log, CancellationToken ct = default)
    {
        try
        {
            var catalog = KharaCatalog.For(services.GetRequiredService<GameDataStore>());
            var repo = Repository(services);
            if (OtherProfessionKharas(await GetPublishedAsync(player.CharacterId, repo, ct), catalog, newProfession).Count == 0)
                return Array.Empty<uint>();

            var gate = Gates.GetOrAdd(player.CharacterId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var current = await GetPublishedAsync(player.CharacterId, repo, ct);
                var removed = OtherProfessionKharas(current, catalog, newProfession);
                if (removed.Count == 0) return removed;

                var next = current.Clone();
                next.Active.RemoveAll(a => removed.Contains(a.KharaId));
                next.UpdatedUtc = DateTime.UtcNow;
                try { await repo.SaveAsync(next, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.LogError(ex, "Khara state of character {Char} not saved (profession change)", player.CharacterId);
                }
                Published[player.CharacterId] = next;
                log?.LogInformation("{Name}: profession change to {Profession} drops accepted Khara(s) {Ids}",
                    player.Name, newProfession, string.Join(",", removed));
                return removed;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Khara profession change for character {Char} failed; nothing removed", player.CharacterId);
            return Array.Empty<uint>();
        }
    }

 /// <summary>
 /// Accepted Kharas whose main row names a profession other than <paramref name="profession"/>, in ascending order.
 /// </summary>
    internal static List<uint> OtherProfessionKharas(KharaStateDocument state, KharaCatalog catalog, byte profession)
    {
        var ids = new List<uint>();
        foreach (var a in state.Active)
            if (catalog.Get(a.KharaId)?.Main.PC_ProJob is byte p && p != 0 && p != profession)
                ids.Add(a.KharaId);
        ids.Sort();
        return ids;
    }

    
    private static bool AnyActive(KharaStateDocument state, KharaCatalog catalog, byte completeType, uint subject)
    {
        foreach (var a in state.Active)
            if (catalog.Get(a.KharaId) is { } d && d.Conditions.Any(c => c.Complete_Type == completeType && RowSubject(c) == subject))
                return true;
        return false;
    }

 /// <summary>The character's profession for the auto-accept check, or 0 when there is no character snapshot.</summary>
    private static byte ProfessionOf(PlayerState player) =>
        player.Character is { } c ? CharacterHeadRules.CreationProfessionByte(c) : (byte)0;

 /// <summary>The character's combat class for the class-mask check, or 0 when there is no character snapshot.</summary>
    private static byte CombatClassOf(PlayerState player) =>
        player.Character is { } c ? CharacterHeadRules.CombatJobByte(c) : (byte)0;

 /// <summary>Character facts for the auto-accept check that need no database reads.</summary>
    private static KharaFacts CheapFactsOf(PlayerState player) => new()
    {
        Level = player.Level,
        Profession = ProfessionOf(player),
        CombatClass = CombatClassOf(player),
    };

    private static IKharaStateRepository Repository(IServiceProvider services) =>
        services.GetRequiredService<IKharaStateRepository>();

    private static async Task<KharaStateDocument> GetPublishedAsync(uint characterId, IKharaStateRepository repo, CancellationToken ct)
    {
        if (Published.TryGetValue(characterId, out var cached)) return cached;
        var loaded = await repo.GetAsync(characterId, ct) ?? new KharaStateDocument { Id = characterId };
        Normalize(loaded, characterId);
        return Published.GetOrAdd(characterId, loaded);
    }

 /// <summary>Restores the sorted, disjoint lists that the engine relies on in a loaded document.</summary>
    private static void Normalize(KharaStateDocument doc, uint characterId)
    {
        doc.Id = characterId;
        doc.Completed = doc.Completed.Distinct().OrderBy(x => x).ToList();
        doc.Active = doc.Active
            .Where(a => doc.Completed.BinarySearch(a.KharaId) < 0)
            .GroupBy(a => a.KharaId).Select(g => g.First())
            .OrderBy(a => a.KharaId).ToList();
    }

    private static async Task<KharaFacts> LoadFactsAsync(PlayerState player, IServiceProvider services)
    {
        var held = new Dictionary<uint, int>();
        try
        {
            var items = await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(player.CharacterId);
            foreach (var it in items)
            {
                
                
                
                if (it.StorageType > 1) continue;
                held[it.ItemId] = held.GetValueOrDefault(it.ItemId) + Math.Max(0, it.Count);
            }
        }
        catch {   }

        HashSet<uint> quests;
        try
        {
            quests = new HashSet<uint>(await services.GetRequiredService<IQuestRepository>()
                .GetCompletedQuestIdsAsync(player.CharacterId));
        }
        catch { quests = new HashSet<uint>(); }

        bool inGuild;
        try
        {
            await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
            inGuild = GuildService.Instance.GetByCharId(player.CharacterId) is not null;
        }
        catch { inGuild = false; }

        return new KharaFacts
        {
            Level = player.Level,
            HeldCount = id => held.GetValueOrDefault(id),
            IsQuestCompleted = quests.Contains,
            InGuild = inGuild,
            Profession = ProfessionOf(player),
            CombatClass = CombatClassOf(player),
            ProfessionLevel = player.Character?.JobLevel ?? 0,
        };
    }

    private static async Task RunAsync(ProudSession session, PlayerState player, IServiceProvider services, ILogger? log,
        string trigger, Func<KharaStateDocument, KharaCatalog, bool> relevant, Action<KharaEngine> apply, CancellationToken ct)
    {
        try
        {
            var catalog = KharaCatalog.For(services.GetRequiredService<GameDataStore>());
            var repo = Repository(services);
            if (!relevant(await GetPublishedAsync(player.CharacterId, repo, ct), catalog)) return;

            var gate = Gates.GetOrAdd(player.CharacterId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var current = await GetPublishedAsync(player.CharacterId, repo, ct);
                if (!relevant(current, catalog)) return;

                var engine = new KharaEngine(catalog, current.Clone(), await LoadFactsAsync(player, services));
                apply(engine);
                if (engine.Changed)
                {
                    engine.State.UpdatedUtc = DateTime.UtcNow;
                    try { await repo.SaveAsync(engine.State, ct); }
                    catch (Exception ex)
                    {
                        
                        
                        log?.LogError(ex, "Khara state of character {Char} not saved ({Trigger})", player.CharacterId, trigger);
                    }
                    Published[player.CharacterId] = engine.State;
                }
                foreach (var f in engine.Frames)
                    await session.SendRmiAsync(f.Opcode, f.Body, ct);
                if (engine.Frames.Count > 0)
                    log?.LogDebug("{Name}: Khara {Trigger} sent {N} message(s)", player.Name, trigger, engine.Frames.Count);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Khara {Trigger} for character {Char} failed", trigger, player.CharacterId);
        }
    }
}
