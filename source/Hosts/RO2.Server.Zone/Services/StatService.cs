using System;
using System.Collections.Generic;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;
using RO2.Skills.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Computes a character's stat bonuses from its worn equipment and packs them for the 2498 OnPcTrait
/// sheet. The server is authoritative: it sums the per-item bonuses in <c>TraitInfo.json</c> and the
/// caller applies them to the character's base stats, so the sheet (and later, combat) reflects the real
/// gear regardless of what the client displays.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trait_Kind matters — do NOT sum every Value as flat.</b> A trait row is either FLAT
/// (<c>Trait_Kind == 1</c>: <c>Value</c> is an absolute amount, e.g. the "Faint Strength Rune" is
/// Kind 1 / Type 0 / Value 1 = <c>+1 STR</c>) or PERCENT (<c>Trait_Kind == 2</c>: <c>Value</c> is a
/// percentage stored ×100, so <c>Value 1000 = +10%</c>). The old cut summed both as flat, which turned a
/// Kind-2 <c>Value 1000</c> into <c>+1000</c> flat. They are kept separate here and combined by the
/// caller as <c>(base + flat) · (1 + percent/100)</c>.
/// </para>
/// <para>
/// Scope of this pass: only the innate per-item traits in <c>TraitInfo</c>. NOT modelled yet — socketed
/// runes (<c>ItemReinforceTraitInfo</c>, which need per-instance socket-content tracking), set bonuses
/// (<c>SetTrait</c>), cards, and the base derived-stat formulas. For the six PRIMARY stats
/// (Trait_Type 0..5 = STR/AGI/INT/WIS/VIT/LUK) the server knows the base, so percent is applied exactly.
/// For DERIVED stats (Trait_Type 23/25/26/58…) the server does not compute a base, so only the FLAT part
/// is applied and any Kind-2 percent on a derived stat is dropped (documented gap).
/// </para>
/// </remarks>
public static class StatService
{

    /// <summary>AoV DefenseRate denominator terms: Defense% = 100·Def/(Def + <see cref="DefenseRateAdd"/> +
    /// AttackerLv·<see cref="DefenseRateMulti"/>), capped 75%. Backed by data/PCBaseStatus.json fields
    /// DefenseAdd (870.0) and DefenseMulti (35.0), uniform across all class + monster rows, so there is one global
    /// pair — a per-row lookup would add zero fidelity. Named here (not inline literals) so their provenance is
    /// explicit and NpcCombatService, which has no GameDataStore, can share them.</summary>
    public const int DefenseRateAdd = 870;
    public const int DefenseRateMulti = 35;


    private static readonly Lock Lock = new();
    // ItemID -> [(Trait_Kind, Trait_Type, Value)] from the TraitInfo table.
    private static Dictionary<uint, List<(byte kind, byte type, float value)>>? _index;

    /// <summary>
    /// Lazily builds and caches the ItemID → [(Trait_Kind, Trait_Type, Value)] index from the loaded
    /// TraitInfo table. The GameDataStore is a process singleton loaded once at startup, so one index
    /// serves every request.
    /// </summary>
    private static Dictionary<uint, List<(byte, byte, float)>> ItemBonusIndex(GameDataStore store)
    {
        if (_index is not null) return _index;
        lock (Lock)
        {
            if (_index is not null) return _index;
            var idx = new Dictionary<uint, List<(byte, byte, float)>>();
            foreach (var t in store.TraitInfo)
            {
                if (!idx.TryGetValue(t.ItemID, out var list))
                    idx[t.ItemID] = list = new List<(byte, byte, float)>(2);
                list.Add((t.Trait_Kind, t.Trait_Type, t.Value));
            }
            _index = idx;
            return idx;
        }
    }

