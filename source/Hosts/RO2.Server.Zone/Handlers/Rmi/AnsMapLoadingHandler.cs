using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;
using AccountGift = RO2.Domain.Entities.AccountGift;

using Base1304 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.AnsMapLoadingHandler>;
using Base1307 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqActorDetailInfoHandler>;
using Base1340 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPCBankDataHandler>;
using Base1435 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqLoadKharaTraceListHandler>;
using Base1442 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqCardBookListHandler>;
using Base1513 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGuildSkillListHandler>;
using Base1516 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqMessengerLoginKeyHandler>;
using Base1541 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqLoadQuestTraceListHandler>;
using Base1567 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqMyEventHandler>;
using Base1592 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallItemListAllHandler>;
using Base1593 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallItemListHandler>;
using Base1596 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqGameMallMyInfoHandler>;
using Base1599 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPresentPasswordStatusHandler>;
using Base1642 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqPlayTimeRewardHandler>;
using Base1644 = RO2.Network.Handlers.ZoneRmiHandlerBase<RO2.Server.Zone.Handlers.Rmi.ReqAttendanceRewardHandler>;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

public class AnsMapLoadingHandler : Base1304
{
    public override ushort Opcode => ZoneC2SOpcodes.AnsMapLoading; 
    public override string Name => "AnsMapLoading";

    // The client's 1304 ack body is a compact-length envelope:
    // [u8 present = 1][u8 inner length][u16 selfLen][u16 msgTag][u32 acked self actor handle].
    private const byte AckEnvelopePresent = 1;
    private const int AckMsgTagOffset = 4;
    private const int AckActorHandleOffset = 6;

    /// <summary>msgTag (0xE295) of the 1304 ack; the envelope's only payload field is the self actor handle.</summary>
    private const ushort MsgTagAnsMapLoading = 58005;

    /// <summary>Smallest valid ack: the envelope header plus the u32 actor handle.</summary>
    private const int AckMinLength = AckActorHandleOffset + sizeof(uint);

    /// <summary>3025 NfySkillCoolTime body: an empty cooldown list, a compact count of 0 (<c>01 00</c>).</summary>
    private static readonly byte[] EmptySkillCoolTimeList = BuildEmptySkillCoolTime3025();

    /// <summary>2836 NfySpinelRevivalInfo body: the spinel-revival cooldown as an F32, 0.0 (no revival cooldown on this server).</summary>
    private static readonly byte[] SpinelRevivalNoCooldown = BuildSpinelRevivalInfo2836(revivalCooldown: 0f);

    /// <summary>2837 NfyPCCafeRevivalInfo body: no PC-cafe revival info, every field zero (7 bytes).</summary>
    private static readonly byte[] PcCafeRevivalNone = BuildPcCafeRevivalInfo2837(revivalCooldown: 0f, flag: 0, count: 0);

    /// <summary>Builds the 3025 body: <c>[compact count][12-byte cooldown entries]</c>, here with no entries.</summary>
    private static byte[] BuildEmptySkillCoolTime3025()
    {
        using var pw = new PacketWriter();
        pw.WriteCompactLength(0);   // +0 compact count of cooldown entries (01 00)
        return pw.ToArray();
    }

    /// <summary>Builds the 2836 body: <c>[f32 revivalCooldown]</c> (4 bytes).</summary>
    private static byte[] BuildSpinelRevivalInfo2836(float revivalCooldown)
    {
        using var pw = new PacketWriter();
        pw.WriteSingle(revivalCooldown);   // +0 spinel-revival cooldown
        return pw.ToArray();
    }

    /// <summary>Builds the 2837 body: <c>[f32 revivalCooldown][u8 flag][u16 count]</c> (7 bytes).</summary>
    private static byte[] BuildPcCafeRevivalInfo2837(float revivalCooldown, byte flag, ushort count)
    {
        using var pw = new PacketWriter();
        pw.WriteSingle(revivalCooldown);   // +0 PC-cafe revival cooldown
        pw.WriteByte(flag);                // +4 flag
        pw.WriteUInt16(count);             // +5 count
        return pw.ToArray();
    }

