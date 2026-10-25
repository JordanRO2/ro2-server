using System.Text;
using BP = System.Buffers.Binary.BinaryPrimitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Handles player spawn and despawn notifications (OnPC_In 2504 and OnPC_Out 2505) for multiplayer visibility.
/// When a player enters the world, the players already on the same map and the newcomer are sent to each other.
/// </summary>
/// <remarks>
/// Notifications use the internal unencrypted RMI path. The remote-player record is serialized in the variable
/// wire form the client reads: a fixed header, the name, job bits, mask-gated optional fields, the mount and
/// guild blocks, and the equipment, effect and list tails. The body is gated by the gender and race fields, so
/// gender is clamped to a valid value.
/// </remarks>
public class PlayerSpawnService
{
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly ILogger<PlayerSpawnService> _logger;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGuildEmblemRepository _emblems;

    public PlayerSpawnService(
        PlayerStateManager playerManager,
        BroadcastService broadcast,
        ILogger<PlayerSpawnService> logger,
        IServiceScopeFactory scopeFactory,
        IGuildEmblemRepository emblems)
    {
        _playerManager = playerManager;
        _broadcast = broadcast;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _emblems = emblems;
    }

 /// <summary>
 /// On world entry, makes the newcomer and every existing player on the same map see each other via OnPC_In (2504).
 /// Existing players are sent to the newcomer, and the newcomer is sent to every existing session.
 /// Despawn (OnPC_Out 2505) is handled by <c>NotifyPlayerDespawnAsync</c> and <c>LogoutHandler</c>.
 /// </summary>
    public async Task NotifyPlayerSpawnAsync(PlayerState newPlayer)
    {
        var existingPlayers = _playerManager.GetPlayersOnMap(newPlayer.MapId)
            .Where(p => p.SessionId != newPlayer.SessionId)
            .ToList();

        using var scope = _scopeFactory.CreateScope();
        var invRepo = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();

        var guildsReady = await EnsureGuildsLoadedAsync(scope.ServiceProvider);

        var store = scope.ServiceProvider.GetRequiredService<GameDataStore>();
        var newSession = _broadcast.GetSession(newPlayer.SessionId);

        var newcomerSpawn = await BuildSpawnBlobAsync(newPlayer, invRepo);
        if (newcomerSpawn is { } newcomer)
        {
            var newcomerBlob = newcomer.Blob;
            var newcomerViewData = ActorStatePackets.BuildOnPCViewData2315(newPlayer, newcomer.Character, newcomer.Equipment, _emblems);
            var newcomerViewInfo = guildsReady ? PcViewInfoPackets.BuildFor(newPlayer, _emblems) : null;

            var newcomerSpeed = newSession != null
                ? ActorMoveSpeed.BuildFor(newSession, newPlayer, store)
                : TraitPackets.BuildActorMoveSpeed2497(newPlayer.EntityId, TraitPackets.PcModalWorldEntrySpeed);
            foreach (var existing in existingPlayers)
            {
                var existingSession = _broadcast.GetSession(existing.SessionId);
                if (existingSession == null) continue;
                await existingSession.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_In, newcomerBlob);
                await existingSession.SendInternalRmiAsync(ZoneS2COpcodes.OnPCViewData, newcomerViewData);
                await existingSession.SendInternalRmiAsync(ZoneS2COpcodes.OnChangeActorMoveSpeed, newcomerSpeed);
                if (newcomerViewInfo != null)
                    await existingSession.SendInternalRmiAsync(ZoneS2COpcodes.NfyPCViewInfoUpdate, newcomerViewInfo);
            }
        }