    /// <summary>
    /// Sums the worn (StorageType==1) items' bonuses by stat id, keeping FLAT (Kind 1) and PERCENT (Kind 2)
    /// separate: innate per-item <c>TraitInfo</c> PLUS <c>SetTrait</c> set bonuses (added when enough set
    /// members are worn). The result feeds <c>WorldEntryService.BuildPcTrait2498FromDonor</c>. These belong
    /// to the Basic side of the sheet (effective primaries + derived), never the allocated-Bonus column.
    /// (Socketed runes/cards would fold in here too once per-instance socket content is tracked.)
    /// </summary>
    public static EquipStats ComputeEquipmentStats(
        GameDataStore store, IEnumerable<InventoryItem> equipped)
    {
        var index = ItemBonusIndex(store);
        // Collect every contribution in Add-order; the Domain fold (EquipStats.Fold) does the Kind-2 percent /
        // Kind-1 flat accumulation. The list preserves order, so the float percent sum is byte-identical.
        var lines = new List<TraitLine>();
        void Add(byte kind, byte type, float value) => lines.Add(new TraitLine(kind, type, value));

        var runeIndex = RuneBonusIndex(store);
        var enchantIndex = EnchantBonusIndex(store);
        var refineIndex = RefineIndex(store);
        var cardIds = CardIdSet(store);
        var wornIds = new HashSet<uint>();
        foreach (var it in equipped)
        {
            if (it.StorageType != 1) continue;
            // INV-H2 stat cap: a worn row only counts when its template may stand in that cell (EquipRules.WornSlotsFor,
            // the same table the 1324/1346 equip legality uses). Slot legality, not actor level: this method has no actor
            // at most call sites. Without it a row planted in a worn cell it cannot occupy (a legacy or hand-edited row,
            // any path that bypassed CanPlace) would still add its TraitInfo/refine/socket/set bonuses. A template the
            // store does not know cannot be shown legal, so it counts for nothing.
            if (store.GetItem(it.ItemId) is not { } wornTemplate
                || !EquipRules.WornSlotsFor(wornTemplate).Contains(it.Slot))
                continue;
            // dur-0 keeps bonuses (P-INV32): a broken worn item (Durability 0) still contributes everything below;
            // whether it should drop out is not established, so behaviour is deliberately unchanged (INV-H10).
            wornIds.Add(it.ItemId);
            if (index.TryGetValue(it.ItemId, out var bonuses))
                foreach (var (kind, type, value) in bonuses)
                    Add(kind, type, value);
            // Socketed content (CardsJson = per-instance JSON array of item ids, 0 = empty). A socket holds
            // either a RUNE or a CARD; apply the matching bonus with rune-FIRST/else-card ordering so the 50
            // MVP cards that are ALSO ItemReinforce sources are not double-counted:
            //   - Rune: ItemReinforceTraitInfo bonus (Kind1 flat / Kind2 percent) — the existing path.
            //   - Card: its own TraitInfo rows (keyed by Card_ID; mostly Kind1 flat primaries, some Kind2 on
            //     derived stats). Without this ~890 socketed non-MVP cards contributed NOTHING (they are not
            //     ItemReinforce sources). Card ENFORCE (+N %) is a separate axis not applied here — it needs
            //     per-instance enforce-grade storage (CardsJson holds ids only); base card bonus works today.
            foreach (var socketId in ItemSockets.ParseSockets(it.CardsJson))
            {
                if (socketId == 0) continue;
                if (runeIndex.TryGetValue(socketId, out var rt))
                    foreach (var (kind, type, value) in rt) Add(kind, type, value);
                else if (cardIds.Contains(socketId) && index.TryGetValue(socketId, out var ct))
                    foreach (var (kind, type, value) in ct) Add(kind, type, value);
            }
            // Timed enchant (a Reinforce_Type-2 blessing, whetstone, oil or embroidery put on with 1543): the
            // ItemReinforceTraitInfo lines of its Reinforce_ID while it has seconds left (TimedEnchant). All 38
            // type-2 rows have lines, e.g. 30006 = Kind 2, Types 26 and 27, Value 1000 (+10%). A spent enchant stays
            // on the record with 0 seconds and gives nothing. Like any Kind-2 line on a derived stat, a percent on
            // 23/25/26/27 is still dropped by the 2498 builder (no derived base, see the class remarks).
            if (TimedEnchant.IsLive(it) && enchantIndex.TryGetValue(it.EnchantReinforceId, out var en))
                foreach (var (kind, type, value) in en) Add(kind, type, value);
            // Refine bonus (per-instance Refine level): REFINE_INFO keyed by
            // INDEX = Equip_Type*100000 + Grade*1000 + Refine (join column = item Grade). Val_Type IS the flat/percent
            // selector:
            //   Val_Type 0 (weapon min/max 96/97, magic-atk 25) = FLAT: add Trait_Val directly. One Trait_Val is added
            //     to BOTH trait 96 and 97, so the PSE rises by exactly Trait_Val.
            //   Val_Type 1 (armour Defense 26) = PERCENT of the item's OWN base stat: bonus =
            //     itemBase(Trait_Id0) × Trait_Val / 100, added flat (an armour piece with Def 550 and Trait_Val 271
            //     gains 1490). Folding it to a flat amount lands it in FlatOf(26), which the derived-Defense path consumes.
            // The client never computes any of this (no REFINE_INFO consumer) — it is server-authoritative and
            // the client only displays the final stat.
            if (it.Refine > 0 && store.GetItem(it.ItemId) is { } itemData
                && refineIndex.TryGetValue((uint)itemData.Equip_Type * 100000u + (uint)itemData.Grade * 1000u + it.Refine, out var rf))
            {
                if (rf.Val_Type == 0)   // FLAT (weapon / magic)
                {
                    Add(1, rf.Trait_Id0, rf.Trait_Val);
                    if (rf.Trait_Id1 != 999999u && rf.Trait_Id1 <= byte.MaxValue)
                        Add(1, (byte)rf.Trait_Id1, rf.Trait_Val);
                }
                else                    // PERCENT of the item's own base stat (armour Defense)
                {
                    float itemBase = 0f;
                    if (index.TryGetValue(it.ItemId, out var baseTraits))
                        foreach (var (bk, bt, bv) in baseTraits)
                            if (bt == rf.Trait_Id0) itemBase += bv;
                    int refineBonus = (int)(itemBase * rf.Trait_Val / 100.0);   // truncates, e.g. 1490.5 → 1490
                    if (refineBonus != 0) Add(1, rf.Trait_Id0, refineBonus);
                }
            }
        }

        // Set bonuses: for each set with a worn member, count worn members (SetItem OrItemID0..6 = the set's
        // items) and apply each SetTrait row whose member-count threshold is met. Membership comes from
        // SetItem, bonuses from SetTrait (using both would double-count the shared trait columns).
        var (setMembers, setTraits) = SetIndex(store);
        if (wornIds.Count > 0)
        {
            foreach (var (setId, members) in setMembers)
            {
                if (!setTraits.TryGetValue(setId, out var traits)) continue;
                int worn = 0;
                foreach (var id in members) if (wornIds.Contains(id)) worn++;
                if (worn == 0) continue;
                foreach (var (kind, type, value, count) in traits)
                    if (worn >= count) Add(kind, type, value);
            }
        }

        return EquipStats.Fold(lines);
    }

