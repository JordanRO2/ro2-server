using RO2.Economy.Domain;
using RO2.Skills.Domain;
using RO2.Skills.Application;
using RO2.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Enums;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

using NetworkZoneRmiBase = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.LoginPCHandler>;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handler for C2S RMI 1417 (Packet_CLMSN_LOGIN_PC) - Select character and enter world.
///
/// Payload format (59 bytes):
///   +0x00  10 bytes  Header (compact-length envelope, blob size, msgTag, zero u32)
///   +0x0A  8 bytes   Character UID (uint64 LE)
///   +0x12  33 bytes  Zeros
///   +0x33  8 bytes   Footer (transport trailer, ignored)
///
/// After receiving, server must send world entry data:
///   - RMI 2310 (OnCharacterDetailACK) with character data
///   - RMI 2311 (OnReceiveMapLoad) with map info
/// </summary>
public class LoginPCHandler : NetworkZoneRmiBase
{
    /// <summary>
    /// Bytes of the 1417 body that precede the character uid: the compact-length envelope
    /// (<c>01 39</c>: one length byte, blob length 57), the blob's inner size (u16 57), its msgTag (u16 56001,
    /// 0xDAC1) and a zero u32 (blob+4).
    /// </summary>
    private const int RequestHeaderBytes = 10;

    /// <summary>Smallest usable 1417 body: the header plus the u64 character uid.</summary>
    private const int MinRequestBytes = RequestHeaderBytes + sizeof(ulong);

    /// <summary>The character id is the low 32 bits of the u64 character uid the client sends.</summary>
    private const ulong CharacterIdMask = 0xFFFFFFFF;

    /// <summary>msgTag of the 2306 OnCharacterLoginFail blob: 56002 (0xDAC2).</summary>
    private const ushort MsgTagCharacterLoginFail = 0xDAC2;

    /// <summary>
    /// Refusal code carried at blob+20 of the 2306 OnCharacterLoginFail blob, the one field the client reads.
    /// 16840 is the code sent when the requested character cannot be entered; its text is not in the client's
    /// string table.
    /// </summary>
    private const ushort LoginRefusedReason = 16840;

    /// <summary>Level reported for a character stored with level 0.</summary>
    private const byte MinimumLevel = 1;

    /// <summary>
    /// Fallback maximum HP, <c>base + level * per-level</c>, for a class with no PCBaseStatus row (the
    /// class formula is authoritative whenever the row exists).
    /// </summary>
    private const int FallbackMaxHpBase = 100;
    private const int FallbackMaxHpPerLevel = 50;

    /// <summary>Fallback maximum SP, <c>base + level * per-level</c>, for a class with no PCBaseStatus row.</summary>
    private const int FallbackMaxMpBase = 50;
    private const int FallbackMaxMpPerLevel = 20;

    /// <summary>
    /// Highest job id that has a bit in the 32-bit <c>Require_Job</c> mask: job <c>j</c> maps to bit
    /// <c>j - 1</c>.
    /// </summary>
    private const byte MaxJobInRequireMask = 32;

    /// <summary>A skill id is <c>group * 100 + level</c>, so a quick-slot skill entry divided by this is its group.</summary>
    private const uint SkillIdGroupDivisor = 100;

    public override ushort Opcode => ZoneC2SOpcodes.Packet_CLMSN_LOGIN_PC;
    public override string Name => "LoginPC";

