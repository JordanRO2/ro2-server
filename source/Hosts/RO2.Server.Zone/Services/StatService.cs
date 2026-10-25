using System;
using System.Collections.Generic;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

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
        var flat = new Dictionary<byte, int>();
        var pct = new Dictionary<byte, float>();

        void Add(byte kind, byte type, float value)
        {
            if (kind == 2) // PERCENT: Value is ×100 (1000 = +10%)
                pct[type] = pct.GetValueOrDefault(type) + value / CharacterStats.PercentTraitDivisor;
            else           // FLAT (Kind 1, and any unexpected kind treated conservatively as flat)
                flat[type] = flat.GetValueOrDefault(type) + (int)Math.Round(value);
        }

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

        return new EquipStats(flat, pct);
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
        // The gear's pool with the worn Khara titles' rows added, the pool the 2498 builder folds them into
        // (KharaTitleTraits), so combat reads the sheet's stats.
        var gear = ComputeEquipmentStats(store, equipped);
        var eq = KharaTitleTraits.Fold(gear, store, c.Id) ?? gear;
        int P(int baseStat, byte traitType) => Math.Clamp(eq.Apply(traitType, baseStat), 0, ushort.MaxValue);
        int str = P(c.Str, 0), agi = P(c.Agi, 1), intel = P(c.Int, 2),
            wis = P(c.Wis, 3), vit = P(c.Vit, 4), luk = P(c.Luk, 5);
        var bs = ComputeBaseStats(store, c.Job, c.Level, str, agi, intel, wis, vit, luk);
        // Trait 21 / 22 Kind-1 rows add flat to the class max, as on the sheet (WorldEntryService.BuildPcTrait2498FromDonor).
        // A class with no PCBaseStatus row keeps its 0 max, as before.
        uint maxHp = bs.MaxHp > 0 ? (uint)Math.Clamp((long)bs.MaxHp + eq.FlatOf(21), 0, uint.MaxValue) : bs.MaxHp;
        ushort maxSp = bs.MaxHp > 0 ? (ushort)Math.Clamp(bs.MaxSp + eq.FlatOf(22), 0, ushort.MaxValue) : bs.MaxSp;
        // Effective Defense = naked derived DEF (VitDefenseValue·VIT) + worn gear's flat armour (trait 26),
        // the same merge the sheet performs. 0 when the class has no PCBaseStatus row and no armour is worn.
        int defense = bs.Derived.GetValueOrDefault((byte)26) + eq.FlatOf(26);
        // Effective Vigor (trait 62) drives skill-cooldown reduction. It has NO PCBaseStatus class
        // coefficient (gear/skill-driven only), so it is the summed worn-gear flat — 0 for a naked character.
        int vigor = eq.FlatOf(62);
        // Crit (trait 60, attacker roll) and Parry (trait 61, defender roll) are naked derived + gear flat,
        // the same merge as Defense. Penetration (trait 94) is gear-only (no class coefficient), like Vigor.
        int crit = bs.Derived.GetValueOrDefault((byte)60) + eq.FlatOf(60);
        int parry = bs.Derived.GetValueOrDefault((byte)61) + eq.FlatOf(61);
        int pen = eq.FlatOf(94);
        return new EffectiveStats(str, agi, intel, wis, vit, luk, maxHp, maxSp, defense, vigor, crit, parry, pen);
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
    {
        if (!store.PcBaseStatus.TryGetValue(job, out var pc))
            return new BaseStats(0, 0, new Dictionary<byte, int>());

        int lvl = Math.Max(1, level);
        long basePool = (long)Math.Floor((double)pc.BaseHP * pc.BaseHPRate * pc.BaseHPRateValue);
        long maxHp = basePool + (long)pc.BaseHPValue * (lvl - 1) + (long)pc.VitHPValue * vit;
        long maxSp = (long)pc.WisSPValue * wis;

        var d = new Dictionary<byte, int>
        {
            [23] = pc.APValue * str,                                   // Physical Attack   
            [25] = pc.MAValue * intel,                                 // Magic Attack      
            [26] = pc.VitDefenseValue * vit,                           // Defense (naked)   (+armor elsewhere)
            [60] = pc.AgiCrticalValue * agi,                           // Critical          
            [61] = pc.StrParryValue * str + pc.IntParryValue * intel,  // Parry / Guard     
            [59] = pc.AgiDodgeValue * agi,                             // Dodge / Evade     (base only; +1 level term omitted)
            [58] = pc.StrHitValue * str + pc.IntHitValue * intel,      // Hit / Precision   (partial; level term unresolved)
        };
        return new BaseStats(
            (uint)Math.Clamp(maxHp, 0, uint.MaxValue),
            (ushort)Math.Clamp(maxSp, 0, ushort.MaxValue),
            d);
    }

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
    {
        if (ClassBasePrimaries(store, c.Job) is not { } cb) return false;
        c.BonusStr = c.BonusAgi = c.BonusInt = c.BonusWis = c.BonusVit = c.BonusLuk = 0;
        c.Str = cb.str; c.Agi = cb.agi; c.Int = cb.intel;
        c.Wis = cb.wis; c.Vit = cb.vit; c.Luk = cb.luk;
        return true;
    }

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
    {
        if (!store.StandardStats.TryGetValue(job, out var s)) return null;
        return (s.Base_STR, s.Base_AGI, s.Base_INT, s.Base_DEX, s.Base_VIT, s.Base_LUK);
    }

    // ===================== Bonus (allocatable) stat points =====================


    // ===================== Talent (skill) points =====================

    /// <summary>
    /// The skills a class holds for free at level 1, which is what a skill reset leaves it with: the
    /// <c>Talent_Table</c> rows that <paramref name="job"/> may use (bit <c>job-1</c> of <c>Require_Job</c>),
    /// that are not the job-master skill (<c>ISJobMaster</c> 0) and that need no other skill (all three
    /// <c>Need_Skill_Group</c> 0), and of those only the ones at the lowest <c>Base_Level</c> among them.
    /// Ascending. Empty for a job with no such row (Novice).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A skill reset leaves a job with this set (covered: jobs 1, 3, 4, 6, 7, 9, 10, 12, 13, 16, 17 and 18). Among
    /// them: job 4 -> [4101, 4103, 4108]; job 18 -> [6301, 6309, 6317]; job 6 -> [1102, 1110, 1144], the Swordsman
    /// rows, whose Require_Job carries jobs 1, 6 and 7 (the job-master skill 1200, ISJobMaster 1, goes); and the
    /// quest job changes to 10 -> [3104, 3107, 3124] and 17 -> [6101, 6104, 6111], rows whose Job_ID is the base
    /// class (3, 16). The job-6 reset and the job-10 change also carry the profession skill 50300, which has no
    /// Talent_Table row and is not touched by a reset.
    /// </para>
    /// <para>
    /// The lowest-Base_Level cut is what job 9 needs, and job 9 is the only job whose candidates span two
    /// Base_Levels, so it changes no other job's set. Its candidates are the Acolyte rows 2100, 2102 and 2103
    /// (Base_Level 1, Require_Job 386 = jobs 2, 8 and 9) and its own 2312 (Job_ID 9, Require_Job 256, no
    /// prerequisite, Base_Level 25). A job-9 reset leaves [210001, 210201, 210301] with no 2312. Granting 2312 would
    /// add 231201 to the reset's 2488, seed it on every job-9 login and make its first level free. The cut is not
    /// "Base_Level at most the character's level", nor "Base_Level 1": jobs 11, 15 and 18 have only Base_Level-25
    /// candidates, and a job-18 reset keeps all three of [6301, 6309, 6317].
    /// </para>
    /// <para>
    /// Grouping the table by <c>Job_ID</c> instead gives the second classes 6-15 their job-master row (job 6:
    /// [1200]) and Crescentia (17) nothing, because their roots are the base class's rows; JobInfo's
    /// Base_Job_ID is 0 on every row, so it cannot walk them back. The mask also keeps apart what the
    /// Job_ID grouping cannot say: the Archer rows name jobs 3 and 10 but not Beastmaster (11), and the
    /// Alter rows 16 and 17 but not Soul Maker (18), which keep none of them.
    /// </para>
    /// </remarks>
    public static uint[] StarterSkillGroups(GameDataStore store, byte job)
    {
        if (job is 0 or > 32) return [];
        var bit = 1u << (job - 1);
        var candidates = new List<(uint Group, byte BaseLevel)>();
        byte floor = byte.MaxValue;
        foreach (var r in store.TalentTable)
        {
            if ((r.Require_Job & bit) == 0 || r.ISJobMaster != 0) continue;
            if (r.Need_Skill_Group_1 != 0 || r.Need_Skill_Group_2 != 0 || r.Need_Skill_Group_3 != 0) continue;
            candidates.Add((r.Skill_Group_ID, r.Base_Level));
            if (r.Base_Level < floor) floor = r.Base_Level;
        }

        // Only the candidates at the lowest Base_Level among them (see the remarks: job 9's 2312).
        var groups = new SortedSet<uint>();
        foreach (var (group, baseLevel) in candidates)
            if (baseLevel == floor) groups.Add(group);
        var result = new uint[groups.Count];
        groups.CopyTo(result);
        return result;
    }

    /// <summary>The skill groups that are bought with talent points: every <c>Talent_Table</c> row, of any class.</summary>
    public static HashSet<uint> TalentSkillGroups(GameDataStore store)
    {
        var groups = new HashSet<uint>();
        foreach (var r in store.TalentTable) groups.Add(r.Skill_Group_ID);
        return groups;
    }

    /// <summary>
    /// True when <paramref name="skillGroup"/> is a talent skill (any <c>Talent_Table</c> row of any class names it),
    /// with <paramref name="max"/> the highest <c>Max_Skill_Level</c> among its rows. A group whose rows all carry
    /// <c>Max_Skill_Level</c> 0 (no cap recorded) reports <see cref="byte.MaxValue"/>, so <c>level &lt;= max</c> is a
    /// safe test for every caller. False (and <c>max</c> 0) for a group with no row: a non-talent, guild, item or
    /// monster skill. Unlike <see cref="IsTalentAllocationLegal"/> this is class-agnostic: it bounds a CAST, where the
    /// per-job cap is already enforced when the point is spent.
    /// </summary>
    public static bool TryGetSkillLevelLimit(GameDataStore store, uint skillGroup, out byte max)
    {
        max = 0;
        if (skillGroup > ushort.MaxValue) return false;
        var found = false;
        foreach (var r in store.TalentTable)
        {
            if (r.Skill_Group_ID != skillGroup) continue;
            found = true;
            if (r.Max_Skill_Level > max) max = r.Max_Skill_Level;
        }
        if (found && max == 0) max = byte.MaxValue;
        return found;
    }

    /// <summary>
    /// Talent points <paramref name="learned"/> has cost a character of <paramref name="job"/>: the levels of
    /// its <c>Talent_Table</c> skills, less the free first level of each starter it holds
    /// (<see cref="StarterSkillGroups"/>). Skills with no Talent_Table row (the profession skills, e.g. 50300)
    /// cost none.
    /// </summary>
    /// <remarks>
    /// A job-10 change leaves [3104, 3107, 3124] and 50300 at level 1 and answers 2544 blob+10 = 24 at level 25: the
    /// profession level costs nothing. A job-6 reset leaves 1102, 1110 and 1144 at level 1 and 50300 at level 3, and
    /// the character's next 1310 (the full talent state) spends the whole pool (2543 blob+10 = 0) on rows summing to
    /// more levels: 1102 at level 2, which it held at 1, and rows it did not hold, 1200 among them. The starters'
    /// first level is free, the job-master row is not, and 50300 is not in the vector.
    /// </remarks>
    public static int TalentPointsSpent(GameDataStore store, byte job, IEnumerable<(uint Group, byte Level)> learned)
    {
        var tree = TalentSkillGroups(store);
        var starters = StarterSkillGroups(store, job);
        int spent = 0;
        foreach (var (group, level) in learned)
        {
            if (level == 0 || !tree.Contains(group)) continue;
            spent += level;
            if (Array.IndexOf(starters, group) >= 0) spent -= 1;
        }
        return Math.Max(0, spent);
    }

    /// <summary>
    /// Talent points left to spend: one per level after the first (the 2542 each level-up sends is level - 1
    /// while nothing is bought) minus <see cref="TalentPointsSpent"/>, clamped to the byte the client keeps.
    /// </summary>
    /// <remarks>
    /// Skill resets send level - 1 as the point count (42 at level 43, 49 at level 50, 24 at level 25 for job
    /// changes). A job-master Warrior at level 50 got 69: 20 more, from a source this server does not model.
    /// </remarks>
    public static byte TalentPointsRemaining(GameDataStore store, byte job, int level,
        IEnumerable<(uint Group, byte Level)> learned)
        => (byte)Math.Clamp(Math.Max(0, level - 1) - TalentPointsSpent(store, job, learned), 0, 255);

    /// <summary>
    /// Most (group, level) pairs one 1310 may carry: <c>Talent_Table.json</c> has 230 rows, one per skill group, and a
    /// request lists each group at most once, so a longer vector can only repeat groups or name junk.
    /// </summary>
    public const int MaxTalentAllocations = 230;

    /// <summary>
    /// Whether a 1310 ReqPCUseTalentPoint allocation is legal to persist for a character of
    /// <paramref name="job"/> at <paramref name="level"/> holding <paramref name="held"/> (its LearnedSkills,
    /// group -> level; null is an empty tree). Each requested <c>(group, newLevel)</c> pair must name a
    /// <c>Talent_Table</c> skill the job may use (bit <c>job-1</c> of <c>Require_Job</c>), at a level within
    /// <c>[1, Max_Skill_Level]</c> that is not below the level already held, the character must have reached the
    /// row's <c>Base_Level</c>, and the prerequisites <c>Need_Skill_Group_1..3</c> must be in the post-change tree
    /// (the held tree overlaid with the requested pairs) at their <c>_Level</c> or more. A pair with a zero group or
    /// level is ignored (the handler persists neither). The first violation sets <paramref name="reason"/> and
    /// returns false, so the handler can refuse the whole request without persisting any of it.
    /// </summary>
    /// <remarks>
    /// These gates block the forgeries a modified client can attempt on 1310: learning a skill that is not
    /// in the class's talent tree (including junk or other classes' ids), raising one past its cap, taking one
    /// below the level it unlocks at, buying a skill without its parent, or lowering a held level to get points back.
    /// The talent-point BUDGET is deliberately NOT a hard gate
    /// here: the earned pool this server models (<c>level - 1</c>) is known to undercount for some
    /// characters — a job-master Warrior can hold 69 points at level 50 where <c>level - 1</c> is
    /// 49, from a source not yet modelled (see <see cref="TalentPointsRemaining"/>) — so rejecting on it
    /// would refuse a legitimate allocation. The caller logs an over-budget spend instead, and
    /// <see cref="TalentPointsRemaining"/> already clamps the pool it reports to the client to zero.
    /// The monotonic rule is defensive: the client UI never stages a level below the one held, and a reset goes
    /// through the delete path (item 2685 / ItemUseHandler), never through a 1310.
    /// <c>Need_Quest_ID</c> is not enforced: no row of the shipped table sets it, and the completed-quest set lives in
    /// the quest repository, not in the player state, so it needs an async read this pure gate does not have.
    /// </remarks>
    public static bool IsTalentAllocationLegal(
        GameDataStore store, byte job, int level,
        IReadOnlyList<(uint Group, byte Level)> allocations,
        IReadOnlyDictionary<uint, byte>? held,
        out string reason)
    {
        if (job is 0 or > 32)
        {
            reason = $"job {job} is out of range";
            return false;
        }

        var bit = 1u << (job - 1);

        // The job's talent rows, group -> (cap, unlock level). A group shared by several of the job's rows
        // takes the highest cap and the lowest unlock among them.
        var rows = new Dictionary<uint, (byte Max, byte Base)>();
        var needs = new Dictionary<uint, List<(uint Group, byte Level)[]>>();
        foreach (var r in store.TalentTable)
        {
            if ((r.Require_Job & bit) == 0) continue;
            if (rows.TryGetValue(r.Skill_Group_ID, out var cur))
                rows[r.Skill_Group_ID] = (Math.Max(cur.Max, r.Max_Skill_Level), Math.Min(cur.Base, r.Base_Level));
            else
                rows[r.Skill_Group_ID] = (r.Max_Skill_Level, r.Base_Level);

            // Prerequisites of this row; a group listed by several of the job's rows is satisfied by any one of them.
            var row = new List<(uint, byte)>(3);
            if (r.Need_Skill_Group_1 != 0) row.Add((r.Need_Skill_Group_1, r.Need_Skill_Group_1_Level));
            if (r.Need_Skill_Group_2 != 0) row.Add((r.Need_Skill_Group_2, r.Need_Skill_Group_2_Level));
            if (r.Need_Skill_Group_3 != 0) row.Add((r.Need_Skill_Group_3, r.Need_Skill_Group_3_Level));
            if (!needs.TryGetValue(r.Skill_Group_ID, out var list)) needs[r.Skill_Group_ID] = list = new();
            list.Add(row.ToArray());
        }

        // The tree as it stands once the request is applied (a repeated group keeps its last level, as the handler's
        // dedup does), against which both the monotonic rule and the prerequisites are read.
        var after = new Dictionary<uint, byte>();
        if (held is not null) foreach (var (g, l) in held) after[g] = l;

        foreach (var (group, lvl) in allocations)
        {
            if (group == 0 || lvl == 0) continue;
            if (!rows.TryGetValue(group, out var row))
            {
                reason = $"skill group {group} is not a talent skill of job {job}";
                return false;
            }
            if (row.Max != 0 && lvl > row.Max)
            {
                reason = $"skill group {group} level {lvl} exceeds its max {row.Max}";
                return false;
            }
            if (level < row.Base)
            {
                reason = $"skill group {group} unlocks at character level {row.Base} (have {level})";
                return false;
            }
            if (held is not null && held.TryGetValue(group, out var heldLevel) && lvl < heldLevel)
            {
                reason = $"skill group {group} level {lvl} is below the held level {heldLevel}";
                return false;
            }
            after[group] = lvl;
        }

        foreach (var (group, lvl) in allocations)
        {
            if (group == 0 || lvl == 0) continue;
            var alternatives = needs[group];   // rows has the group, so does needs
            var met = false;
            foreach (var req in alternatives)
            {
                var ok = true;
                foreach (var (needGroup, needLevel) in req)
                {
                    // A Need_Skill_Group this job cannot learn (not among its own talent rows) is a cross-job
                    // shared-row artifact, not a real prerequisite for this job - skip it, or the skill would be
                    // permanently unlearnable (e.g. group 6102's row names 6204, which belongs to another job's tree).
                    if (!rows.ContainsKey(needGroup)) continue;
                    if (!after.TryGetValue(needGroup, out var have) || have < Math.Max((byte)1, needLevel)) { ok = false; break; }
                }
                if (ok) { met = true; break; }
            }
            if (!met)
            {
                reason = $"skill group {group} is missing a prerequisite skill group";
                return false;
            }
        }

        reason = "";
        return true;
    }

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
        return map.TryGetValue(currentStatValue, out var c) ? c : 1;
    }
}