    /// <summary>
    /// The equipped weapon's damage range for the physical/magic damage formula: TraitInfo trait 96
    /// (Weapon Min Damage) and 97 (Weapon Max Damage), summed across worn gear by
    /// <see cref="ComputeEquipmentStats"/> (confirmed against TraitBasicInfo names). Returns (0,0) when
    /// no weapon is worn — the combat path treats that as bare fists and skips the weapon term.
    /// </summary>
    public static (int Min, int Max, int AttackRange) WeaponDamageRange(GameDataStore store, IEnumerable<InventoryItem> equipped)
    {
        var eq = ComputeEquipmentStats(store, equipped);
        // 96 = Weapon Min Damage, 97 = Weapon Max Damage, 42 = Auto_Attack_Range (bow ~20, melee ~2).
        return (eq.FlatOf(96), eq.FlatOf(97), eq.FlatOf(42));
    }


    /// <summary>
    /// Resolves a character's gear-adjusted combat inputs the SAME way the 2498 builder does: gear raises
    /// every stat THROUGH the primaries (TraitInfo Trait_Type 0..5 = STR/AGI/INT/WIS/VIT/LUK), so the
    /// effective VIT feeds VitHPValue and VitDefenseValue, the effective WIS feeds WisSPValue, and the
    /// effective STR/INT feed AP/MA. Defense (trait 26) is the naked <c>VitDefenseValue·VIT</c> PLUS the
    /// worn gear's flat armour (trait-26 Kind-1), exactly as <c>WorldEntryService</c> merges them onto the
    /// sheet — so combat DEF equals the DEF the player sees. Mirrors
    /// <c>WorldEntryService.BuildPcTrait2498FromDonor</c>'s primary→ComputeBaseStats path.
    /// </summary>
    public static EffectiveStats EffectivePlayerStats(
        GameDataStore store, Character c, IEnumerable<InventoryItem> equipped)
    {
        // The gear pool with the worn Khara titles' rows added (as the 2498 builder folds them), then the pure
        // derivation in Character.Domain over the class row.
        var gear = ComputeEquipmentStats(store, equipped);
        var eq = KharaTitleTraits.Fold(gear, store, c.Id) ?? gear;
        return CharacterStats.EffectivePlayerStats(c, eq, store.PcBaseStatus.GetValueOrDefault(c.Job));
    }