        if (newSession != null)
        {
            foreach (var existing in existingPlayers)
            {
                if (await BuildSpawnBlobAsync(existing, invRepo) is not { } spawn)
                    continue;
                await newSession.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_In, spawn.Blob);
                await newSession.SendInternalRmiAsync(ZoneS2COpcodes.OnPCViewData,
                    ActorStatePackets.BuildOnPCViewData2315(existing, spawn.Character, spawn.Equipment, _emblems));

                var speed = _broadcast.GetSession(existing.SessionId) is { } existingSession
                    ? ActorMoveSpeed.BuildFor(existingSession, existing, store)
                    : TraitPackets.BuildActorMoveSpeed2497(existing.EntityId, TraitPackets.PcModalWorldEntrySpeed);
                await newSession.SendInternalRmiAsync(ZoneS2COpcodes.OnChangeActorMoveSpeed, speed);
                if (guildsReady)
                    await newSession.SendInternalRmiAsync(ZoneS2COpcodes.NfyPCViewInfoUpdate,
                        PcViewInfoPackets.BuildFor(existing, _emblems));
            }
        }

        _logger.LogInformation("Player '{Name}' (eid 0x{Eid:X8}) spawned on map {MapId}; exchanged OnPC_In with {Count} existing players.",
            newPlayer.Name, newPlayer.EntityId, newPlayer.MapId, existingPlayers.Count);
    }

 /// <summary>
 /// Loads the guild service and the emblem repository if they are not loaded yet. Returns false when either load fails.
 /// </summary>
 /// <remarks>
 /// The emblem store is needed because the 2591 carries the guild emblem index, and an unloaded store would make a
 /// viewer see no emblem for a guild that has one.
 /// </remarks>
    private async Task<bool> EnsureGuildsLoadedAsync(IServiceProvider services)
    {
        try
        {
            await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
            await _emblems.InitAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Guild registry or emblem load failed; NfyPCViewInfoUpdate (2591) not sent for this spawn exchange.");
            return false;
        }
    }

 /// <summary>
 /// Notifies all players on the map that a player has despawned (OnPC_Out 2505). The body is the actor entity id.
 /// </summary>
    public async Task NotifyPlayerDespawnAsync(PlayerState player)
    {
        var body = BuildActorOutList(player.EntityId, new[] { player.EntityId });
        await _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnPC_Out,
            writer => writer.WriteBytes(body), excludeSessionId: player.SessionId);

        _logger.LogDebug("Player '{Name}' (eid 0x{Eid:X8}) despawned from map {MapId}.",
            player.Name, player.EntityId, player.MapId);
    }

 /// <summary>msgTag carried at blob+2 by the 2505 OnPC_Out actor-out-list family.</summary>
 /// <remarks>Constant across all 2505 messages.</remarks>
    internal const ushort MsgTagActorOutList = 0xE3CF; // 58319

 /// <summary>msgTag carried at blob+2 by RMI 2504 OnPC_In.</summary>
 /// <remarks>Constant across all 2504 messages.</remarks>
    internal const ushort MsgTagOnPcIn = 0xE3CE; // 58318

 /// <summary>
 /// Builds the OnPC_Out (2505) body: a compact-length actor-out-list with the self length, msgTag, subject handle,
 /// and a count-driven array of actor handles.
 /// </summary>
 /// <remarks>
 /// The handle slots carry the PC actor type tag in their low byte, which generated ids do not yet carry.
 /// </remarks>
    internal static byte[] BuildActorOutList(uint subjectHandle, IReadOnlyList<uint> handles)
    {
        using var pw = new PacketWriter();
        pw.WriteCompactBABody(w =>
        {
            w.WriteUInt16(MsgTagActorOutList);
            w.WriteUInt32(subjectHandle);
            w.WriteUInt16((ushort)handles.Count);
            foreach (var h in handles)
                w.WriteUInt32(h);
        });
        return pw.ToArray();
    }

 /// <summary>
 /// Builds the OnPC_In (2504) body for <paramref name="player"/> as the remote-player record the client reads.
 /// Returns null if the player has no cached character snapshot. The live position is used, and the snapshot and
 /// worn gear are returned so the 2315 that follows carries the same record.
 /// </summary>
    private async Task<(byte[] Blob, RO2.Domain.Entities.Character Character, IReadOnlyList<InventoryItem> Equipment)?>
        BuildSpawnBlobAsync(PlayerState player, IInventoryRepository invRepo)
    {
        var character = player.Character;
        if (character == null)
        {
            _logger.LogWarning("Cannot build OnPC_In for '{Name}' (session {Sid}): no Character snapshot.",
                player.Name, player.SessionId);
            return null;
        }

        IReadOnlyList<InventoryItem> equipment;
        try
        {
            equipment = await invRepo.GetEquipmentAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnPC_In for '{Name}' (session {Sid}): equipment read failed; spawning without gear.",
                player.Name, player.SessionId);
            equipment = System.Array.Empty<InventoryItem>();
        }

        using var pw = new PacketWriter();
        WriteOnPc2504(pw, player, character, _emblems, equipment);
        return (pw.GetWrittenSpan().ToArray(), character, equipment);
    }

 /// <summary>Size of the fixed header of the remote-player record.</summary>
    internal const int HeaderSize = 70;

    // Offsets inside the fixed header of the remote-player record.
    private const int OffEntityId = 0;      // u32 actor handle
    private const int OffCharUid = 4;       // u64 character handle
    private const int OffUnk12 = 12;        // u8, always written as 0
    private const int OffPosX = 14;         // f32
    private const int OffPosY = 18;         // f32
    private const int OffPosZ = 22;         // f32
    private const int OffGender = 26;       // u8, 1 or 2
    private const int OffRace = 27;         // u8
    private const int OffAppearance = 28;   // 13 bytes: the stored create-appearance bytes [2..14]
    private const int AppearanceLen = 13;
    private const int OffAppearance14 = 40; // u8: the fifteenth appearance byte (create-appearance byte 14)
    private const int OffHp = 41;           // u32 current HP
    private const int OffMaxHp = 45;        // u32 maximum HP
    private const int OffSp = 49;           // u16 current SP
    private const int OffMaxSp = 53;        // u16 maximum SP
    private const int OffMoveSpeed = 57;    // f32 current move speed
    private const int OffYaw = 61;          // f32 facing angle
    private const int OffModel = 65;        // u8 per-character model byte
    private const int OffEquipHideFlag = 67; // u16 equipment/costume visibility bits (RMI 1312)

    /// <summary>Length of the create-appearance block a character row stores; bytes [2..14] go into the header.</summary>
    private const int StoredAppearanceLen = 15;

    /// <summary>Index of the first stored create-appearance byte copied into the header (bytes 0 and 1 are gender and race).</summary>
    private const int StoredAppearanceStart = 2;

    /// <summary>Value of the fifteenth appearance byte for a row without a stored create block (the commonest value).</summary>
    private const byte DefaultAppearance14 = 1;

    /// <summary>Valid gender values are 1 and 2; anything else is clamped to <see cref="DefaultGender"/>.</summary>
    private const byte DefaultGender = 1;
    private const byte MaxGender = 2;

    /// <summary>Race 1 (Norman) is valid for every record.</summary>
    private const byte DefaultRace = 1;

    /// <summary>Longest character name the record carries, in UTF-16 characters.</summary>
    private const int NameMaxChars = 21;

    /// <summary>Size of a guild GUID.</summary>
    private const int GuildGuidSize = 16;

    // jobBits packing (u32 after the name): level, class, creation profession and job level in separate bit fields.
    private const int JobBitsLevelShift = 21, JobBitsLevelMask = 0x3F;
    private const int JobBitsClassShift = 16, JobBitsClassMask = 0x1F;
    private const int JobBitsProfessionShift = 12, JobBitsProfessionMask = 0x0F;
    private const int JobBitsJobLevelShift = 6, JobBitsJobLevelMask = 0x3F;

    // Option mask (u16 after jobBits). Each set bit gates one optional field; the fields follow the mask in
    // ascending bit order.
    private const ushort OptParty = 0x0004;         // u32 party handle
    private const ushort OptTitleA = 0x0008;        // u32 worn title, slot A
    private const ushort OptTitleB = 0x0010;        // u32 worn title, slot B
    private const ushort OptMount = 0x0080;         // u32 mount id
    private const ushort OptRelation = 0x0200;      // u32 relation value, always present
    private const ushort OptReviewRefused = 0x8000; // no payload: this player refuses reviews

    /// <summary>Relation value sent under <see cref="OptRelation"/>: 2 makes the client treat the player as friendly.</summary>
    private const uint RelationFriendly = 2;

    /// <summary>Record count of an OnPC_In blob that carries a single player record.</summary>
    private const ushort SingleRecordCount = 1;

    /// <summary>Raw bytes read and discarded after the effect list; always zero.</summary>
    private const int DiscardedTailBytes = 4;

 /// <summary>
 /// Emits a single OnPC_In (2504) record in the variable wire form, with the 8-byte preamble and a record count of one.
 /// Internal and static for unit testing.
 /// </summary>
    internal static void WriteOnPc2504(PacketWriter w, PlayerState player, RO2.Domain.Entities.Character character,
        IGuildEmblemRepository emblems, IReadOnlyList<InventoryItem>? equipment = null)
    {
        using var body = new PacketWriter();

        // Message envelope: [u16 selfLen][u16 msgTag][u32 subject handle][u16 record count], then the records.
        // selfLen counts itself, so it equals the whole blob length and is patched once the blob is complete.
        var selfLenPos = body.Length;
        body.WriteUInt16(0);                 // blob+0 selfLen, patched below once the blob is complete
        body.WriteUInt16(MsgTagOnPcIn);      // blob+2 msgTag
        body.WriteUInt32(player.EntityId);   // blob+4 subject actor handle
        body.WriteUInt16(SingleRecordCount); // blob+8 record count

        WriteRecord(body, player, character, emblems, equipment);

        body.WriteAt(selfLenPos, (ushort)body.Length);

        var bodySpan = body.GetWrittenSpan();
        WriteProudCompactLength(w, (uint)bodySpan.Length); // [u8 byte count][length LE]
        w.WriteBytes(bodySpan);
    }

 /// <summary>
 /// Emits one record in the variable wire form: the base block (<see cref="WriteBaseBlock"/>), then the equipment,
 /// active effects, the discarded bytes and an empty u32 list. The guild block carries the player's guild.
 /// </summary>
 /// <param name="mountId">See <see cref="WriteBaseBlock"/>.</param>
    internal static void WriteRecord(PacketWriter body, PlayerState player, RO2.Domain.Entities.Character character,
        IGuildEmblemRepository emblems, IReadOnlyList<InventoryItem>? equipment = null, uint? mountId = null)
    {
        WriteBaseBlock(body, player, character, emblems, mountId);

        // Tail: worn equipment as [u16 count][count x 28-byte records] (the record shared with the 2315 and 2848
        // messages), the active-effect list, four discarded zero bytes, then an empty u32 list.
        var vec28 = EquipViewNotifyPackets.BuildEquipRecords28(equipment);
        body.WriteUInt16((ushort)vec28.Count);
        foreach (var rec in vec28)
            body.WriteBytes(rec);

        WriteEffectList(body, player, DateTime.UtcNow);

        body.WriteZeros(DiscardedTailBytes);

        body.WriteUInt16(0);   // empty u32 list: count 0
    }

 /// <summary>
 /// Writes the base block of the player record: the fixed header, name, job bits, optional fields, mount and guild.
 /// The equipment count and the list tails are written by the caller.
 /// </summary>
 /// <param name="mountId">
 /// When set, the record carries the mount option bit and this id. Glider notifies (2464) pass a mount id; the 2504
 /// record passes null and leaves the bit clear.
 /// </param>
    internal static void WriteBaseBlock(PacketWriter body, PlayerState player, RO2.Domain.Entities.Character character,
        IGuildEmblemRepository emblems, uint? mountId)
    {
        // Fixed 70-byte header, zero-filled so unwritten bytes stay 0.
        Span<byte> hdr = stackalloc byte[HeaderSize];
        hdr.Clear();

        BP.WriteUInt32LittleEndian(hdr.Slice(OffEntityId, 4), player.EntityId);

        BP.WriteUInt64LittleEndian(hdr.Slice(OffCharUid, 8), player.CharacterUid);

        hdr[OffUnk12] = 0;

        BP.WriteSingleLittleEndian(hdr.Slice(OffPosX, 4), player.X);
        BP.WriteSingleLittleEndian(hdr.Slice(OffPosY, 4), player.Y);
        BP.WriteSingleLittleEndian(hdr.Slice(OffPosZ, 4), player.Z);

        byte gender = character.Gender == 0 ? DefaultGender : character.Gender;
        if (gender > MaxGender) gender = DefaultGender;
        hdr[OffGender] = gender;
        hdr[OffRace] = character.Race == 0 ? DefaultRace : character.Race;

        // +28..+40: the stored create-appearance bytes, the same block the character's own 2310 and 2315 carry.
        WriteAppearanceBlock(hdr.Slice(OffAppearance, AppearanceLen), character);

        // +57 move speed (the speed the server states for this player in 2497) and +61 facing angle.
        BP.WriteSingleLittleEndian(hdr.Slice(OffMoveSpeed, 4),
            ActorMoveSpeed.CurrentSpeedOf(player, GameDataStore.Loaded));                          // move speed
        BP.WriteSingleLittleEndian(hdr.Slice(OffYaw, 4), player.Rotation);                     // yaw

        // +41..+56 HP and SP, laid out as in the 2315 self record.
        BP.WriteUInt32LittleEndian(hdr.Slice(OffHp, 4), (uint)Math.Max(0, player.Hp));
        BP.WriteUInt32LittleEndian(hdr.Slice(OffMaxHp, 4), (uint)Math.Max(0, player.MaxHp));
        BP.WriteUInt16LittleEndian(hdr.Slice(OffSp, 2), (ushort)Math.Clamp(player.Mp, 0, ushort.MaxValue));
        BP.WriteUInt16LittleEndian(hdr.Slice(OffMaxSp, 2), (ushort)Math.Clamp(player.MaxMp, 0, ushort.MaxValue));

        // +65 model byte: a per-character value the player's own 2310 also carries; every character uses the same
        // class value, as in the 2310 and 2315.
        hdr[OffModel] = ActorStatePackets.DetailTailModelClass;

        // +67 equipment hide flag: the costume/equipment visibility bits RMI 1312 stores, as in the 2310.
        BP.WriteUInt16LittleEndian(hdr.Slice(OffEquipHideFlag, 2), character.EquipHideFlag);

        body.WriteBytes(hdr);

        // Name: [u8 charCount <= 21][charCount x u16 UTF-16], no terminator.
        WriteWireWideString(body, character.Name ?? string.Empty, NameMaxChars);

        // jobBits: bits 21-26 character level, 16-20 combat class, 12-15 creation profession, 6-11 job level.
        uint jobBits = ((uint)(character.Level & JobBitsLevelMask) << JobBitsLevelShift)
                     | ((uint)(WorldEntryService.CombatJobByte(character) & JobBitsClassMask) << JobBitsClassShift)
                     | ((uint)(WorldEntryService.CreationProfessionByte(character) & JobBitsProfessionMask) << JobBitsProfessionShift)
                     | ((uint)(character.JobLevel & JobBitsJobLevelMask) << JobBitsJobLevelShift);
        body.WriteUInt32(jobBits);

        // Option mask: OptRelation is always set (relation 2 shows the player as friendly); OptReviewRefused is set
        // only when the player has turned reviews off.
        var partyHandle = PcRecordParty.HandleOf(player);
        var titles = KharaTitleStore.PublishedFor(player.CharacterId);
        uint titleA = titles?.TitleA ?? 0, titleB = titles?.TitleB ?? 0;
        ushort optMask = OptRelation;                         // 0x0200
        if (partyHandle.HasValue) optMask |= OptParty;        // 0x0204
        if (titleA != 0) optMask |= OptTitleA;
        if (titleB != 0) optMask |= OptTitleB;
        if (mountId.HasValue) optMask |= OptMount;            // 0x0280
        if (!character.AllowAnyoneReview) optMask |= OptReviewRefused;
        body.WriteUInt16(optMask);

        // Optional fields follow the mask in ascending bit order.
        if (partyHandle.HasValue) body.WriteUInt32(partyHandle.Value); // OptParty
        if (titleA != 0) body.WriteUInt32(titleA);              // OptTitleA
        if (titleB != 0) body.WriteUInt32(titleB);              // OptTitleB
        if (mountId.HasValue) body.WriteUInt32(mountId.Value); // OptMount
        body.WriteUInt32(RelationFriendly);                  // OptRelation

        // Mount block: [u8 hasMount] = 0, no mount.
        body.WriteByte(0);

        // Guild block, see WriteGuildBlock.
        WriteGuildBlock(body, GuildService.Instance.GetByCharId(player.CharacterId), player.CharacterId, emblems);
    }

 /// <summary>Maximum guild name length written in the guild block.</summary>
    private const int GuildNameMaxChars = 24;

 /// <summary>
 /// Writes the guild block of the player record: the player's guild name and, for guild members, the GUID, emblem
 /// index, robe record and rank title. Guildless players get a single zero byte.
 /// </summary>
 /// <remarks>
 /// The emblem, robe and title are taken from the same sources as the 2559 and 2591 messages so the three never
 /// disagree. The guild data loads lazily; until it has loaded, the player is written as guildless.
 /// </remarks>
    private static void WriteGuildBlock(PacketWriter body, Guild? guild, uint charId, IGuildEmblemRepository emblems)
    {
        var name = guild?.Name ?? string.Empty;
        if (name.Length > GuildNameMaxChars) name = name.Substring(0, GuildNameMaxChars);
        if (guild == null || name.Length == 0)
        {
            body.WriteByte(0);   // zero name length: guildless, nothing else follows
            return;
        }

        WriteWireWideString(body, name, GuildNameMaxChars);

        var guid = new byte[GuildGuidSize];
        if (guild.Guid16 != null)
            Array.Copy(guild.Guid16, guid, Math.Min(GuildGuidSize, guild.Guid16.Length));
        body.WriteBytes(guid);   // 16-byte guild GUID

        body.WriteUInt16(emblems.IndexOf(guild.Guid16));   // u16 emblem index
        body.WriteBytes(GuildRobeRecord.InfoForm());       // 25-byte robe record
        WriteWireWideString(body, GuildService.RankTitleOf(guild, charId),
            GuildRosterPackets.RankNameMaxChars);   // rank title
    }

 /// <summary>
 /// Writes the compact-length prefix: one byte giving the byte count of the value (1, 2, 4 or 8), then the value in
 /// little-endian order.
 /// </summary>
 /// <remarks>
 /// The byte count is chosen from the signed range, so values from 0x80 to 0xFF use two bytes.
 /// </remarks>
    internal static void WriteProudCompactLength(PacketWriter w, uint value) => w.WriteCompactLength(value);

 /// <summary>
 /// Writes a length-prefixed inline UTF-16 string: one byte character count, then the characters. Clamped to
 /// <paramref name="maxChars"/>. No terminator is written.
 /// </summary>
    private static void WriteWireWideString(PacketWriter body, string value, int maxChars)
    {
        if (value.Length > maxChars) value = value.Substring(0, maxChars);
        body.WriteByte((byte)value.Length);
        if (value.Length > 0)
            body.WriteBytes(Encoding.Unicode.GetBytes(value));
    }

 /// <summary>
 /// Writes the character's stored create-appearance bytes (13 bytes, from index 2) into the record header.
 /// </summary>
 /// <remarks>
 /// The appearance block matches the 15-byte block carried by the character's own 2310. A row without a create block
 /// uses zeros, with the most common value at the last byte.
 /// </remarks>
    private static void WriteAppearanceBlock(Span<byte> dst, RO2.Domain.Entities.Character character)
    {
        dst[..AppearanceLen].Clear();
        var raw = CharacterSerializer.ParseAppearancePublic(character.AppearanceJson).RawBytes;
        if (raw is { Length: >= StoredAppearanceLen })
            raw.AsSpan(StoredAppearanceStart, AppearanceLen).CopyTo(dst);
        else
            dst[OffAppearance14 - OffAppearance] = DefaultAppearance14;
    }

 /// <summary>Length of one active-effect record in the player record's effect list.</summary>
    internal const int EffectRecordLen = 23;

 /// <summary>
 /// Writes the active-effect list of the player record: <c>[u16 n][n x 23 B]</c>, one record per active effect, in
 /// ascending effect id:
 /// <code>
 /// +0 u32 effect id +4 u16 value +6 u8 stack count
 /// +7 u64 remaining ms, 0 = permanent +15 u32 source skill +19 u32 caster handle
 /// </code>
 /// </summary>
 /// <remarks>
 /// Written from the player's active effects. An effect whose expiry has passed is left out, so that it is not
 /// sent as permanent. The 2464 and 2465 consumers parse this list without applying it.
 /// </remarks>
    internal static void WriteEffectList(PacketWriter body, PlayerState player, DateTime nowUtc)
    {
        ActiveEffect[] snapshot;
        lock (player.ActiveEffects)
            snapshot = player.ActiveEffects.ToArray();
        WriteEffectRecords(body, snapshot, nowUtc);
    }

 /// <summary>
 /// Writes the effect list <c>[u16 n][n x 23 B]</c> for any actor's effects, used by the player record and by the NPC
 /// record extension. An expired effect is left out, one record is written per effect id, in ascending id, and the
 /// remaining time is rounded up, with 0 only for a permanent effect.
 /// </summary>
 /// <param name="body">Where the vector is written.</param>
 /// <param name="effects">A snapshot the caller took under the owner's lock.</param>
 /// <param name="nowUtc">The moment the remaining time is counted from.</param>
    internal static void WriteEffectRecords(PacketWriter body, IEnumerable<ActiveEffect> effects, DateTime nowUtc)
    {
        var live = effects
            .Where(e => e.ExpiresAt is null || e.ExpiresAt.Value > nowUtc)
            .GroupBy(e => e.EffectId).Select(g => g.Last())
            .OrderBy(e => e.EffectId)
            .Take(ushort.MaxValue)
            .ToList();

        body.WriteUInt16((ushort)live.Count);
        foreach (var e in live)
        {
            ulong remaining = e.ExpiresAt is { } until
                ? (ulong)Math.Max(1.0, Math.Ceiling((until - nowUtc).TotalMilliseconds))
                : 0UL;
            body.WriteUInt32(e.EffectId);
            body.WriteUInt16(e.EffectValue);
            body.WriteByte(e.StackCount);
            body.WriteUInt64(remaining);
            body.WriteUInt32(e.SourceSkillId);
            body.WriteUInt32(e.CasterHandle);
        }
    }
}
