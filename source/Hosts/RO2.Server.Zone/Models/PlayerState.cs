using RO2.BuildingBlocks;
using System.Numerics;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using RO2.Characters.Domain;
using RO2.Combats.Domain;

namespace RO2.Server.Zone.Models;

/// <summary>
/// Runtime state for a connected player in the zone server.
/// </summary>
public class PlayerState : IWallet
{
    /// <summary>
    /// Where live changes to persistent fields are reported for real-time write-behind persistence.
    /// Wired by <see cref="Services.PlayerStateManager"/> when the player is registered; stays null
    /// before then so the world-entry load (which sets X/Hp/Gold from the DB row) does not echo the
    /// same values straight back to the database.
    /// </summary>
    internal IDirtySink? PersistSink;

    /// <summary>
    /// The setter of every persistent property (Job, Level, MapId, X/Y/Z, Hp/MaxHp, Mp/MaxMp, Exp, Gold,
    /// BankGold, BindIndex, FacingYaw): store the value and report it to <see cref="PersistSink"/> only when it really
    /// changed, so an unchanged write (a position re-sent as is, a regen tick at full HP) queues no DB write.
    /// Keeping it in one place means a new persistent property cannot forget the Mark.
    /// </summary>
    /// <remarks>
    /// The test is the type's own <c>!=</c> operator, which is what the hand-written setters used, and NOT
    /// <see cref="EqualityComparer{T}.Default"/>: for X/Y/Z that keeps IEEE float semantics, where NaN != NaN
    /// (writing NaN over NaN still marks) and +0 == -0 (no mark). Equals() would invert the first.
    /// </remarks>
    private void SetPersistent<T>(ref T slot, T value) where T : IEqualityOperators<T, T, bool>
    {
        if (slot != value)
        {
            slot = value;
            PersistSink?.Mark(this);
        }
    }

    // Identity
    public uint SessionId { get; set; }
    public uint CharacterId { get; set; }
    public uint AccountId { get; set; }
    /// <summary>Account GM/staff tier (0 = normal player). Loaded from the account at world entry; gates the
    /// GM chat commands. Account-level, so it follows the account across all its characters.</summary>
    public byte AccessLevel { get; set; }
    public string Name { get; set; } = string.Empty;

    // Spawn entity id (the runtime actor OID the client uses for this PC). This is the SAME id
    // the world-entry burst (2310/2311/2497) stamps for the local player — 0x00100000 + SessionId.
    // Multiplayer spawn/move broadcasts MUST key off this (NOT SessionId): the other client only
    // knows the actor by EntityId, so the spawn OID and the move OID must both be EntityId or the
    // client can't bind movement to the spawned avatar.
    public uint EntityId { get; set; }

    // 8-byte character UID (echo of the 1417 LOGIN_PC UID) — needed to rebuild the 2310-family
    // OnPC_In (2504) PC-detail blob for OTHER players who spawn this one.
    public ulong CharacterUid { get; set; }

    // Snapshot of the DB Character row, stashed at world-entry so PlayerSpawnService can rebuild the
    // exact same 2310 PC-detail body (appearance/job/exp/stats) for the other-player spawn (2504)
    // without re-hitting the DB. Null only if spawned through a path that didn't load it.
    public Character? Character { get; set; }

    // Character
    public byte Gender { get; set; }
    public byte Job { get; set => SetPersistent(ref field, value); }
    public byte ProJob { get; set; }
    public byte Level { get; set => SetPersistent(ref field, value); }
    public byte ProLevel { get; set; }

    // Position. MapId/X/Y/Z are persistent: their setters report a real change to PersistSink so the
    // DB tracks the player live. Rotation is client-cosmetic and is not stored.
    public uint MapId { get; set => SetPersistent(ref field, value); }
    public float X { get; set => SetPersistent(ref field, value); }
    public float Y { get; set => SetPersistent(ref field, value); }
    public float Z { get; set => SetPersistent(ref field, value); }
    public float Rotation { get; set; }