    /// <summary>
    /// The DEFENDER's effective armour after the attacker's Penetration (trait 94) chews through it, fed to
    /// the AoV DefenseRate. <c>Def_eff = Def · (1 − PenPct/100)</c>, <c>PenPct = min(50, Pen/(0.005·Pen +
    /// 0.2·AttackerLv + 10))</c> — the AoV expression is ALREADY a percentage (asymptote 1/0.005 = 200), so
    /// there is no extra ×100 (same scale convention as the Crit/Parry rates). The 50% ceiling is the data
    /// clamp (PCBaseStatus PenLimitValue = 0.5). Pen 0 (every naked character, and all NPCs, which carry no
    /// Pen) returns the armour unchanged — a strict no-op, so behaviour is byte-identical until Pen gear is worn.
    /// </summary>
    /// <remarks>The fold shape (reduce Def before DefenseRate) is the standard AoV armour-pen form but is
    /// not data-verified: it only becomes observable once Pen gear exists, and it cannot regress anything at Pen 0.</remarks>
    public static double PenetratedDef(int def, int pen, int attackerLevel)
    {
        if (pen <= 0 || def <= 0) return Math.Max(0, def);
        // The 0.005 / 0.2 / 10.0 constants are an AoV-style fold chosen here, not read from a data table.
        double penPct = Math.Min(50.0, pen / (0.005 * pen + 0.2 * Math.Max(1, attackerLevel) + 10.0));
        return def * (1.0 - penPct / 100.0);
    }