    public LoginPCHandler(IServiceScopeFactory scopeFactory, ILogger<LoginPCHandler> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override async Task HandleAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
    {
        Logger.LogInformation("Session {SessionId}: LoginPC (RMI 1417) received ({Size} bytes)",
            session.SessionId, payload.Length);

        if (payload.Length < MinRequestBytes || session.AccountId == null)
        {
            Logger.LogWarning("Session {SessionId}: Invalid LoginPC (too short or no account)",
                session.SessionId);
            return;
        }

        var reader = new PacketReader(payload.Span);

        // Skip the envelope + blob header
        reader.Skip(RequestHeaderBytes);

        // Read character UID (8 bytes)
        var charUid = reader.ReadUInt64();
        var characterId = (uint)(charUid & CharacterIdMask);

        Logger.LogInformation("Session {SessionId}: LoginPC - UID=0x{Uid:X16}, CharId={CharId}",
            session.SessionId, charUid, characterId);

        // A duplicate of an entry still in flight gets nothing. The client may re-send 1417 several times
        // before the answer arrives, and only ONE entry is answered per run (one 2558 .. 2310 burst), then the
        // server waits for the 1304; a same-session 1417 that arrives shortly after the 2310 and
        // before the client's 1304 gets no answer. "In flight" is this session's PlayerState for the same character
        // with MapLoaded still false (set false at AddPlayer below, true by AnsMapLoadingHandler on the 1304). The
        // receive loop handles one frame at a time (ProudSession.ProcessAsync and RmiDispatcher await each handler),
        // so the duplicate is seen only after the first entry has added its PlayerState, and no flag is needed.
        // A 1417 after the 1304 still runs the full entry, and so does one after a 1418 (ChangeCharacterHandler removes
        // the PlayerState) or on a new connection (no PlayerState yet): a same-uid 1417 after the first one's 1304 gets
        // a second 2310 (after a 1421 channel-move login or a 1303/2307 return to the character list).
        if (IsEntryInFlight(services.GetRequiredService<PlayerStateManager>().GetPlayer(session.SessionId), characterId))
        {
            Logger.LogDebug("Session {SessionId}: duplicate 1417 for CharId={CharId} while its entry is in flight (no 1304 yet); the original server answers once",
                session.SessionId, characterId);
            return;
        }

        // Load character from DB
        var characters = services.GetRequiredService<ICharacterRepository>();
        var character = await characters.GetByIdAsync(characterId);

        if (character == null || character.AccountId != session.AccountId.Value)
        {
            Logger.LogWarning("Session {SessionId}: Character {CharId} not found or wrong account",
                session.SessionId, characterId);
            // Send login fail (RMI 2306). Compact-BA (the first byte must be a valid compact length of
            // 1, 2, 4 or 8, otherwise the client decodes an empty ByteArray), msgTag
            // 56002 (0xDAC2), blob 22 bytes / body 24:
            //   [u16 22][u16 0xDAC2][u32 accountId@4][u64 charUid@8][u32 accountId@16][u16 reason@20]
            // blob+4 and blob+16 are the account id, the same id the session's
            // Login 12502 carried at body+2. blob+8 is the 1417's own
            // character uid echoed back. blob+20 is the only field the client reads,
            // the refusal code. 16840 is used as the generic "cannot enter" code.
            uint failAccountId = session.AccountId.Value;
            await session.SendRmiAsync(ZoneS2COpcodes.OnCharacterLoginFail, writer =>
            {
                writer.WriteCompactBABody(p =>
                {
                    p.WriteUInt16(MsgTagCharacterLoginFail);  // blob+2  msgTag 56002
                    p.WriteUInt32(failAccountId);               // blob+4  account id
                    p.WriteUInt64(charUid);                     // blob+8  the character uid the 1417 asked for
                    p.WriteUInt32(failAccountId);               // blob+16 account id (repeated)
                    p.WriteUInt16(LoginRefusedReason);          // blob+20 refusal code (see above)
                });
            });
            return;
        }

        // Set session state
        session.CharacterId = characterId;
        session.State = SessionState.InGame;

        // Which channel this character is on, for the location its friends' 23505 shows (ChannelPresenceStore).
        // Every login passes here after its 12005, and every channel move after its 2557, so the label follows
        // the channel the account is on now.
        await RecordChannelLocationAsync(services, session.AccountId.Value, characterId, session.SessionId, ct);

        Logger.LogInformation("Session {SessionId}: Character '{Name}' (Lv{Level} Job{Job}) entering world",
            session.SessionId, character.Name, character.Level, character.Job);

        // Generate entity ID for this session (unique per-session runtime ID)
        // Use a large entity ID (small values like 1 may be treated as invalid by the client)
        var entityId = PlayerStateManager.BuildActorHandle(session.SessionId);

        // Add to player state manager
        var playerManager = services.GetRequiredService<PlayerStateManager>();
        // Resolve spawn map/position from character DB, validated against Map_List
        var store = services.GetRequiredService<GameDataStore>();
        var (mapId, startX, startY, startZ) = WorldEntryService.ResolveSpawnPosition(character, store);
        Logger.LogInformation(
            "Session {Id}: spawn resolved to map {Map} ({X},{Y},{Z}) -- DB had map {DbMap} ({DbX},{DbY},{DbZ})",
            session.SessionId, mapId, startX, startY, startZ,
            character.MapId, character.X, character.Y, character.Z);

        // A row stored before CharacterCreateHandler kept the class holds the creation PROFESSION (create[21])
        // in Character.Job; rewrite it to the class (create[19]) before anything below reads Job as the class.
        // The condition and why it is safe and one-shot: CharacterHeadRules.TryHealStoredCombatClass. The client
        // is told the same class before and after (CombatJobByte already read create[19] for such a row); what
        // changes is the server: base primaries, PCBaseStatus row, starter skills and talent pool, quest job
        // checks. Persisted by the save below, with the recomputed stats. The previous job is kept to drop the
        // skills learned for it (after the skill load).
        byte jobBeforeHeal = character.Job;
        bool classHealed = CharacterHeadRules.TryHealStoredCombatClass(character);
        if (classHealed)
            Logger.LogInformation("Session {Id}: '{Name}' stored its creation profession {Old} as the class; class is now {New} (create[19])",
                session.SessionId, character.Name, jobBeforeHeal, character.Job);

        // ALWAYS RECOMPUTE the effective primaries as class base (Standard_Stat) + the player's ALLOCATED
        // bonus points (Bonus*). The class base is derivable, so we never trust the stored Str/Agi/… copy;
        // the only per-character state is the allocation, which is persisted separately. This keeps every
        // character correct with no migration (characters made under the old hardcoded stats — already
        // self-healed to base with Bonus*=0 — stay correct). Bonus points are spent via RMI 1493
        // (UseBonusStatPointHandler); unspent = BonusStatPointsEarned(level) − sum(Bonus*). If the stored
        // allocation somehow exceeds what the level earned (corruption / a level rollback), clamp it away so
        // the character can never carry more than it earned.
        bool statsChanged = false;
        if (StatService.ClassBasePrimaries(store, character.Job) is { } cbp)
        {
            int earned = CharacterStats.BonusStatPointsEarned(character.Level);
            int spent = character.BonusStr + character.BonusAgi + character.BonusInt
                      + character.BonusWis + character.BonusVit + character.BonusLuk;
            if (spent > earned)
            {
                // Over-allocated (should not happen): drop all bonuses back into the pool.
                character.BonusStr = character.BonusAgi = character.BonusInt =
                    character.BonusWis = character.BonusVit = character.BonusLuk = 0;
            }
            int nStr = cbp.str + character.BonusStr, nAgi = cbp.agi + character.BonusAgi,
                nInt = cbp.intel + character.BonusInt, nWis = cbp.wis + character.BonusWis,
                nVit = cbp.vit + character.BonusVit, nLuk = cbp.luk + character.BonusLuk;
            statsChanged = character.Str != nStr || character.Agi != nAgi || character.Int != nInt
                        || character.Wis != nWis || character.Vit != nVit || character.Luk != nLuk;
            character.Str = nStr; character.Agi = nAgi; character.Int = nInt;
            character.Wis = nWis; character.Vit = nVit; character.Luk = nLuk;
        }

        // Derive MaxHP/MaxSP from the class formula (PCBaseStatus): MaxHP = floor(BaseHP·BaseHPRate·
        // BaseHPRateValue) + BaseHPValue·(level-1) + VitHPValue·VIT,
        // MaxSP = WisSPValue·WIS. Always recomputed (the stored MaxHp/MaxMp are ignored except as a
        // fallback when the class has no data row). This is the authoritative max the vitals/combat/heal
        // paths use, and it matches the 2498 sheet the client shows.
        var baseStats = StatService.ComputeBaseStats(store, character.Job, character.Level,
            character.Str, character.Agi, character.Int, character.Wis, character.Vit, character.Luk);
        var maxHp = baseStats.MaxHp > 0 ? (int)baseStats.MaxHp
            : (character.MaxHp > 0 ? character.MaxHp : FallbackMaxHpBase + character.Level * FallbackMaxHpPerLevel);
        var maxMp = baseStats.MaxSp > 0 ? baseStats.MaxSp
            : (character.MaxMp > 0 ? character.MaxMp : FallbackMaxMpBase + character.Level * FallbackMaxMpPerLevel);

        // Write the recomputed stats back so the stored copy SELF-HEALS (no stale placeholders left in the
        // DB) — persist only when something actually changed. Current Hp/Mp are clamped into the new maxima
        // but otherwise left as-is (damage state), so a relog doesn't silently full-heal.
        if (classHealed || statsChanged || character.MaxHp != maxHp || character.MaxMp != maxMp)
        {
            character.MaxHp = maxHp;
            character.MaxMp = maxMp;
            if (character.Hp <= 0 || character.Hp > maxHp) character.Hp = maxHp;
            if (character.Mp <= 0 || character.Mp > maxMp) character.Mp = maxMp;
            try { await characters.SaveCharacterAsync(character); }
            catch (Exception ex) { Logger.LogWarning(ex, "Session {Id}: could not persist recomputed stats", session.SessionId); }
        }

        // Account-level GM tier: read from the shared accounts collection (0 = normal player). Gates the GM
        // chat commands (/i, /tp, /speed). A read failure degrades to a non-GM (0), never blocks world entry.
        byte accessLevel = 0;
        try
        {
            accessLevel = await services.GetRequiredService<RO2.Application.Interfaces.IAccountAccessRepository>()
                .GetAccessLevelAsync(session.AccountId.Value);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: could not read account access level; defaulting to 0", session.SessionId);
        }

        playerManager.AddPlayer(session.SessionId, new Models.PlayerState
        {
            SessionId = session.SessionId,
            // EntityId/CharacterUid/Character feed the multiplayer OnPC_In (2504) broadcast so OTHER
            // players see this PC. EntityId MUST match the id stamped into the local world-entry burst
            // (2310/2311/2497) below — same formula 0x00100000 + SessionId — so spawn and movement
            // broadcasts address the same actor.
            EntityId = entityId,
            CharacterUid = charUid,
            Character = character,
            CharacterId = characterId,
            AccountId = session.AccountId.Value,
            AccessLevel = accessLevel,
            Name = character.Name,
            Gender = character.Gender,
            Job = character.Job,
            Level = character.Level > 0 ? character.Level : MinimumLevel,
            MapId = mapId,
            X = startX,
            Y = startY,
            Z = startZ,
            Rotation = 0f,
            // No map until the client acks this entry's 2311 with 1304 (AnsMapLoadingHandler sets it).
            // The 1560s sent between a 1417 and its 1304 are not answered.
            MapLoaded = false,
            Hp = character.Hp > 0 ? character.Hp : maxHp,
            MaxHp = maxHp,
            Mp = character.Mp > 0 ? character.Mp : maxMp,
            MaxMp = maxMp,
            Exp = character.Exp,
            Gold = character.Gold,   // runtime wallet; persisted write-through on change + real-time write-behind + boundary flush
            BankGold = character.BankGold,   // bank zeny (set before AddPlayer, so no echo-back)
            BindIndex = character.BindIndex,   // seed the save-point (set before AddPlayer, so no echo-back)
            FacingYaw = character.FacingYaw,   // seed the persisted facing (2310 head+107), the same way
            Str = character.Str,
            Agi = character.Agi,
            Vit = character.Vit,
            Int = character.Int,
            Wis = character.Wis,
            Luk = character.Luk,
        });

        // Load inventory for the character detail packet
        IReadOnlyList<RO2.Inventory.Domain.InventoryItem>? inventoryItems = null;
        try
        {
            var inventory = services.GetRequiredService<RO2.Inventory.Domain.IInventoryRepository>();
            // Heal legacy items sitting at Slot -1 (added before slot-assignment existed): without a real
            // slot the wire mapping collapses them all onto bag slot 0, so the bag shows a different one of
            // them each login. Assign real slots once, before we deliver the inventory.
            var fixedSlots = await inventory.RepairSlotsAsync(characterId);
            if (fixedSlots > 0)
                Logger.LogInformation("Session {Id}: repaired {N} bag item(s) with unassigned slots",
                    session.SessionId, fixedSlots);
            inventoryItems = await inventory.GetByCharacterAsync(characterId);
        }
        catch { /* Inventory may not exist yet */ }

        // The bank size the 2310 record+121 carries (BankExpansion). Until it was stored, every bank took 60 items
        // while the client was told 20, so a bank may hold items at slot 20 or beyond: raise the count to the
        // 20-step that holds them all, as an expansion would have, so none sits outside the client's bank window.
        byte bankSlots = BankExpansion.RequiredFor(character.BankSlots, inventoryItems);
        if (bankSlots != character.BankSlots)
        {
            Logger.LogInformation("Session {Id}: '{Name}' bank slots {Old} -> {New} to hold its stored items",
                session.SessionId, character.Name, character.BankSlots, bankSlots);
            character.BankSlots = bankSlots;
            try { await BankExpansion.PersistAsync(services.GetRequiredService<ICharacterRepository>(), characterId, bankSlots); }
            catch (Exception ex) { Logger.LogWarning(ex, "Session {Id}: could not persist the bank slot count", session.SessionId); }
        }

        // Load learned skills from DB and populate runtime state
        IReadOnlyList<RO2.Skills.Domain.CharacterSkill>? learnedSkills = null;
        try
        {
            var skillRepo = services.GetRequiredService<ISkillRepository>();
            learnedSkills = await skillRepo.GetSkillsAsync(characterId);

            // A row whose class was just healed (above) was seeded and trained as the class its profession id
            // names (a Thief's 4101/4103/4108 on an Alter). Those skills are not the character's: drop them
            // before the starter seeding below adds the real class's, as the quest job change's skill reset
            // does for a new class. Talent points are derived from what is held, so the points they cost come
            // back by themselves.
            if (classHealed && learnedSkills is { Count: > 0 })
                learnedSkills = await DropSkillsOutsideClassAsync(services, store, characterId, character.Job,
                    learnedSkills, session.SessionId);

            // Mirror the loaded rows and seed the class's missing starter skills at level 1 (self-healing, so a
            // character made before this, or one that just job-changed, gains its basics on relog). The seeded set
            // is TalentRules.StarterSkillGroups, the one a skill reset leaves (ItemUseHandler); the flow persists
            // each and folds them into the list handed to the world-entry 2488 send.
            if (playerManager.GetPlayer(session.SessionId) is { } skillOwner && learnedSkills is not null)
            {
                var before = learnedSkills.Count;
                learnedSkills = await SeedStarterSkillsFlow.SeedAsync(
                    new LearnedSkillsSession(skillOwner), skillRepo, store, character.Job, characterId, learnedSkills);
                var granted = learnedSkills.Count - before;
                if (granted > 0)
                    Logger.LogInformation("Session {Id}: granted {N} starter skill(s) for job {Job} to {Name}",
                        session.SessionId, granted, character.Job, character.Name);
            }
        }
        catch (Exception ex) { Logger.LogDebug(ex, "Skill load/seed skipped"); }

        // Load active quests from DB and populate runtime state
        IReadOnlyList<RO2.Domain.Entities.CharacterQuest>? activeQuests = null;
        int completedQuestCount = 0;
        try
        {
            var questRepo = services.GetRequiredService<IQuestRepository>();
            activeQuests = await questRepo.GetActiveQuestsAsync(characterId);
            var completedIds = await questRepo.GetCompletedQuestIdsAsync(characterId);
            completedQuestCount = completedIds.Count;

            // Populate runtime state with active quest IDs
            var playerState2 = playerManager.GetPlayer(session.SessionId);
            if (playerState2 != null && activeQuests != null)
            {
                foreach (var q in activeQuests)
                {
                    playerState2.ActiveQuests.Add(q.QuestId);
                    if (q.IsTraced) playerState2.TracedQuests.Add(q.QuestId);   // restore tracker pins
                    // And its kill counts, stored at each 2425 (LootService.AdvanceKillQuestsAsync): the 2310
                    // below shows them (QuestProgressStore.WriteLoginElement) and the next kill counts on from
                    // them, as the 2425 does after a relog (see QuestProgressStore remarks). Then its other
                    // objective state: a collect / deliver counter from the bag (inventoryItems, the 2310's item vector),
                    // and the talk / use-item closes stored at the earlier login's close, so a quest the 2310 lists as
                    // done is turned in with no second 2432 / 2427 (QuestProgressStore.RestoreObjectiveState).
                    // Guarded on its own so one bad document cannot drop the quests after it from ActiveQuests.
                    try
                    {
                        QuestProgressStore.RestoreKillProgress(playerState2, q, store);
                        QuestProgressStore.RestoreObjectiveState(playerState2, q, store, inventoryItems);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Session {Id}: quest {QuestId} objective progress not restored",
                            session.SessionId, q.QuestId);
                    }
                }
            }
        }
        catch { /* Quest data may not exist yet */ }

        // Load the saved quick-slot bar so 2472 can restore it. Without this the bar goes out
        // empty on every login regardless of what was persisted.
        IReadOnlyList<RO2.Characters.Domain.CharacterQuickSlot>? quickSlots = null;
        try
        {
            var qs = services.GetRequiredService<IQuickSlotRepository>();
            quickSlots = await qs.GetByCharacterAsync(characterId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not load quick slots", session.SessionId);
        }

        // The character's saved key bindings (stored by UpdateKeyConfigHandler from C2S 1423) for the
        // world-entry 2558 Notify_ZSCLN_DB_ACCOUNT_CONFIG. The login 2558 is that list coming back,
        // record for record as saved by the last 1423 (see KeyConfigRepository).
        // Null (never saved, or a storage
        // error) makes BuildAccountConfig2558 emit the 11-byte no-config form, the same as before.
        KeyConfigDocument? keyConfig = null;
        try
        {
            keyConfig = await services.GetRequiredService<RO2.Characters.Domain.IKeyConfigRepository>()
                .GetAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not load key config", session.SessionId);
        }

        // The card-book charge a 1548 card album left (UseCashItemHandler persists it per character), for the
        // login 2793 NfyCardBookChargedTime. Null = the book was never charged, and such a
        // character gets no 2793 (see WorldEntryService); a storage error degrades to that same "nothing sent".
        DateTime? cardBookChargedUntil = null;
        try
        {
            var chargedUntilUtc = await services.GetRequiredService<ICardBookChargeRepository>()
                .GetChargedUntilUtcAsync(characterId, ct);
            cardBookChargedUntil = chargedUntilUtc is { } utc ? CardBookChargeStore.ToServerLocal(utc) : (DateTime?)null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not load card book charge", session.SessionId);
        }

        // Persisted collection state (monster book, pet book, active pet) for the 2913/2919/2918 the burst
        // ends with. Without it those three always went out in the "registered nothing" form, even for a
        // character whose CollectionDocument holds registrations or an active pet.
        // GetStateAsync never throws: a storage error yields the empty document, i.e. those empty forms.
        var collection = await CollectionService.From(services, Logger).GetStateAsync(characterId, ct);

        // The character's Khara state (points, accepted Kharas with their progress, completed Kharas), loaded
        // into KharaService's cache before the burst. The 2310 carries all three: the balance at
        // head+126 and the two vectors after the quest vector (KharaStateDocument has the layout),
        // and the client never re-accepts what they list. KharaService.PublishedPoints
        // and WriteLoginVectors hand them to the 2310 writer from this cache, synchronously, which is why the
        // load happens here and not in the writer. Loading here also means the first kill or loot after login
        // needs no database read to find its Kharas. A storage error leaves the cache empty; the next Khara
        // trigger retries the load.
        try
        {
            await KharaService.PreloadAsync(characterId, services, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not load Khara state", session.SessionId);
        }

        // The recipes learned with 1348 (CraftRecipeStore), for the login 2535: its consumer resets every learned-recipe
        // flag and sets only the listed ones, so a list without them unlearns them on the client. A storage error
        // leaves the list at the profession's Craft_Level-1 set, what it was before recipes were stored.
        IReadOnlyList<ushort>? learnedCraftRecipes = null;
        try
        {
            learnedCraftRecipes = await services.GetRequiredService<RO2.Economy.Domain.ICraftRecipeRepository>()
                .GetAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not load learned craft recipes", session.SessionId);
        }

        // The union membership for the 2310 head+173..184, published before the burst because the head writer reads
        // it synchronously (UnionMembershipStore). A storage error publishes nothing: the head then says no union.
        try
        {
            await UnionMembershipStore.PreloadAsync(characterId, services.GetRequiredService<RO2.Characters.Domain.IUnionMembershipRepository>(), ct);
        }
        catch (Exception ex)
        {
            UnionMembershipStore.Publish(characterId, null);
            Logger.LogError(ex, "Session {Id}: could not load union membership", session.SessionId);
        }

        // The vehicles the character owns (VehicleRepository, filled by reins items in ItemUseHandler), for the burst's
        // 2522. A storage error sends the empty list, the no-vehicle form, and the next map load retries.
        IReadOnlyList<OwnedVehicle>? vehicles = null;
        try
        {
            // A character load is where expiries come back cut to the whole second (VehiclePackets remarks:
            // the .5 s float32 sum of a reins use survives only in the session that made it). The repository stores
            // the cut list too, so the rest of this session, warps and glider arrivals included, keeps sending it,
            // and so a grant made now adds its days to the cut value.
            vehicles = await services.GetRequiredService<IVehicleRepository>().LoadAndCutToWholeSecondsAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not load owned vehicles", session.SessionId);
        }

        // The worn Khara titles for the 2310 head (+143 timer, +147 title A, +185 title B) and this PC's 2504 / 2464 /
        // 2465 records (optMask 0x0008 / 0x0010), published before the burst because both writers read it synchronously
        // (KharaTitleStore.Publish). A storage error publishes nothing: no title is shown until the next select.
        try
        {
            await services.GetRequiredService<KharaTitleStore>().PreloadAsync(characterId, store.KharaInfo, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not load the Khara titles", session.SessionId);
        }

        // The login 2488 (and the 2310's Vec1) carry the guild's line skills (WorldEntryService, the 2488
        // step), so the guild registry has to be loaded here: nothing on the login path loaded it before (its callers
        // were the guild handlers and PlayerSpawnService). The list adds Link_Skill_IDs to the own skills; it is passed
        // apart from learnedSkills, which also gives the talent points. A registry or table error falls back to the own
        // skills (null), what the burst sent before.
        IReadOnlyList<(uint Group, byte Level)>? useSkillList = null;
        try
        {
            await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
            if (playerManager.GetPlayer(session.SessionId) is { } skillOwner)
                useSkillList = GuildSkillBookLearn.SkillListFor(skillOwner, store);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {Id}: could not add the guild's line skills to the login skill list", session.SessionId);
        }

        // Send world entry data (with resolved map/position from GameDataStore)
        await WorldEntryService.SendWorldEntryAsync(session, character, charUid, entityId, Logger,
            inventoryItems, activeQuests, completedQuestCount, learnedSkills, store, quickSlots,
            collection: collection, keyConfig: keyConfig, cardBookChargedUntil: cardBookChargedUntil,
            learnedCraftRecipes: learnedCraftRecipes, vehicles: vehicles, useSkillList: useSkillList);

        // The 2793 just sent told the client its book is charged until cardBookChargedUntil (when that is still
        // ahead); 2794 NfyCardBookChargedTimeEnd tells it when the charge runs out during this session. A lapsed or
        // never-charged book needs no watch (Arm ignores a lapsed expiry: that 2793 was already [3][0.0]).
        if (cardBookChargedUntil is { } chargedUntil)
            CardBookChargeExpiryNotifier.Arm(session, characterId, chargedUntil, playerManager,
                services.GetRequiredService<ICardBookChargeRepository>(), Logger);

        // Player<->player visibility (2504 OnPC_In) is established in AnsMapLoadingHandler on the client's
        // 1304 map-load ack — NOT here. At this point neither this client nor the others are guaranteed to
        // have finished loading the map, so a one-shot 2504 sent now is dropped intermittently.

        // TODO: NPC spawns need CMessage-wrapped 97-byte batched format
        // TODO: Welcome messages / announcements (send in Phase 2, not Phase 1)
    }

    /// <summary>
    /// Whether a 1417 for <paramref name="characterId"/> repeats an entry this session has not finished:
    /// <paramref name="current"/> (the session's PlayerState) is that character and has not acked its map with 1304.
    /// </summary>
    internal static bool IsEntryInFlight(Models.PlayerState? current, uint characterId)
        => current is { MapLoaded: false } && current.CharacterId == characterId;

    /// <summary>
    /// Labels <paramref name="characterId"/> with the channel <paramref name="accountId"/> selected at 12005 or
    /// moved to at 1421, for the Chat server's 23505 friend records (ChannelPresenceStore). An account with no
    /// recorded channel, or a handle our channel list does not have, gets an empty label: there is
    /// no channel to name. A storage failure is logged and leaves the
    /// location empty or stale; it never blocks the world entry.
    /// </summary>
    private async Task RecordChannelLocationAsync(IServiceProvider services, uint accountId, uint characterId,
        uint sessionId, CancellationToken ct)
    {
        try
        {
            var presence = services.GetRequiredService<IChannelPresenceStore>();
            var world = services.GetRequiredService<RO2.Server.Zone.Configuration.ServerConfiguration>().World;
            var handle = await presence.GetAccountChannelAsync(accountId, ct);
            var location = handle is uint h ? ChannelLocation(world, h) : "";
            await presence.SetCharacterLocationAsync(characterId, handle ?? 0, location, ct);
            Logger.LogDebug("Session {Id}: character {CharId} is on channel 0x{Handle:X8} ('{Location}')",
                sessionId, characterId, handle ?? 0, location);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: could not record the channel of character {CharId}", sessionId, characterId);
        }
    }

    /// <summary>
    /// The location a friend record gives a player on the channel with handle <paramref name="handle"/>: the
    /// 'Channel N' our 2555 list names it (ZoneChannelList.ChannelId(world, N) == handle, the label the Login's
    /// 12503 gives the same handle), or empty when the list has no such channel (see
    /// ChannelPresenceStore).
    /// </summary>
    internal static string ChannelLocation(RO2.Application.Configuration.WorldConfig world, uint handle)
        => ChannelPresenceStore.LabelOf(handle, world.ChannelCount, ch => ZoneChannelList.ChannelId(world, ch));

    /// <summary>CharacterQuickSlot.EntryType of a skill cell; its EntryId is the skill id group*100+level
    /// (2472 type-2 entries, e.g. 410101; see ItemUseHandler.SkillQuickSlotEntryType).</summary>
    private const byte SkillQuickSlotEntryType = 2;

    /// <summary>
    /// Deletes the learned skills <paramref name="job"/> cannot use (<see cref="TalentRules.GroupsOutsideClass"/>) and
    /// clears the quick-slot cells that hold them, then returns the skills that are left. Only for a row whose
    /// class was healed at this login; a storage error is logged and the list returned unchanged.
    /// </summary>
    private async Task<IReadOnlyList<RO2.Skills.Domain.CharacterSkill>> DropSkillsOutsideClassAsync(
        IServiceProvider services, GameDataStore store, uint characterId, byte job,
        IReadOnlyList<RO2.Skills.Domain.CharacterSkill> learned, uint sessionId)
    {
        var dropped = TalentRules.GroupsOutsideClass(store, job, learned.Select(s => s.SkillId));
        if (dropped.Length == 0) return learned;
        try
        {
            await services.GetRequiredService<ISkillRepository>().DeleteSkillsAsync(characterId, dropped);

            var bar = services.GetRequiredService<IQuickSlotRepository>();
            var droppedSet = new HashSet<uint>(dropped);
            foreach (var cell in await bar.GetByCharacterAsync(characterId))
                if (cell.EntryType == SkillQuickSlotEntryType && droppedSet.Contains(cell.EntryId / SkillIdGroupDivisor))
                    await bar.ClearAsync(characterId, cell.Page, cell.Slot);

            Logger.LogInformation("Session {Id}: dropped {N} skill group(s) job {Job} cannot use: {Groups}",
                sessionId, dropped.Length, job, string.Join(", ", dropped));
            return learned.Where(s => !droppedSet.Contains(s.SkillId)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Session {Id}: could not drop the skills of the healed class", sessionId);
            return learned;
        }
    }
}