    // Stats. Hp/MaxHp/Mp/MaxMp/Exp/Gold are all persistent and report live changes (MaxHp/MaxMp grow
    // on level-up, so they must persist too or a relog loads Hp > MaxHp).
    public int Hp { get; set => SetPersistent(ref field, value); }
    public int MaxHp { get; set => SetPersistent(ref field, value); }
    public int Mp { get; set => SetPersistent(ref field, value); }
    public int MaxMp { get; set => SetPersistent(ref field, value); }

    /// <summary>
    /// Serialises read-modify-write of <see cref="Hp"/>/<see cref="Mp"/> and the dead flag. Hp/Mp are written from
    /// the NPC combat loop, the session handlers and the regen loop, so a plain <c>Hp -= x</c> loses updates. Every
    /// damage/heal/spend goes through <see cref="ApplyHpDelta"/>/<see cref="ApplySpDelta"/>/<see cref="SetVitalsDead"/>;
    /// a caller that must read and write as one step (a lethal-hit decision) holds this lock around them (the lock is
    /// reentrant). Leaf lock: take nothing else while holding it except <see cref="PersistSink"/>'s Mark.
    /// </summary>
    internal readonly Lock Vitals = new();

    /// <summary>
    /// Adds <paramref name="delta"/> (negative = damage) to <see cref="Hp"/> atomically, clamps the result to
    /// [0, <see cref="MaxHp"/>] and persists it. Returns the new HP.
    /// </summary>
    /// <remarks>
    /// A <see cref="MaxHp"/> of 0 or less means the maximum was never resolved (a state built outside world entry):
    /// the upper clamp is then skipped and only the floor of 0 applies, so such a state is not zeroed by its first hit.
    /// </remarks>
    public int ApplyHpDelta(int delta)
    {
        lock (Vitals)
        {
            Hp = ClampVital((long)Hp + delta, MaxHp);
            return Hp;
        }
    }

    /// <summary>
    /// Adds <paramref name="delta"/> (negative = spend) to <see cref="Mp"/> atomically, clamps the result to
    /// [0, <see cref="MaxMp"/>] and persists it. Returns the new SP. Same unresolved-maximum rule as
    /// <see cref="ApplyHpDelta"/>.
    /// </summary>
    public int ApplySpDelta(int delta)
    {
        lock (Vitals)
        {
            Mp = ClampVital((long)Mp + delta, MaxMp);
            return Mp;
        }
    }

