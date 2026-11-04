using RO2.GameData.Tables;

namespace RO2.Kharas.Domain;

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
 /// and completes it when every condition already holds. Returns false, with nothing sent, for an unknown, already
 /// accepted or completed, or not yet allowed (level, class, profession, gate quest) Khara.
 /// </summary>
    public bool Accept(uint kharaId)
    {
        var def = _catalog.Get(kharaId);
        if (def is null || State.FindActive(kharaId) is not null || State.IsCompleted(kharaId)) return false;
        if (!MeetsRequirements(def, _facts)) return false; // the 1433 manual accept comes through here too

        var entry = new KharaActiveDocument { KharaId = kharaId, State = KharaActiveDocument.StateInProgress };
        for (int i = 0; i < def.Conditions.Count; i++) entry.Counts.Add(0);
        InsertActive(entry);
        Changed = true;

        
        
        
        
        
        
        
        
        if (KharaRules.IsTimed(def))
            Frames.Add(new KharaFrame(KharaPackets.AnsKharaTimerStart, KharaPackets.BuildAnsKharaTimerStart2580(kharaId)));

        
        RefreshHeldRows(def, entry, onlyItem: null);
        
        
        RefreshTitleRows(def, entry);

        if (def.Main.Active_Type == KharaRules.ActiveOnAccept && def.Main.Action_Type == KharaRules.ActionSpendPoints
            && def.Main.Action_Value1 > 0)
        {
            State.Points = State.Points > def.Main.Action_Value1 ? State.Points - def.Main.Action_Value1 : 0;
            Frames.Add(new KharaFrame(KharaPackets.OnPcKharaPointChangeNotify, KharaPackets.BuildPointChange2502(State.Points)));
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
            Frames.Add(new KharaFrame(KharaPackets.AnsKharaAccept, KharaPackets.BuildAnsKharaAccept2574(def.Id, 0)));
            Accept(def.Id);
        }
        foreach (var def in _catalog.GatedByQuest(questId))
        {
            if (!AutoEligible(State, def, _facts)) continue;
            Frames.Add(new KharaFrame(KharaPackets.AnsKharaAccept, KharaPackets.BuildAnsKharaAccept2574(def.Id, 0)));
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
            if (def is null || !State.Active.Contains(entry) || !def.Conditions.Any(c => KharaRules.IsLevelRow(c.Complete_Type))) continue;
            if (Satisfied(def, entry)) Complete(def, entry);
        }
    }

 /// <summary>Handles a kill of <paramref name="npcId"/>: advances the matching kill rows and completes Kharas that are now met.</summary>
    public void OnNpcKilled(uint npcId)
    {
        AutoAcceptOn(KharaRules.CompleteKillNpc, npcId);
        foreach (var entry in State.Active.ToList())
        {
            var def = _catalog.Get(entry.KharaId);
            
            if (def is null || !State.Active.Contains(entry)) continue;
            bool stepped = false;
            for (int i = 0; i < def.Conditions.Count; i++)
            {
                var c = def.Conditions[i];
                if (c.Complete_Type != KharaRules.CompleteKillNpc || c.Complete_Type_Value1 != npcId) continue;
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
        
        
        if (_facts.HeldCount(itemId) is > 0) AutoAcceptOn(KharaRules.CompleteHoldItem, itemId);
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
                if (c.Complete_Type != completeType || KharaRules.RowSubject(c) != subject) continue;
                var count = CountOf(entry, i);
                var target = KharaRules.RowTarget(c);
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
            if (def is null || !State.Active.Contains(entry) || !def.Conditions.Any(c => c.Complete_Type == KharaRules.CompleteInGuild)) continue;
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
            Frames.Add(new KharaFrame(KharaPackets.AnsKharaAccept, KharaPackets.BuildAnsKharaAccept2574(def.Id, 0)));
            Accept(def.Id);
        }
    }

 /// <summary>
 /// Whether an event of <paramref name="completeType"/> about <paramref name="subject"/> would auto-accept a Khara in
 /// <paramref name="state"/>. Uses the same test as <see cref="AutoAcceptOn"/>.
 /// </summary>
    public static bool AnyAutoCandidate(KharaCatalog catalog, KharaStateDocument state, byte completeType, uint subject,
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
    public static bool AutoEligible(KharaStateDocument state, KharaDefinition def, KharaFacts facts)
        => def.AutoChallenge
           && state.FindActive(def.Id) is null && !state.IsCompleted(def.Id) && MeetsRequirements(def, facts);

 /// <summary>
 /// Whether the character meets the Khara's own requirements: level window, class mask, profession and gate quest.
 /// Shared by auto-accept and the manual accept.
 /// </summary>
    private static bool MeetsRequirements(KharaDefinition def, KharaFacts facts)
        => def.LevelAllows(facts.Level) && def.JobAllows(facts.CombatClass)
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
            if (c.Complete_Type != KharaRules.CompleteTitles) continue;
            uint target = KharaRules.RowTarget(c);
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
            Frames.Add(new KharaFrame(KharaPackets.NfyKharaComplete, KharaPackets.BuildNfyKharaComplete2575(d.Id)));
            Frames.Add(new KharaFrame(KharaPackets.NfyKharaReward, KharaPackets.BuildNfyKharaReward2576(reward)));

            foreach (var other in State.Active)
            {
                var od = _catalog.Get(other.KharaId);
                if (od is null || !od.HasCondition(KharaRules.CompleteKharaDone, d.Id)) continue;
                if (pending.Any(p => ReferenceEquals(p.Entry, other))) continue;
                if (Satisfied(od, other)) pending.Enqueue((od, other));
            }

            
            
            
            if (d.Main.Reward_Title_Id != 0)
            {
                foreach (var other in State.Active)
                {
                    var od = _catalog.Get(other.KharaId);
                    if (od is null || !od.Conditions.Any(c => c.Complete_Type == KharaRules.CompleteTitles)) continue;
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
                case KharaRules.CompleteKillNpc:
                case KharaRules.CompleteHoldItem:   
                    if (CountOf(entry, i) < c.Complete_Type_Value2) return false;
                    break;
                case KharaRules.CompleteQuestDone:
                    if (!_facts.IsQuestCompleted(c.Complete_Type_Value1)) return false;
                    break;
                case KharaRules.CompleteKharaDone:
                    if (!State.IsCompleted(c.Complete_Type_Value1)) return false;
                    break;
                case KharaRules.CompleteUseItem:
                case KharaRules.CompleteDeath:
                case KharaRules.CompleteRevived:
                case KharaRules.CompleteHarvest:
                case KharaRules.CompleteUseSkill:
                case KharaRules.CompleteGuardianSummon:
                case KharaRules.CompleteRepairCount:
                    if (CountOf(entry, i) < KharaRules.RowTarget(c)) return false;   
                    break;
                case KharaRules.CompleteBaseLevel:
                    if (_facts.Level < c.Complete_Type_Value1) return false;   
                    break;
                case KharaRules.CompleteProfessionLevel:
                    if (_facts.ProfessionLevel < c.Complete_Type_Value1) return false;
                    break;
                case KharaRules.CompleteTitles:
                    if (TitledCompletedCount() < KharaRules.RowTarget(c)) return false;   
                    break;
                case KharaRules.CompleteInGuild:
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
            if (c.Complete_Type != KharaRules.CompleteHoldItem) continue;
            if (onlyItem is { } item && c.Complete_Type_Value1 != item) continue;
            if (HeldTarget(c) is not { } held) continue; // inventory unknown: keep the stored progress
            if (held == CountOf(entry, i)) continue;
            SetCount(entry, i, held);
            Emit2577(def.Id, i, c.Complete_Type_Value1, held);
            moved = true;
        }
        return moved;
    }

    private uint? HeldTarget(KharaInfo c)
    {
        if (_facts.HeldCount(c.Complete_Type_Value1) is not { } n) return null;
        long held = Math.Max(0, n);
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
        Frames.Add(new KharaFrame(KharaPackets.NfyKharaInComplete,
            KharaPackets.BuildNfyKharaInComplete2577(kharaId, (byte)row, subject, (ushort)Math.Min(count, ushort.MaxValue))));

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