    /// <summary>
    /// The REAL skill cooldown after Vigor reduction — the value the server must both SEND to the client
    /// (2715 AnsUseSkillStart, which the client stores verbatim: it is the sole authority for the enforced
    /// cooldown) and enforce itself. AoV model:
    ///   cooldown = tableCooldown · (1 − VigorPct/100),  VigorPct = min(75, Vigor/(0.005·Vigor + 0.2·Level + 10)).
    /// (The AoV expression is already a percentage — asymptote 1/0.005 = 200 — so there is no extra ×100.)
    /// The 75% cap is a HARD floor of 0.25·table; the result is clamped to [0.25·table, table] so uncertainty in
    /// the interior constants can never push it past those bounds. A naked character (Vigor 0) gets no reduction
    /// (= table).
    /// </summary>
    public static float EffectiveCooldown(float tableCooldown, int level, int vigor)
    {
        if (tableCooldown <= 0f) return 0f;
        int v = Math.Max(0, vigor);
        double vigorPct = Math.Min(75.0, v / (0.005 * v + 0.2 * Math.Max(1, level) + 10.0));
        double reduced = tableCooldown * (1.0 - vigorPct / 100.0);
        return (float)Math.Clamp(reduced, tableCooldown * 0.25, tableCooldown);
    }

    /// <summary>
    /// The GEAR-ADJUSTED MaxHP/MaxSP for a character — the SAME value the 2498 sheet shows the client.
    /// Must be used wherever the runtime PlayerState.MaxHp/MaxMp is set, or the server caps HP at the NAKED
    /// max while the client shows the geared max — every hit's HP update then snaps the bar back to the
    /// naked value ("HP resets to base on each hit"). Thin projection of <see cref="EffectivePlayerStats"/>.
    /// </summary>
    public static (uint MaxHp, ushort MaxSp) EffectiveMaxHpSp(
        GameDataStore store, Character c, IEnumerable<InventoryItem> equipped)
    {
        var s = EffectivePlayerStats(store, c, equipped);
        return (s.MaxHp, s.MaxSp);
    }

    private static readonly Lock SetLock = new();
    private static Dictionary<uint, HashSet<uint>>? _setMembers;
    private static Dictionary<uint, List<(byte kind, byte type, float value, int count)>>? _setTraits;

    /// <summary>Lazily builds (Set_ID → member item ids) from <c>SetItem</c> and (Set_ID → trait rows) from
    /// <c>SetTrait</c>. Cached like the item index (the GameDataStore is a startup singleton).</summary>
    private static (Dictionary<uint, HashSet<uint>> members, Dictionary<uint, List<(byte, byte, float, int)>> traits) SetIndex(GameDataStore store)
    {
        if (_setMembers is not null && _setTraits is not null) return (_setMembers, _setTraits);
        lock (SetLock)
        {
            if (_setMembers is null || _setTraits is null)
            {
                var members = new Dictionary<uint, HashSet<uint>>();
                foreach (var s in store.SetItem)
                {
                    if (!members.TryGetValue(s.Set_ID, out var set))
                        members[s.Set_ID] = set = new HashSet<uint>();
                    foreach (var id in new[] { s.OrItemID0, s.OrItemID1, s.OrItemID2, s.OrItemID3, s.OrItemID4, s.OrItemID5, s.OrItemID6 })
                        if (id != 0) set.Add(id);
                }
                var traits = new Dictionary<uint, List<(byte, byte, float, int)>>();
                foreach (var t in store.SetTrait)
                {
                    if (!traits.TryGetValue(t.Set_ID, out var list))
                        traits[t.Set_ID] = list = new List<(byte, byte, float, int)>(2);
                    list.Add((t.Trait_Kind, t.Trait_Type, t.Value, t.Count));
                }
                _setMembers = members; _setTraits = traits;
            }
            return (_setMembers, _setTraits);
        }
    }

    private static readonly Lock RuneLock = new();
    private static Dictionary<uint, List<(byte kind, byte type, float value)>>? _runeIndex;

    /// <summary>(Reinforce_ID -> ItemReinforceTraitInfo rows) per store: the lines of a timed enchant, looked up by
    /// the id the item itself carries (InventoryItem.EnchantReinforceId) rather than by a source item.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameDataStore,
        Dictionary<ushort, List<(byte kind, byte type, float value)>>> EnchantIndexes = new();