    /// <summary>Death state in one step: HP 0, SP 0 and <see cref="IsDead"/>. A lethal hit reports SP 0
    /// (see NpcCombatService); RevivalHandler restores both.</summary>
    public void SetVitalsDead()
    {
        lock (Vitals)
        {
            Hp = 0;
            Mp = 0;
            IsDead = true;
        }
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to <see cref="Hp"/> as <see cref="ApplyHpDelta"/> does, UNLESS the player is
    /// already dead (<see cref="IsDead"/>): a dead player refuses the change and keeps its HP, so a regen or heal
    /// racing a lethal hit cannot silently resurrect a corpse. Returns whether the change was applied, with the
    /// resulting HP in <paramref name="newHp"/>. Revival is the explicit path (<see cref="ReviveTo"/>).
    /// </summary>
    public bool TryApplyHpDelta(int delta, out int newHp)
    {
        lock (Vitals)
        {
            if (IsDead)
            {
                newHp = Hp;
                return false;
            }
            newHp = ApplyHpDelta(delta);
            return true;
        }
    }

    /// <summary>SP counterpart of <see cref="TryApplyHpDelta"/>: refuses the change on a dead player.</summary>
    public bool TryApplySpDelta(int delta, out int newSp)
    {
        lock (Vitals)
        {
            if (IsDead)
            {
                newSp = Mp;
                return false;
            }
            newSp = ApplySpDelta(delta);
            return true;
        }
    }

    /// <summary>
    /// Sets <see cref="Hp"/> and <see cref="Mp"/> to <paramref name="hp"/>/<paramref name="sp"/> (each clamped to its
    /// maximum) and clears <see cref="IsDead"/>, in one step under the lock: the explicit revive path (RevivalHandler
    /// for 1372, RevivalSkillHandler for 1684). A heal that <see cref="TryApplyHpDelta"/> refused on the corpse comes
    /// back only here.
    /// </summary>
    public void ReviveTo(int hp, int sp)
    {
        lock (Vitals)
        {
            Hp = ClampVital(hp, MaxHp);
            Mp = ClampVital(sp, MaxMp);
            IsDead = false;
        }
    }

    private static int ClampVital(long value, int max)
        => (int)Math.Clamp(value, 0L, max > 0 ? max : (long)int.MaxValue);

    public ulong Exp { get; set => SetPersistent(ref field, value); }
    /// <summary>
    /// The zeny wallet, the authoritative balance (the game is RAM-authoritative; the DB only persists it).
    /// Never negative: the setter throws on a negative value. The setter is for loading the stored balance;
    /// every runtime change goes through <see cref="TryDebit"/> / <see cref="Credit"/>, which are atomic.
    /// </summary>
    public long Gold
    {
        get;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(Gold), value, "Gold cannot be negative.");
            lock (_walletGate)
            {
                SetPersistent(ref field, value);
                // Single source of truth: keep the persisted Character snapshot in lock-step, so no caller has to
                // hand-sync `Character.Gold = Gold` after a debit/credit.
                if (Character is { } character) character.Gold = value;
            }
        }
    }

    private readonly object _walletGate = new();

    /// <summary>
    /// Takes <paramref name="amount"/> from the wallet if it can pay it, atomically. Returns false and leaves the
    /// balance unchanged when <paramref name="amount"/> is negative or exceeds the balance; it never clamps to 0,
    /// so two concurrent spenders can never both be paid out of the same funds.
    /// </summary>
    public bool TryDebit(long amount)
    {
        if (amount < 0) return false;
        lock (_walletGate)
        {
            if (Gold < amount) return false;
            Gold -= amount;
            return true;
        }
    }

    /// <summary>
    /// Takes up to <paramref name="amount"/> from the wallet, atomically, and returns what it took:
    /// <c>min(amount, balance)</c>, 0 for a negative amount. A DELIBERATE clamp, for a charge that is never refused for
    /// money (the glider dismount fare, see GetOffGliderHandler); every other spender must use <see cref="TryDebit"/>,
    /// which refuses instead of clamping.
    /// </summary>
    public long DebitUpTo(long amount)
    {
        if (amount <= 0) return 0;
        lock (_walletGate)
        {
            var taken = Math.Min(amount, Gold);
            Gold -= taken;
            return taken;
        }
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to the wallet, atomically. The balance saturates at <see cref="long.MaxValue"/>
    /// rather than wrapping negative.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="amount"/> is negative.</exception>
    public void Credit(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), amount, "Credit amount cannot be negative.");
        lock (_walletGate)
        {
            Gold = Gold > long.MaxValue - amount ? long.MaxValue : Gold + amount;
        }
    }
    public long BankGold { get; set => SetPersistent(ref field, value); }

    // Base stats
    public int Str { get; set; }
    public int Agi { get; set; }
    public int Vit { get; set; }
    public int Int { get; set; }
    public int Wis { get; set; }
    public int Luk { get; set; }

    /// <summary>
    /// Save point the player is bound to, as the client's own index into game data table 99.
    /// It is an INDEX, not a position: RMI 2440 carries no map and no coordinates, and the client
    /// resolves map and x/y/z from that table itself. See SetBindIndexHandler.
    /// Persistent: a change reports to PersistSink so the bind point survives relog.
    /// </summary>
    public ushort BindIndex { get; set => SetPersistent(ref field, value); }

    /// <summary>
    /// The facing the client last reported, in RADIANS: the f32 at body+68 of its C2S 1526 ViewGroupPCMoveTo
    /// (ViewGroupMoveHandler, finite values only). The 2310-family head carries it at head+107 as 2-degree steps
    /// (CharacterHeadRules.FacingByte). Persistent, so a relog spawns the character facing that way.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Rotation"/>, the yaw the 2504 OnPC_In advertises at +61: that slot may use a
    /// different convention from the radians of 1526@68, so the 2504 output is left as it was.
    /// </remarks>
    public float FacingYaw { get; set => SetPersistent(ref field, value); }

    /// <summary>
    /// When the auto-attack loop may land its next hit on <see cref="TargetEntityId"/>.
    /// </summary>
    /// <remarks>
    /// Basic attack is a TOGGLE driven by the server: the client sends RMI 1498
    /// ReqNormalAttackStart once per target and then waits, so nothing happens at all unless the
    /// server keeps swinging. See AutoAttackService.
    /// </remarks>
    public DateTime NextAttackAt { get; set; }

    /// <summary>
    /// When the client last re-asserted the attack toggle with RMI 1498.
    /// </summary>
    /// <remarks>
    /// The client re-sends 1498 roughly every 1.6 s for as long as you are attacking, so its
    /// SILENCE is the disengage signal -- there is no explicit stop message. Without this the
    /// server keeps swinging forever once engaged.
    /// </remarks>
    public DateTime LastAttackRequestAt { get; set; }

    /// <summary>
    /// When <see cref="Services.MovementGuard"/> last accepted a client position for this PC: the baseline the
    /// next move's elapsed time (dt) is measured from, null until the first move is seen. Runtime-only; the
    /// accepted position itself is <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/> (only an accepted move writes
    /// them), so a server-initiated jump that sets those directly needs no update here.
    /// </summary>
    internal DateTime? LastValidatedMoveAtUtc;

    // Runtime state
    public bool IsMoving { get; set; }
    /// <summary>
    /// Set with HP 0 by NpcCombatService on a lethal hit; cleared by a revival (RevivalHandler for 1372,
    /// RevivalSkillHandler for 1684). RevivalHandler revives only while this (or <c>Hp &lt;= 0</c>) holds:
    /// a 1372 from a PC that is not dead is answered with a result-code 2399 and no revival.
    /// </summary>
    public bool IsDead { get; set; }

    // ---- Revival offers (3016 NfyRevivalSkill -> 1684 AnsRevivalSkill) -------------------------------

    private readonly Lock _revivalOffersLock = new();
    private readonly List<RevivalOffer> _revivalOffers = new();

    /// <summary>
    /// Most offers kept at once. It only bounds memory against a caster repeating the
    /// skill on a corpse; real play never piles up more than a couple of offers for one
    /// death, so 16 is never reached.
    /// </summary>
    internal const int MaxRevivalOffers = 16;

    /// <summary>
    /// Records a revival offer this dead PC was sent (a 3016 NfyRevivalSkill), so its 1684 can be matched.
    /// </summary>
    /// <remarks>
    /// Written from the CASTER's session (SkillDamageHandler, on the 1509 of a Skill_Function 7 skill) and
    /// read from this PC's own session (RevivalSkillHandler), so every access takes the lock. Every offer
    /// raised since the death is kept, not only the latest: when two casters' offers land and the 1684 echoes
    /// the EARLIER one, that one is honoured. This matches the client: its 3016 handler opens nothing while the
    /// revival dialog is already up, so the dialog the player answers is the first one. An identical
    /// offer (same caster, skill and position) is stored once.
    /// </remarks>
    public void AddRevivalOffer(RevivalOffer offer)
    {
        lock (_revivalOffersLock)
        {
            foreach (var o in _revivalOffers)
                if (o.CasterHandle == offer.CasterHandle && o.SkillId == offer.SkillId && o.SamePosition(offer.X, offer.Y, offer.Z))
                    return;
            // At the cap the new offer is dropped, not the oldest: the oldest is the dialog on screen.
            if (_revivalOffers.Count < MaxRevivalOffers)
                _revivalOffers.Add(offer);
        }
    }

    /// <summary>
    /// Finds the offer whose position is bit-for-bit (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) and, when there is one, drops EVERY pending offer: the revival spends them all.
    /// </summary>
    /// <remarks>
    /// The client builds 1684 from the 3016 it is answering, copying the three floats unchanged (the dialog
    /// record is <c>{u8 1, f32 x, f32 y, f32 z}</c> and is sent as is), so a 1684 is byte-equal to bytes 0..11 of
    /// the 3016 it answers. So an exact comparison is the right test and a tolerance would only widen what a
    /// modified client can name. After a revival no other offer is honoured: a 1684 for a second offer sent after
    /// the first had revived the player gets nothing.
    /// </remarks>
    public bool TryTakeRevivalOffer(float x, float y, float z, out RevivalOffer offer)
    {
        lock (_revivalOffersLock)
        {
            foreach (var o in _revivalOffers)
            {
                if (!o.SamePosition(x, y, z)) continue;
                offer = o;
                _revivalOffers.Clear();
                return true;
            }
        }
        offer = default;
        return false;
    }

    /// <summary>
    /// Drops every pending revival offer. Called when the PC is revived by any path (RevivalHandler's
    /// RestoreVitals for 1372, TryTakeRevivalOffer for 1684) and when it changes map (WarpService), because an
    /// offer's position is on the map the caster stood on.
    /// </summary>
    public void ClearRevivalOffers()
    {
        lock (_revivalOffersLock)
            _revivalOffers.Clear();
    }

    /// <summary>How many revival offers are pending.</summary>
    public int RevivalOfferCount
    {
        get { lock (_revivalOffersLock) return _revivalOffers.Count; }
    }
    public bool IsInCombat { get; set; }
    public uint TargetEntityId { get; set; }

    // ---- Battle mode (the 2398 OnChangeState "in battle" stance, PcCombatStateService) --------------
    // The client's "you have entered battle" combat stance (state 0x0C) is driven by the server; these track
    // when to send/keep/drop it. Distinct from IsInCombat (the auto-attack engagement): battle mode also enters
    // on taking a hit and lingers through an out-of-combat timeout.

    /// <summary>True while the player is in the visible combat stance (a PcCombat 0x0C 2398 is in effect).</summary>
    public bool InBattleMode { get; set; }

    /// <summary>When the player last attacked or was hit; drives the battle-mode leave timeout.</summary>
    public DateTime LastCombatActionAt { get; set; }

    /// <summary>When the PcCombat 0x0C 2398 was last sent for this player, to throttle the re-pulse.</summary>
    public DateTime LastBattleStateSentAt { get; set; }

    // Cached equipped-weapon damage range (TraitInfo trait 96 = Weapon Min Damage, 97 = Weapon Max Damage),
    // aggregated by StatService.ComputeEquipmentStats. Runtime-only (not persisted): recomputed wherever
    // the equipment sheet is (login world-entry, equip/unequip swap, socket attach) so the combat damage
    // path can read the weapon term without a per-hit inventory fetch. 0/0 = no weapon (bare fists).
    public int WeaponMinDamage { get; set; }
    public int WeaponMaxDamage { get; set; }

    /// <summary>Equipped weapon's Auto_Attack_Range (TraitInfo trait 42): bow ~20, melee ~2, 0 = unarmed.
    /// Recomputed wherever the 2498 sheet is (login/equip/socket). Drives the server-side reach gate for
    /// the auto-attack grant (1498→2694) and the damage validation (1499) so a bow attacks from range.</summary>
    public int AutoAttackRange { get; set; }

    // Cached GEAR-ADJUSTED combat inputs — the same effective primaries/defense the 2498 sheet shows,
    // resolved by StatService.EffectivePlayerStats and refreshed wherever the sheet is recomputed
    // (login world-entry, equip/unequip swap, socket attach). The combat path reads these so equipment
    // STR/INT actually changes the damage dealt and equipment/VIT DEF actually mitigates incoming hits,
    // without a per-hit inventory fetch. 0 = not yet resolved; combat falls back to the base primary.
    public int CombatStr { get; set; }
    public int CombatAgi { get; set; }
    public int CombatInt { get; set; }
    public int CombatWis { get; set; }
    public int CombatVit { get; set; }
    public int CombatLuk { get; set; }
    /// <summary>Effective physical Defense (2498 trait 26 = VitDefenseValue·VIT + worn armour), used to
    /// mitigate incoming NPC damage via the AoV DefenseRate. 0 = not yet resolved (no mitigation).</summary>
    public int CombatDefense { get; set; }
    /// <summary>Effective Vigor (2498 trait 62, gear/skill-driven), used to reduce skill cooldowns via the
    /// AoV Vigor formula. 0 = naked / not yet resolved (cooldown stays at the table value).</summary>
    public int CombatVigor { get; set; }
    /// <summary>Effective Critical stat (2498 trait 60): the ATTACKER's chance to land a x2 crit (kind 4).</summary>
    public int CombatCrit { get; set; }
    /// <summary>Effective Parry stat (2498 trait 61): the DEFENDER's chance to guard/block a hit to x0.5 (kind 3).</summary>
    public int CombatParry { get; set; }
    /// <summary>Effective Penetration (2498 trait 94, gear-only): reduces the target's effective armour before
    /// the DefenseRate. 0 = none (strict no-op).</summary>
    public int CombatPen { get; set; }

    /// <summary>Mirror the freshly-resolved gear-adjusted combat inputs onto the runtime state. Called at
    /// every point the 2498 sheet is recomputed so the combat path stays in lock-step with the sheet.</summary>
    public void CacheCombatStats(EffectiveStats s)
    {
        CombatStr = s.Str; CombatAgi = s.Agi; CombatInt = s.Int;
        CombatWis = s.Wis; CombatVit = s.Vit; CombatLuk = s.Luk;
        CombatDefense = s.Defense; CombatVigor = s.Vigor;
        CombatCrit = s.Crit; CombatParry = s.Parry; CombatPen = s.Pen;
    }

    // Active quests: quest_id set for duplicate-check
    public HashSet<uint> ActiveQuests { get; set; } = new();

    // Quests the player has pinned in the on-screen tracker box ("lock/focus"). Seeded at login from the
    // persisted CharacterQuest.IsTraced, sent to the client via 2779 AnsLoadQuestTraceList on its 1541
    // request, and replaced wholesale when the client sends 1540 ReqSaveQuestTraceList.
    public HashSet<uint> TracedQuests { get; set; } = new();

    // (unused) kept for source compat; the quest log now loads silently via the 2310 quest vector.
    public bool QuestsSynced { get; set; }

    // Kill-quest progress: quest_id → monsters killed so far toward its Complete_Type_Value2 target.
    // Advanced on kill in LootService, stored per quest document (QuestProgressStore) and restored at login;
    // cleared on complete/give-up. A u16, the width the client reads it at: 2425 carries the count
    // at blob+16 as a u16 and the 2310 a-array is u16 per
    // objective; quest 211112314 needs 1,000 kills per row, so the count can exceed a byte.
    public Dictionary<uint, ushort> QuestKillProgress { get; set; } = new();

    // Timestamp of the last glider teleport (1528). The client sends 1528 twice per flight; this guards
    // against processing the duplicate (which double-warped and dropped the socket).
    public DateTime LastGliderWarpUtc { get; set; } = DateTime.MinValue;

    // Set on a glider warp: after the destination map finishes loading (the client's next AnsMapLoading),
    // the server must re-dress the re-created PC with OnPCViewData 2315 (built here where the Character is
    // in hand), take it off the glider with OnUnmountFarGlider 2732, and re-unblock movement with 2497 —
    // in that order. Without them the arrived PC shows the default look and cannot move.
    public bool PendingGliderDismount { get; set; }
    public byte[]? PendingGliderViewData { get; set; }

    /// <summary>
    /// True once the client has acked its current map with C2S 1304 AnsMapLoading (set by
    /// AnsMapLoadingHandler), false from world entry (a new 1417 PlayerState starts false) and from every
    /// 2311 OnReceiveMapLoad until the 1304 that acks it (cleared by WarpService.WarpAsync
    /// right before its 2311). The glider re-entry 2311 goes out through
    /// WorldEntryService, which cannot see the PlayerState; that window is the one bracketed by
    /// <see cref="PendingGliderDismount"/> (armed before that 2311, cleared on its 1304), so a gate on
    /// map-loaded state must test both. Some replies are keyed on it: a 1560
    /// ReqPartyMatchingSearchInvite gets its 2621 only in this state, so PartyMatchingSearchInviteHandler gates on it.
    /// </summary>
    public bool MapLoaded { get; set; }

    // Skill cooldowns: skill_id → cooldown_expires
    public Dictionary<uint, DateTime> SkillCooldowns { get; set; } = new();

    /// <summary>Last time a 1508 ReqSkillStart for a skill GROUP (skillId/100) was accepted, used to
    /// debounce the burst of 2+ back-to-back 1508 frames a single skill activation emits (some casts
    /// fire a duplicate within 300 ms). Without it SP would be charged twice per activation — and for a
    /// high-SP skill the duplicate would see Mp &lt; cost and cancel the very cast the first frame started.
    /// Runtime-only.</summary>
    public Dictionary<uint, DateTime> RecentSkillStart { get; } = new();

    /// <summary>Skill id 0: no channelled skill in progress.</summary>
    private readonly ChannelSlot _channel = new();

    /// <summary>
    /// The channelled skill this PC is casting right now, 0 when none: set by an accepted 1508 of a
    /// Skill_Duration skill (SkillStartHandler), ended by the caster's 1440 cancel or 1609 complete
    /// (SkillChannelingCancelHandler, SkillChannelingCompleteHandler). The 1609 answer 2897 reports it: result
    /// 0 when the completing skill is still the one channelled, 1 when it no longer is (see
    /// SkillChannelingPackets). Runtime-only, like the cooldowns. Backed by the Domain <see cref="ChannelSlot"/>.
    /// </summary>
    public uint ActiveChannelSkillId => _channel.Active;

    /// <summary>Records <paramref name="skillId"/> as the channel in progress, replacing any earlier one.</summary>
    public void BeginChannel(uint skillId) => _channel.Begin(skillId);

    /// <summary>
    /// Ends the channel of <paramref name="skillId"/> if it is the one in progress and says whether it was.
    /// A request naming another skill (or 0) leaves the channel in progress untouched. Atomic, so two requests
    /// racing for the same channel cannot both see it active.
    /// </summary>
    public bool TryEndChannel(uint skillId) => _channel.TryEnd(skillId);

    // Learned skills: skill_id → learned level, loaded from DB on login
    // The reference is replaced, never grown in place, by the 1310 talent spend (OverlayLearnedSkills): readers on
    // other threads (cast gates, damage, 2488 builders) iterate whichever dictionary they grabbed, which then never
    // changes under them. Older call sites still write through the indexer, so the type stays a plain Dictionary.
    public Dictionary<uint, byte> LearnedSkills { get; set; } = new();

    private readonly Lock _learnedSkillsLock = new();

    /// <summary>Level of a learned skill at its first rank: the level a reset gives the skills it keeps.</summary>
    private const byte FirstSkillLevel = 1;

    /// <summary>
    /// Held by a talent spend from the legality check until its mirror is applied, so two 1310 requests of one
    /// player validate against the tree the previous one left, not the one before it.
    /// </summary>
    public SemaphoreSlim LearnedSkillsWriteGate { get; } = new(1, 1);

    /// <summary>A private copy of <see cref="LearnedSkills"/>, safe to enumerate and to validate a request against.</summary>
    public Dictionary<uint, byte> SnapshotLearnedSkills()
    {
        lock (_learnedSkillsLock) return new Dictionary<uint, byte>(LearnedSkills);
    }

    /// <summary>
    /// Sets each <paramref name="changes"/> skill to its level in a copy of <see cref="LearnedSkills"/> and swaps the
    /// copy in, so a reader never sees the dictionary change under it. Skills not listed are untouched.
    /// </summary>
    public void OverlayLearnedSkills(IEnumerable<(uint Group, byte Level)> changes)
    {
        lock (_learnedSkillsLock)
        {
            var next = new Dictionary<uint, byte>(LearnedSkills);
            foreach (var (group, level) in changes) next[group] = level;
            LearnedSkills = next;
        }
    }

    /// <summary>
    /// Removes each <paramref name="remove"/> group and sets each <paramref name="setToOne"/> group to level 1 in a
    /// copy of <see cref="LearnedSkills"/>, then swaps the copy in — one lock-guarded replacement so a skill reset is
    /// never seen half-applied by a reader on another thread. Skills in neither list are untouched.
    /// </summary>
    public void ResetLearnedSkills(IEnumerable<uint> remove, IEnumerable<uint> setToOne)
    {
        lock (_learnedSkillsLock)
        {
            var next = new Dictionary<uint, byte>(LearnedSkills);
            foreach (var group in remove) next.Remove(group);
            foreach (var group in setToOne) next[group] = FirstSkillLevel;
            LearnedSkills = next;
        }
    }

    // Active buffs: buff_id → expires
    public Dictionary<uint, DateTime> ActiveBuffs { get; set; } = new();

    /// <summary>
    /// Timed buff/debuff effects applied to this player, driven by EffectService. Each entry mirrors
    /// a 2492 OnAddEffect payload so it can be expired later with 2493 (unicast) + 2494 (broadcast).
    /// Mutated from both the item-use handler and the EffectService tick, so callers lock on the
    /// list itself before touching it.
    /// </summary>
    public List<ActiveEffect> ActiveEffects { get; } = new();

    /// <summary>One bank-open mark: when the window lapses and where the player stood when 1341 opened it.</summary>
    private sealed record BankOpenMark(DateTime UntilUtc, float X, float Y);

    private volatile BankOpenMark? _bankOpen;

    /// <summary>Session-only (never persisted). Set by <c>BankOpenHandler</c> (1341) when it opens the bank; read by
    /// the 1346 bank-swap handler through <see cref="IsBankOpen"/>. One reference swap, so a concurrent reader sees
    /// the old or the new mark whole. The window length and radius are the caller's
    /// defensive choice.</summary>
    public void MarkBankOpen(DateTime untilUtc, float x, float y) => _bankOpen = new BankOpenMark(untilUtc, x, y);

    /// <summary>Drops the bank-open mark (bank closed, map change).</summary>
    public void ClearBankOpen() => _bankOpen = null;

    /// <summary>True while a mark exists, has not lapsed at <paramref name="nowUtc"/>, and the player's current
    /// ground position (X/Y) is within <paramref name="maxDistance"/> of where the bank was opened.</summary>
    public bool IsBankOpen(DateTime nowUtc, float maxDistance)
    {
        var m = _bankOpen;
        if (m is null || nowUtc > m.UntilUtc) return false;
        float dx = X - m.X, dy = Y - m.Y;
        return dx * dx + dy * dy <= maxDistance * maxDistance;
    }

    // Timestamps
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public DateTime LoginTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One revival a dead PC was offered: the 3016 NfyRevivalSkill body, <c>[f32 x][f32 y][f32 z][u32 caster][u32 skill]</c>.
/// </summary>
/// <param name="CasterHandle">The PC whose Skill_Function 7 skill landed on the dead one.</param>
/// <param name="SkillId">That skill (group*100+level), e.g. 9200101 Yggdrasil Leaf.</param>
/// <param name="X">Where the revival puts the dead PC: the caster's own position when the skill landed.</param>
/// <param name="Y">See <paramref name="X"/>.</param>
/// <param name="Z">See <paramref name="X"/>.</param>
public readonly record struct RevivalOffer(uint CasterHandle, uint SkillId, float X, float Y, float Z)
{
    /// <summary>Bit-for-bit position equality: 1684 echoes the 3016's floats unchanged.</summary>
    public bool SamePosition(float x, float y, float z) =>
        BitConverter.SingleToInt32Bits(X) == BitConverter.SingleToInt32Bits(x)
        && BitConverter.SingleToInt32Bits(Y) == BitConverter.SingleToInt32Bits(y)
        && BitConverter.SingleToInt32Bits(Z) == BitConverter.SingleToInt32Bits(z);
}