    public AnsMapLoadingHandler(IServiceScopeFactory s, ILogger<AnsMapLoadingHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name} ({Size}b)", session.SessionId, Name, payload.Length);

        
        
        
        uint ackedHandle = 0;
        if (payload.Length >= AckMinLength && payload.Span[0] == AckEnvelopePresent
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Span.Slice(AckMsgTagOffset, 2)) == MsgTagAnsMapLoading)
            ackedHandle = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(AckActorHandleOffset, sizeof(uint)));

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        bool moveSpeedSent = false;
        async Task SendMoveSpeedAsync(uint handle)
        {
            await session.SendInternalRmiAsync(
                ZoneS2COpcodes.OnChangeActorMoveSpeed,
                TraitPackets.BuildActorMoveSpeed2497(handle, TraitPackets.PcModalWorldEntrySpeed));
            moveSpeedSent = true;
            Logger.LogDebug("Session {Id}: Sent OnChangeActorMoveSpeed (2497) speed={Speed} on map-load complete",
                session.SessionId, TraitPackets.PcModalWorldEntrySpeed);
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var playerManager = services.GetRequiredService<PlayerStateManager>();
        if (playerManager.TryGetPlayer(session.SessionId, out var player) && player is not null)
        {
            var npcSpawnSvc = services.GetRequiredService<NpcSpawnService>();

            if (ackedHandle != 0 && ackedHandle != player.EntityId)
                Logger.LogWarning("Session {Id}: AnsMapLoading acks actor {Acked:X8}, PlayerState holds {Ours:X8}",
                    session.SessionId, ackedHandle, player.EntityId);

            
            
            player.MapLoaded = true;

            
            
            
            
            
            
            
            DungeonInstanceRegistry.Shared.RecordLoadedMap(player.CharacterId, player.MapId);
            KharaTitleClock.OnMapLoaded(player.CharacterId,
                services.GetRequiredService<RO2.GameData.GameDataStore>().GetMap(player.MapId)?.Map_Type,
                System.DateTime.UtcNow);

            
            
            
            
            MailService.ForgetClaimsOnMapLoad(player);

            
            
            
            
            
            
            
            
            
            
            
            
            await session.SendRmiAsync(ZoneS2COpcodes.OnChangeState,
                writer => writer.WriteBytes(ActorStatePackets.BuildOnChangeState2398(player.EntityId)));

            
            
            
            
            
            var worldEvents = services.GetRequiredService<WorldEventService>();
            foreach (var trigger in await worldEvents.BuildWorldEntryBurstAsync(player.MapId, ct))
                await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyWorldEventTrigger, trigger);

            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            ulong masteryTotal = WorldEntryService.JobMasteryPoints(player.Character);

            
            
            async Task SendMoveSpeedAndMasteryAsync(uint handle)
            {
                await SendMoveSpeedAsync(handle);
                if (masteryTotal > 0)
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyJobMasteryPointChange,
                        TraitPackets.BuildJobMasteryPointChange2986(delta: 0,
                            totalPoints: (uint)System.Math.Min(masteryTotal, uint.MaxValue)));
            }

            
            
            
            
            
            
            
            var spawnSvc = services.GetRequiredService<PlayerSpawnService>();
            await spawnSvc.NotifyPlayerSpawnAsync(player);

            await npcSpawnSvc.SendMapNpcsAsync(session, player.MapId);

            
            
            
            
            
            
            
            
            
            
            
            var funcObjects = FuncObjectRegistry.ForMap(services.GetRequiredService<RO2.GameData.GameDataStore>(), player.MapId);
            foreach (var frame in FuncObjectPackets.BuildMapLoadFrames2512(funcObjects))
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnFuncObject_In, frame);

            var characterRepo = services.GetRequiredService<RO2.Application.Interfaces.ICharacterRepository>();
            var inventoryRepo = services.GetRequiredService<RO2.Inventory.Domain.IInventoryRepository>();
            var store = services.GetRequiredService<RO2.GameData.GameDataStore>();
            
            
            var effectService = services.GetRequiredService<EffectService>();

            
            
            RO2.Domain.Entities.Character? character;
            if (player.PendingGliderDismount)
            {
                player.PendingGliderDismount = false;
                var prebuiltViewData = player.PendingGliderViewData;
                player.PendingGliderViewData = null;

                
                character = await characterRepo.GetByIdAsync(player.CharacterId);
                SyncLiveLevel(player, character);
                var equipped = await inventoryRepo.GetEquipmentAsync(player.CharacterId);

                
                
                
                
                if (character is not null)
                {
                    var viewData = ActorStatePackets.BuildOnPCViewData2315(player, character, equipped, services.GetRequiredService<IGuildEmblemRepository>());
                    await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData, writer => writer.WriteBytes(viewData));
                }
                else if (prebuiltViewData is not null)
                {
                    await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData, writer => writer.WriteBytes(prebuiltViewData));
                }
                
                
                
                await session.SendRmiAsync(ZoneS2COpcodes.OnUnmountFarGlider,
                    writer => writer.WriteBytes(ActorStatePackets.BuildUnmountFarGlider2732(player.EntityId, player.Gold)));
                
                
                await SendMoveSpeedAndMasteryAsync(player.EntityId);

                
                
                
                
                
                
                
                
                
                
                
                if (character is not null)
                {
                    
                    (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, equipped);
                    
                    
                    
                    
                    
                    
                    
                    await effectService.ApplyMapMoveBuffAsync(session, player, character, equipped);
                }

                Logger.LogInformation("Session {Id}: glider arrival re-dressed ({N} worn item(s)) + gear 2498",
                    session.SessionId, equipped.Count);
            }
            else
            {
                
                
                
                character = player.Character ?? await characterRepo.GetByIdAsync(player.CharacterId);
                SyncLiveLevel(player, character);
                if (character is not null)
                {
                    var equipped = await inventoryRepo.GetEquipmentAsync(player.CharacterId);
                    var viewData = ActorStatePackets.BuildOnPCViewData2315(player, character, equipped, services.GetRequiredService<IGuildEmblemRepository>());
                    await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData, w => w.WriteBytes(viewData));

                    
                    await SendMoveSpeedAndMasteryAsync(player.EntityId);

                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    (player.WeaponMinDamage, player.WeaponMaxDamage, player.AutoAttackRange) = StatService.WeaponDamageRange(store, equipped);
                    
                    
                    
                    
                    await effectService.ApplyMapMoveBuffAsync(session, player, character, equipped);

                    Logger.LogDebug("Session {Id}: sent self OnPCViewData (2315) ({N} worn item(s)) + gear 2498 on world entry",
                        session.SessionId, equipped.Count);
                }
            }

            
            
            if (!moveSpeedSent)
                await SendMoveSpeedAndMasteryAsync(PlayerStateManager.BuildActorHandle(session.SessionId));

            
            
            var giftBox = services.GetRequiredService<IAccountGiftRepository>();
            var attendanceGift = await SendMapLoadNotifiesAsync(session, player, character, store, giftBox, ct);

            
            
            
            
            
            
            
            
            
            
            
            
            
            var partyService = services.GetRequiredService<PartyService>();
            try
            {
                await partyService.RebindReturningMemberAsync(player);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Session {Id}: failed to bind '{Name}' back to its party", session.SessionId, player.Name);
            }
            var tacticsParty = partyService.GetPlayerParty(session.SessionId);
            if (tacticsParty is not null
                && PartyTacticsMarks.BuildMapEntry2751(tacticsParty, player.MapId, player.CharacterUid) is { } tacticsMarks)
                await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyTacticsTarget, tacticsMarks);

            
            
            
            
            
            
            
            
            
            
            
            IReadOnlyList<AccountGift>? liveGifts = null;
            try
            {
                liveGifts = player.AccountId == 0 ? new List<AccountGift>()
                    : await giftBox.GetLiveAsync(player.AccountId, ServerWallClock.Now().DateTime, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Session {Id}: gift box of account {Account} unreadable; no 2821", session.SessionId, player.AccountId);
            }
            if (liveGifts is not null)
            {
                int beforeAttendance = attendanceGift is null
                    ? liveGifts.Count
                    : liveGifts.Count(g => g.Guid != attendanceGift.Guid);
                await session.SendRmiAsync(ZoneS2COpcodes.NfyAccountGiftCount, AccountGiftPackets.Build2821(beforeAttendance));
                if (beforeAttendance < liveGifts.Count)
                    await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyAccountGiftReceived,
                        AccountGiftPackets.Build2798(liveGifts.Count), ct);
            }
        }
        else
        {
            
            
            Logger.LogWarning("Session {Id}: AnsMapLoading with no PlayerState — no NPCs sent",
                session.SessionId);
        }

        
        
        
        if (!moveSpeedSent)
            await SendMoveSpeedAsync(PlayerStateManager.BuildActorHandle(session.SessionId));

        
        
        
        
        
        
        
        
        
    }

    
    
    
    
    
    
    
    
 /// <summary>
 /// Brings the character row's level up to the live <see cref="RO2.Server.Zone.Models.PlayerState.Level"/>
 /// before the self 2315 and the 2498 are built from the row.
 /// </summary>
 /// <remarks>
 /// A level-up moves only PlayerState.Level (CombatService.GrantExpAsync); the world-entry snapshot
 /// (<c>PlayerState.Character</c>) keeps the login level. The 2315 carries the level in jobBits bits 21-26,
 /// which the self apply copies to CharInfo+156 (the level the character panel shows), so a snapshot level
 /// would put the panel back to the login level on every later map load. WarpService does the same for its
 /// cross-map 2498.
 /// </remarks>
    internal static void SyncLiveLevel(RO2.Server.Zone.Models.PlayerState player, RO2.Domain.Entities.Character? character)
    {
        if (character is not null && player.Level != 0 && character.Level != player.Level)
            character.Level = player.Level;
    }

 /// <summary>
 /// The server notifies retail puts in every map-load reply after the 2498, in its order:
 /// 2811 -> 3025 -> [3027] -> 2529 -> [2879] -> 2836 -> 2837.
 /// </summary>
 /// <remarks>
 /// <para>
 /// Retail sends them AFTER the client's 1304, on a login and on every warp, within a few seconds of it
 /// and before the next 2311: 2836 and 2837 always, 2811, 2529 and 3025 on most. The full-form replies run
 /// 2986, 2499, 2498, 2492, 2811, 3025, 2529, then 2836 and 2837 (the answers to 1340/1442 may sit in
 /// between). Before the 1304 none of them is sent. This
 /// server used to send 3025/2529/2879/2836/2837 in WorldEntryService's burst BEFORE the 1304 as well, which
 /// doubled them on a login and a glider warp; that copy, and WarpService's 2879, are removed.
 /// </para>
 /// <para>
 /// Bodies. 2811 is the same server clock the tick sends (<see cref="ServerTimeCodec.BuildServerTimeBody"/>
 /// on <see cref="ServerWallClock"/>). 3025 is the empty cooldown list <c>01 00</c> (compact count 0); a
 /// non-empty list holds 12-byte <c>[u32 skill-level id][u32 remaining][u32 full cooldown]</c> records,
 /// and PlayerState.SkillCooldowns keeps only an expiry per cooldown group, not the skill-level id or the
 /// total, so the list cannot be filled from it. 2529 is the transform-charge gauge from TransChargePackets
 /// (1800 s when there is no transform). 2879 is gated exactly as the world-entry copy
 /// (<see cref="ColosseumService.MatchesServed"/> is false, so it does not go out). 2836 is the spinel-revival
 /// cooldown as an F32, 0.0 here (this server has no revival cooldown), and 2837 is seven zero
 /// bytes.
 /// </para>
 /// </remarks>
    private async Task<AccountGift?> SendMapLoadNotifiesAsync(ProudSession session, RO2.Server.Zone.Models.PlayerState player,
        RO2.Domain.Entities.Character? character, RO2.GameData.GameDataStore store, IAccountGiftRepository giftBox,
        CancellationToken ct)
    {
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyServerTime,
            ServerTimeCodec.BuildServerTimeBody(ServerWallClock.Now()));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfySkillCoolTime, EmptySkillCoolTimeList);
        
        
        
        
        
        AttendanceDayGate.AttendedDay? attended = null;
        try
        {
            attended = await AttendanceDayGate.TryAttendTodayAsync(giftBox, player.AccountId, ServerWallClock.Now().DateTime, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Session {Id}: attendance of account {Account} could not be recorded; no 3027",
                session.SessionId, player.AccountId);
        }
        if (attended is not null)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyShowAttendanceState, attended.Popup3027);
            Logger.LogInformation("{Name}: attendance day {Days} of account {Account}, reward {Reward} filed in the gift box",
                player.Name, attended.AttendedDays, player.AccountId,
                attended.Reward is { } g ? $"{g.Count}x {g.ItemId}" : "none");
        }
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnNotifyTransChargeGague,
            TransChargePackets.BuildTransChargeGauge2529(
                TransChargePackets.CurrentChargeSeconds(player.CharacterId, System.DateTime.UtcNow)));
        if (ColosseumService.MatchesServed && character is not null
            && ColosseumSchedule.AnnouncesOnEntry(ServerWallClock.Now(), character.Level, store.GetMap(player.MapId)))
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyColosseumStart, ColosseumSchedule.Build2879(true));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfySpinelRevivalInfo, SpinelRevivalNoCooldown);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyPCCafeRevivalInfo, PcCafeRevivalNone);
        return attended?.Reward;
    }
}