    private static Dictionary<ushort, List<(byte kind, byte type, float value)>> EnchantBonusIndex(GameDataStore store)
        => EnchantIndexes.GetValue(store, s =>
        {
            var byId = new Dictionary<ushort, List<(byte, byte, float)>>();
            foreach (var t in s.ItemReinforceTraitInfo)
            {
                if (!byId.TryGetValue(t.Reinforce_ID, out var l))
                    byId[t.Reinforce_ID] = l = new List<(byte, byte, float)>(2);
                l.Add((t.Trait_Kind, t.Trait_Type, t.Value));
            }
            return byId;
        });

    /// <summary>Lazily builds (rune item id → trait rows): ItemReinforceInfo maps a SourceItem_ID (the rune)
    /// to a Reinforce_ID, and ItemReinforceTraitInfo maps that Reinforce_ID to (Trait_Kind, Trait_Type,
    /// Value) — same semantics as innate TraitInfo. Cached like the item/set indices.</summary>
    private static Dictionary<uint, List<(byte, byte, float)>> RuneBonusIndex(GameDataStore store)
    {
        if (_runeIndex is not null) return _runeIndex;
        lock (RuneLock)
        {
            if (_runeIndex is null)
            {
                var byReinforce = new Dictionary<uint, List<(byte, byte, float)>>();
                foreach (var t in store.ItemReinforceTraitInfo)
                {
                    if (!byReinforce.TryGetValue(t.Reinforce_ID, out var l))
                        byReinforce[t.Reinforce_ID] = l = new List<(byte, byte, float)>(2);
                    l.Add((t.Trait_Kind, t.Trait_Type, t.Value));
                }
                var bySource = new Dictionary<uint, List<(byte, byte, float)>>();
                foreach (var r in store.ItemReinforceInfo)
                {
                    if (r.SourceItem_ID == 0 || !byReinforce.TryGetValue(r.Reinforce_ID, out var traits)) continue;
                    if (!bySource.TryGetValue(r.SourceItem_ID, out var l))
                        bySource[r.SourceItem_ID] = l = new List<(byte, byte, float)>(traits.Count);
                    l.AddRange(traits);
                }
                _runeIndex = bySource;
            }
            return _runeIndex;
        }
    }

    private static readonly Lock RefineLock = new();
    private static Dictionary<uint, RO2.GameData.Tables.REFINEINFO>? _refineIndex;

    /// <summary>Lazily builds the REFINE_INFO lookup keyed by its composite <c>INDEX</c>
    /// (= Equip_Type_ID·100000 + MasteryGrade·1000 + Refine_GRADE, verified unique across all rows), so a
    /// worn item's (Equip_Type, Grade, Refine) resolves its refine trait bonus in O(1). Cached like the
    /// item/set/rune indices (the GameDataStore is a startup singleton).</summary>
    private static Dictionary<uint, RO2.GameData.Tables.REFINEINFO> RefineIndex(GameDataStore store)
    {
        if (_refineIndex is not null) return _refineIndex;
        lock (RefineLock)
        {
            if (_refineIndex is null)
            {
                var idx = new Dictionary<uint, RO2.GameData.Tables.REFINEINFO>(store.REFINEINFO.Count);
                foreach (var r in store.REFINEINFO) idx[r.INDEX] = r;
                _refineIndex = idx;
            }
            return _refineIndex;
        }
    }

    private static readonly Lock CardLock = new();
    private static HashSet<uint>? _cardIds;

    /// <summary>Lazily builds the set of CardInfo Card_IDs so a socketed id can be recognised as a CARD
    /// (vs a rune) and have its own TraitInfo bonus applied. Cached like the other indices.</summary>
    private static HashSet<uint> CardIdSet(GameDataStore store)
    {
        if (_cardIds is not null) return _cardIds;
        lock (CardLock)
        {
            if (_cardIds is null)
            {
                var s = new HashSet<uint>(store.CardInfo.Count);
                foreach (var c in store.CardInfo) s.Add(c.Card_ID);
                _cardIds = s;
            }
            return _cardIds;
        }
    }


    public static BaseStats ComputeBaseStats(
        GameDataStore store, byte job, int level, int str, int agi, int intel, int wis, int vit, int luk)
        => CharacterStats.ComputeBaseStats(store.PcBaseStatus.GetValueOrDefault(job), level, str, agi, intel, wis, vit, luk);

    /// <summary>
    /// Refunds all allocated bonus stat points: zeroes the per-stat allocation deltas and resets the
    /// effective primaries to the pure class base. Because the unspent pool is DERIVED
    /// (<see cref="CharacterStats.BonusStatPointsEarned"/> − sum of allocations), clearing the allocations makes the pool
    /// jump back to the full level curve automatically — no separate pool field to touch. Used by the
    /// stat-reset scroll (ItemInfo Item_Type 25) and by job-change resets (JobChange_Table Stat_Reset).
    /// Returns false (and changes nothing) when the class has no Standard_Stat row, so the caller can skip.
    /// The caller must mirror the primaries to the runtime PlayerState, persist, and re-push the 2498 sheet.
    /// </summary>
    public static bool ResetBonusStats(GameDataStore store, Character c)
        => CharacterStats.ResetBonusStats(c, ClassBasePrimaries(store, c.Job));

    /// <summary>
    /// A new character's starting primary stats for its class, read from the REAL <c>Standard_Stat</c>
    /// table (not a hardcoded per-job table). Column → game-stat mapping:
    /// STR=<c>Base_STR</c>, AGI=<c>Base_AGI</c>, INT=<c>Base_INT</c>, VIT=<c>Base_VIT</c>, LUK=<c>Base_LUK</c>,
    /// and WIS=<c>Base_DEX</c> — per the repo's own migration note (<c>Character.Wis</c> "was the legacy Dex
    /// column"). The four name-matched columns are unambiguous; the WIS←Base_DEX mapping follows that note.
    /// Returns <c>null</c> if the class has no Standard_Stat row (caller keeps its fallback).
    /// </summary>
    public static (int str, int agi, int intel, int wis, int vit, int luk)? ClassBasePrimaries(
        GameDataStore store, byte job)
        => CharacterStats.ClassBasePrimaries(store.StandardStats.GetValueOrDefault(job));

    // ===================== Bonus (allocatable) stat points =====================


    // ===================== Talent (skill) points =====================

    // There is deliberately no per-stat cap from Max_Status.json here (the MaxStatPerStat lookup that
    // read it had no caller left and was removed). Max_Status never exceeds 99 at any level, yet 1493
    // ReqUseBonusStatPoint is answered with result 0 (2680 blob+9) for much larger allocations, so that table does
    // not bound an allocation. What does bound one is the 0 that ends Status_Point (BonusStatCost below;
    // UseBonusStatPointHandler answers it with result 2).

    private static readonly Lock CostLock = new();
    private static Dictionary<int, byte>? _statCost;   // current stat value -> points to raise it by one

    /// <summary>
    /// Points required to raise a primary from <paramref name="currentStatValue"/> to +1, from
    /// <c>Status_Point.json</c> (<c>Curr_Stat -&gt; Require_Point</c>). Nearly always 1; the table caps out
    /// at 999 (Require_Point 0 = cannot raise further). Defaults to 1 when the value is off the table.
    /// </summary>
    public static int BonusStatCost(GameDataStore store, int currentStatValue)
    {
        var map = _statCost;
        if (map is null)
        {
            lock (CostLock)
            {
                if (_statCost is null)
                {
                    var m = new Dictionary<int, byte>(store.StatusPoint.Count);
                    foreach (var r in store.StatusPoint) m[r.Curr_Stat] = r.Require_Point;
                    _statCost = m;
                }
                map = _statCost;
            }
        }
        // The off-table cost of 1 is a server default; Status_Point.json has no row for this value.
        return CharacterStats.BonusStatCost(map, currentStatValue);
    }
}
