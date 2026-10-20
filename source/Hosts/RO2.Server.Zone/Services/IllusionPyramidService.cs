using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Illusion Pyramid run: handles the draw (1617), the result fetch (1618), the finish (1619) and the state
/// request (1616), reports balances (2906), and settles a run when the player leaves the window or the map.
/// </summary>
/// <remarks>
/// <para>
/// A draw starts a run (paid with a Phantom Stone, or with Mystical or Bright stones) or advances it one floor.
/// The result of each draw is rolled when the draw is accepted and held until it is fetched. A result that ends
/// the run is settled when fetched, or at the finish. Item rewards go to the bag, or to the mailbox when the bag is
/// full. Mileage rewards are paid into the Mystical or Bright balance.
/// </para>
/// <para>
/// Draws are refused while the previous one is unfetched, after the speed limit, or when the run state does not
/// allow them. The refusal code states the reason.
/// </para>
/// <para>
/// When the player leaves while the pyramid has been used since the last departure, the open run is settled and
/// the window is closed by an unsolicited finish message. A death closes it the same way.
/// </para>
/// </remarks>
public static class IllusionPyramidService
{
 /// <summary>1617 mode of a Phantom Stone start.</summary>
    public const byte ModeStoneStart = 0x10;

 /// <summary>1617 mode of a Mystical or Bright start.</summary>
    public const byte ModeMileageStart = 0x11;

 /// <summary>1617 mode of a draw that advances the open run.</summary>
    public const byte ModeAdvance = 0x13;

    
    
 /// <summary>Result code 0: the request is accepted (also the finish answer when nothing went wrong).</summary>
    public const ushort ResultOk = 0;

 /// <summary>Result 29001: a Phantom Stone is needed to start a run.</summary>
    public const ushort ResultNeedPhantomStone = 29001;
 /// <summary>Result 29002: the draw came too soon after the previous one.</summary>
    public const ushort ResultSpeedAbnormal = 29002;
 /// <summary>Result 29006: abnormal information in the draw request.</summary>
    public const ushort ResultAbnormalInformation = 29006;
 /// <summary>Result 29007: not exploring. Also the result when no draw is held.</summary>
    public const ushort ResultNotExploring = 29007;
 /// <summary>Result 29008: server error.</summary>
    public const ushort ResultSystemError = 29008;
 /// <summary>Result 29009: a draw is already waiting for its result.</summary>
    public const ushort ResultAlreadyExploring = 29009;
 /// <summary>Result 29010: the draw is not on the expected floor.</summary>
    public const ushort ResultWrongFloor = 29010;
 /// <summary>Result 29011: the run cannot advance; its last result ended it.</summary>
    public const ushort ResultCannotAdvance = 29011;
 /// <summary>Result 29016: the run was canceled by a map move. Sent with the close before an in-place map load.</summary>
    public const ushort ResultCanceledByMovement = 29016;
 /// <summary>Result 29012: the run was canceled by death. Sent with the close at death.</summary>
    public const ushort ResultCanceledByDeath = 29012;
 /// <summary>Result 29005: the run was canceled by combat. Sent after combat hits that involve the player while the pyramid is in use.</summary>
    public const ushort ResultCanceledByCombat = 29005;

 /// <summary>Byte 2 of an unsolicited finish message: 1 closes the pyramid window.</summary>
    public const byte CloseWindowFlag = 1;

 /// <summary>
 /// Speed limit in milliseconds between two accepted draws. A draw sooner than this is refused with
 /// <see cref="ResultSpeedAbnormal"/>.
 /// </summary>
    public const long SpeedWindowMs = 2000;

 /// <summary>Length of the result message body.</summary>
    public const int GetResultBytes = 12;

 /// <summary>Length of the 2907 draw answer and the 2909 finish answer: <c>[u16 result][u8 close flag]</c>.</summary>
    public const int ResultBodyBytes = 3;

 /// <summary>Offset of the close flag in a 2907 or 2909 body.</summary>
    public const int CloseWindowFlagOffset = 2;

 /// <summary>Length of a 1617 draw request body: <c>[u8 stage][u8 mode][u8 cost flags][u32 cost ref]</c>.</summary>
    public const int DrawRequestBytes = 7;

 /// <summary>Offset of the u8 stage in a 1617 body.</summary>
    public const int DrawStageOffset = 0;

 /// <summary>Offset of the u8 mode in a 1617 body.</summary>
    public const int DrawModeOffset = 1;

 /// <summary>Offset of the u8 cost flags in a 1617 body.</summary>
    public const int DrawCostFlagsOffset = 2;

 /// <summary>Offset of the u32 cost reference in a 1617 body: the Mileage_ID of a Mystical or Bright start.</summary>
    public const int DrawCostRefOffset = 3;

 /// <summary>Length of the u32 cost reference in a 1617 body.</summary>
    public const int DrawCostRefBytes = 4;

 /// <summary>Offset of the u8 reward kind in a 2908 body.</summary>
    public const int ResultKindOffset = 3;

 /// <summary>Offset of the u32 item id or Mileage_ID in a 2908 body.</summary>
    public const int ResultIdOffset = 4;

 /// <summary>Offset of the u8 count in a 2908 body.</summary>
    public const int ResultCountOffset = 8;

 /// <summary>Offset of the u8 floor in a 2908 body.</summary>
    public const int ResultFloorOffset = 9;

 /// <summary>Offset of the u8 slot in a 2908 body.</summary>
    public const int ResultSlotOffset = 10;

 /// <summary>Offset of the u8 flags (end, broadcast) in a 2908 body.</summary>
    public const int ResultFlagsOffset = 11;

 /// <summary>2908 reward kind 0: mileage, with its Mileage_ID in the id field.</summary>
    public const byte ResultKindMileage = 0;

 /// <summary>2908 reward kind 1: an item, with its item id in the id field.</summary>
    public const byte ResultKindItem = 1;

 /// <summary>Bit 0 of the 2908 flags: the result ends the run.</summary>
    public const byte ResultFlagEnd = 1;

 /// <summary>Bit 1 of the 2908 flags: the result is broadcast to the world.</summary>
    public const byte ResultFlagBroadcast = 2;

 /// <summary>InventoryItem.StorageType of an item held in the bag.</summary>
    private const byte StorageBag = 0;

 /// <summary>Largest stack count the use-item message (2329) carries in its u8 count field.</summary>
    private const int UseItemCountMax = byte.MaxValue;

 /// <summary>MailMessage.Kind of the system mail that carries a reward the bag could not take.</summary>
    private const byte RewardMailKind = 22;

 /// <summary>Client string table id shown as the sender of the reward mail.</summary>
    private const string RewardMailSenderStringId = "11809";

 /// <summary>Client string table id shown as the title of the reward mail.</summary>
    private const string RewardMailTitleStringId = "11820";

 /// <summary>Client string table id shown as the body of the reward mail.</summary>
    private const string RewardMailBodyStringId = "11821";

 /// <summary>InventoryItem.Durability meaning "not set": the template durability applies.</summary>
    private const short DurabilityUnset = -1;

 /// <summary>Offset of the u8 bag index in a 126-byte item record.</summary>
    private const int ItemRecordBagOffset = 15;

 /// <summary>Offset of the u8 slot index in a 126-byte item record.</summary>
    private const int ItemRecordSlotOffset = 16;

 /// <summary>Bag index the mailed item 2333 record names; the item is in no bag cell.</summary>
    private const byte MailedItemBag = 0;

 /// <summary>Slot index the mailed item 2333 record names; see <see cref="MailedItemBag"/>.</summary>
    private const byte MailedItemSlot = 0;

 /// <summary>A parsed 1617 draw request.</summary>
    public readonly record struct DrawRequest(byte Stage, byte Mode, byte CostFlags, uint CostRef);

 /// <summary>Parses a 1617 body. Returns false when it is shorter than <see cref="DrawRequestBytes"/> bytes.</summary>
    public static bool TryParseDraw(ReadOnlySpan<byte> body, out DrawRequest request)
    {
        request = default;
        if (body.Length < DrawRequestBytes) return false;
        request = new DrawRequest(body[DrawStageOffset], body[DrawModeOffset], body[DrawCostFlagsOffset],
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(DrawCostRefOffset, DrawCostRefBytes)));
        return true;
    }

    
 /// <summary>
 /// Handles a 1617 draw: checks it and, on success, starts or advances the run with a rolled reward. Returns the
 /// 2907 result code. The bag or the balance is read only for a start.
 /// </summary>
    public static async Task<ushort> DrawAsync(PlayerState player, DrawRequest req, IServiceProvider services, ILogger logger,
        CancellationToken ct)
    {
        var slot = IllusionPyramidRunStore.For(player);
        
        
        slot.LastRequestMs = Environment.TickCount64;
        await slot.Gate.WaitAsync(ct);
        try
        {
            slot.UsedSinceLeave = true;
            long now = Environment.TickCount64;
            ushort code = Check(slot, req, now);
            if (code != ResultOk) return code;

            if (req.Mode != ModeAdvance)
            {
                try
                {
                    code = await CheckEntryCanPayAsync(player, IllusionPyramidCatalog.KindOf(req.Mode, req.CostRef), services, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "{Name}: illusion pyramid entry check failed", player.Name);
                    return ResultSystemError;
                }
                if (code != ResultOk) return code;
            }

            Accept(slot, req, IllusionPyramidCatalog.Roll(req.Stage, Random.Shared.Next), now);
            return ResultOk;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

 /// <summary>
 /// Checks the run state for a draw, in order. Returns 0 when the draw may continue to the entry check or be
 /// accepted. Pure over the slot, so it can be tested without a database.
 /// </summary>
    internal static ushort Check(IllusionPyramidRunStore.Slot slot, DrawRequest req, long nowMs)
    {
        if (slot.LastAcceptedMs is long last && nowMs - last < SpeedWindowMs) return ResultSpeedAbnormal;
        var run = slot.Run;
        if (run is { Fetched: false }) return ResultAlreadyExploring;
        switch (req.Mode)
        {
            case ModeStoneStart or ModeMileageStart:
                var kind = IllusionPyramidCatalog.KindOf(req.Mode, req.CostRef);
                if (kind == IllusionPyramidCatalog.EntryKind.None || req.Stage != IllusionPyramidCatalog.EntryFloor(kind))
                    return ResultAbnormalInformation;
                break;
            case ModeAdvance:
                if (run is null) return ResultWrongFloor;
                break;
            default:
                return ResultAbnormalInformation;
        }
        if (run is { Ended: true }) return ResultCannotAdvance;
        if (req.Mode == ModeAdvance && req.Stage != run!.Floor + 1) return ResultWrongFloor;
        
        if (req.Stage > IllusionPyramidCatalog.TopFloor) return ResultWrongFloor;
        return ResultOk;
    }

 /// <summary>
 /// Checks that the entry of a start can be paid: a Phantom Stone in the bag, or the Mystical or Bright balance.
 /// Returns <see cref="ResultNeedPhantomStone"/> or <see cref="ResultAbnormalInformation"/> otherwise.
 /// </summary>
    private static async Task<ushort> CheckEntryCanPayAsync(PlayerState player, IllusionPyramidCatalog.EntryKind kind,
        IServiceProvider services, CancellationToken ct)
    {
        if (kind == IllusionPyramidCatalog.EntryKind.PhantomStone)
        {
            var items = await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(player.CharacterId);
            return IllusionPyramidGetMyStateHandler.CountInBag(items, IllusionPyramidCatalog.PhantomStoneItemId) == 0
                ? ResultNeedPhantomStone : ResultOk;
        }
        var state = await Repository(services).GetAsync(player.CharacterId, ct);
        int balance = kind == IllusionPyramidCatalog.EntryKind.Mystical ? state.Mystical : state.Bright;
        return balance < IllusionPyramidCatalog.MileageEntryCost ? ResultAbnormalInformation : ResultOk;
    }

 /// <summary>
 /// Accepts a draw: a start opens a new run, replacing any open one, and an advance moves the run up one floor.
 /// The rolled <paramref name="reward"/> is held until it is fetched.
 /// </summary>
    internal static void Accept(IllusionPyramidRunStore.Slot slot, DrawRequest req, IllusionPyramidCatalog.Reward reward, long nowMs)
    {
        if (req.Mode != ModeAdvance || slot.Run is null)
            slot.Run = new IllusionPyramidRunStore.Run { Entry = IllusionPyramidCatalog.KindOf(req.Mode, req.CostRef) };
        var run = slot.Run;
        run.Floor = req.Stage;
        run.Held = reward;
        run.Fetched = false;
        run.Ended = false;
        slot.LastAcceptedMs = nowMs;
        slot.DrewSinceFinish = true;
    }

 /// <summary>
 /// Marks the held result as fetched and returns it, or null when no accepted draw is waiting for its result.
 /// </summary>
    internal static IllusionPyramidCatalog.Reward? Fetch(IllusionPyramidRunStore.Slot slot)
    {
        var run = slot.Run;
        if (run is null || run.Fetched || run.Held is null) return null;
        run.Fetched = true;
        run.Ended = run.Held.End;
        return run.Held;
    }

 /// <summary>
 /// Resets the run after its result is settled, or when no draw was held: no run, no pending draw, and the speed
 /// limit cleared.
 /// </summary>
    internal static void Close(IllusionPyramidRunStore.Slot slot)
    {
        slot.Run = null;
        slot.DrewSinceFinish = false;
        slot.LastAcceptedMs = null;
    }

    
 /// <summary>
 /// Handles 1618: sends the held result (2908) and raises the top record to its floor. A result that ends the run
 /// with mileage is settled first. With no accepted draw, sends a result with no code.
 /// </summary>
 /// <remarks>
 /// When the entry of a mileage result can no longer be charged, the result is refused with the entry's code
 /// and the run is dropped. No prize is paid.
 /// </remarks>
    public static async Task GetResultAsync(ProudSession session, PlayerState player, IServiceProvider services, ILogger logger,
        CancellationToken ct)
    {
        var slot = IllusionPyramidRunStore.For(player);
        
        
        slot.LastRequestMs = Environment.TickCount64;
        await slot.Gate.WaitAsync(ct);
        try
        {
            slot.UsedSinceLeave = true;
            var reward = Fetch(slot);
            if (reward is null)
            {
                await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidGetResult, Build2908NoResult(), ct);
                return;
            }
            var run = slot.Run!;

            ushort refused = ResultOk;
            try
            {
                var repo = Repository(services);
                var state = await repo.GetAsync(player.CharacterId, ct);
                if (reward.End && reward.IsMileage && !await SettleAsync(session, player, run, state, services, logger, ct))
                {
                    
                    
                    refused = EntryRefusal(run.Entry);
                }
                else
                {
                    bool dirty = RaiseTopRecord(state, reward.Floor);
                    if (dirty || (reward.End && reward.IsMileage)) await repo.SaveAsync(state, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                
                logger.LogWarning(ex, "{Name}: illusion pyramid state could not be updated at 1618", player.Name);
            }

            if (refused != ResultOk)
            {
                slot.Run = null;
                await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidGetResult, Build2908Code(refused), ct);
                return;
            }
            await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidGetResult, Build2908(reward), ct);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    
 /// <summary>
 /// Handles 1619: closes the run. Settles an item result that is still held, and answers 0. With no accepted draw
 /// since the last finish, answers <see cref="ResultNotExploring"/>.
 /// </summary>
 /// <remarks>
 /// When the bag is full, the item is mailed and the finish answers <see cref="ResultRewardMailed"/>.
 /// </remarks>
    public static async Task FinishAsync(ProudSession session, PlayerState player, IServiceProvider services, ILogger logger,
        CancellationToken ct)
    {
        var slot = IllusionPyramidRunStore.For(player);
        
        
        slot.LastRequestMs = Environment.TickCount64;
        await slot.Gate.WaitAsync(ct);
        try
        {
            slot.UsedSinceLeave = true;
            slot.LastAcceptedMs = null;
            var run = slot.Run;
            if (!slot.DrewSinceFinish)
            {
                Close(slot);
                await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidFinish, Build2909(ResultNotExploring), ct);
                return;
            }

            ushort code = ResultOk;
            if (run?.Held is { } held)
            {
                (code, bool settled) = await SettleHeldAsync(session, player, run, held, services, logger, ct);
                if (!settled)
                {
                    
                    await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidFinish, Build2909(code), ct);
                    return;
                }
            }

            Close(slot);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidFinish, Build2909(code), ct);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

 /// <summary>
 /// Settles the held reward as a finish does: charges the entry, then lands an item in the bag or mails it, or pays
 /// a mileage prize. Returns the finish code and whether the run may be closed.
 /// </summary>
    private static async Task<(ushort Code, bool Settled)> SettleHeldAsync(ProudSession session, PlayerState player,
        IllusionPyramidRunStore.Run run, IllusionPyramidCatalog.Reward held, IServiceProvider services, ILogger logger,
        CancellationToken ct)
    {
        
        
        run.Fetched = true;
        if (!held.IsMileage && await BagIsFullForAsync(player, held, services))
        {
            
            
            
            ushort code = ResultSystemError;
            try
            {
                var repo = Repository(services);
                var state = await repo.GetAsync(player.CharacterId, ct);
                RaiseTopRecord(state, held.Floor);
                run.Held = null;
                bool charged = await ChargeEntryAsync(session, player, run.Entry, state, services, ct);
                await repo.SaveAsync(state, ct);
                code = ResultOk;
                if (charged)
                {
                    await MailRewardAsync(session, player, held, services, ct);
                    code = ResultRewardMailed;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "{Name}: illusion pyramid reward {Count}x {Item} could not be mailed",
                    player.Name, held.Count, held.ItemId);
            }
            return (code, true);
        }
        try
        {
            var repo = Repository(services);
            var state = await repo.GetAsync(player.CharacterId, ct);
            RaiseTopRecord(state, held.Floor);
            await SettleAsync(session, player, run, state, services, logger, ct);
            await repo.SaveAsync(state, ct);
            return (ResultOk, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: illusion pyramid run could not be settled", player.Name);
            return (ResultSystemError, false);
        }
    }

    
 /// <summary>
 /// Sends the unsolicited close before a departure, when the pyramid has been used since the last departure. Settles
 /// the held reward, closes the run and sends the close message, which ends the use. Call it before the departure
 /// message.
 /// </summary>
    public static Task CloseOnDepartureAsync(ProudSession session, PlayerState player, IServiceProvider services,
        ILogger logger, CancellationToken ct)
        => LeaveAsync(session, player, services, logger, ResultOk, endsUse: true, ct);

 /// <summary>
 /// Sends the unsolicited close before an in-place map load, with <see cref="ResultCanceledByMovement"/>. The use
 /// is not ended, so a later departure still gets its close.
 /// </summary>
    public static Task CloseOnMapLoadAsync(ProudSession session, PlayerState player, IServiceProvider services,
        ILogger logger, CancellationToken ct)
        => LeaveAsync(session, player, services, logger, ResultCanceledByMovement, endsUse: false, ct);

 /// <summary>
 /// Sends the unsolicited close at death, with <see cref="ResultCanceledByDeath"/>. It ends the use.
 /// </summary>
    public static Task CloseOnDeathAsync(ProudSession session, PlayerState player, IServiceProvider services,
        ILogger logger, CancellationToken ct)
        => LeaveAsync(session, player, services, logger, ResultCanceledByDeath, endsUse: true, ct);

 /// <summary>
 /// Sends the unsolicited combat close after a combat hit that involves the player while the pyramid is in use. It
 /// settles nothing and does not change the run.
 /// </summary>
    public static async Task NotifyCombatAsync(ProudSession session, PlayerState player, ILogger logger, CancellationToken ct)
    {
        var slot = IllusionPyramidRunStore.For(player);
        await slot.Gate.WaitAsync(ct);
        try
        {
            if (!slot.UsedSinceLeave) return;
            await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidFinish, Build2909Close(ResultCanceledByCombat), ct);
            logger.LogDebug("{Name}: illusion pyramid combat notice (29005)", player.Name);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private static async Task LeaveAsync(ProudSession session, PlayerState player, IServiceProvider services, ILogger logger,
        ushort code, bool endsUse, CancellationToken ct)
    {
        var slot = IllusionPyramidRunStore.For(player);
        await slot.Gate.WaitAsync(ct);
        try
        {
            if (!BeginLeave(slot, endsUse)) return;

            bool settled = true;
            if (slot.Run is { Held: { } held } run)
            {
                
                try
                {
                    (_, settled) = await SettleHeldAsync(session, player, run, held, services, logger, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    settled = false;
                    logger.LogWarning(ex, "{Name}: illusion pyramid run could not be settled at a leave", player.Name);
                }
            }
            
            
            if (settled) Close(slot);

            
            
            await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidFinish, Build2909Close(code), ct);
            logger.LogDebug("{Name}: illusion pyramid window closed on leaving (code {Code})", player.Name, code);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

 /// <summary>
 /// Decides whether a leave needs a close: true when the pyramid was used since the last departure close. Clears
 /// that mark when the leave is a departure. Pure over the slot.
 /// </summary>
    internal static bool BeginLeave(IllusionPyramidRunStore.Slot slot, bool endsUse)
    {
        if (!slot.UsedSinceLeave) return false;
        if (endsUse) slot.UsedSinceLeave = false;
        return true;
    }

 /// <summary>
 /// Handles 1616: marks the pyramid as used, which arms the close at the next leave.
 /// </summary>
    public static async Task NoteStateRequestAsync(PlayerState player, CancellationToken ct)
    {
        var slot = IllusionPyramidRunStore.For(player);
        
        
        slot.LastRequestMs = Environment.TickCount64;
        await slot.Gate.WaitAsync(ct);
        try
        {
            slot.UsedSinceLeave = true;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

 /// <summary>Result 29004: the reward was sent to the mailbox.</summary>
    public const ushort ResultRewardMailed = 29004;

 /// <summary>
 /// Mails an item reward that the bag cannot take, then sends the bag update that names the mailed item.
 /// </summary>
    private static async Task MailRewardAsync(ProudSession session, PlayerState player, IllusionPyramidCatalog.Reward reward,
        IServiceProvider services, CancellationToken ct)
    {
        services.GetRequiredService<GameDataStore>().Items.TryGetValue(reward.ItemId, out var template);
        var mails = services.GetRequiredService<IMailRepository>();
        var mail = new MailMessage
        {
            ReceiverCharId = player.CharacterId,
            ReceiverName = player.Name,
            SenderName = RewardMailSenderStringId,
            Title = RewardMailTitleStringId,
            Body = RewardMailBodyStringId,
            Kind = RewardMailKind,
            SentAt = DateTime.UtcNow,
            AttachedItemUid = await services.GetRequiredService<IInventoryRepository>().GetNextIdAsync(),
            AttachedItemId = reward.ItemId,
            AttachedItemCount = reward.Count,
            AttachedItemDurability = template is null ? DurabilityUnset : (short)Math.Min(template.Durability, (uint)short.MaxValue),
        };
        await mails.AddAsync(mail);
        var record = WorldEntryService.BuildItemRecord126(MailService.AttachmentAsBagRow(mail, player.CharacterId, mail.AttachedItemUid, MailedItemSlot));
        record[ItemRecordBagOffset] = MailedItemBag;
        record[ItemRecordSlotOffset] = MailedItemSlot;
        await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
            LootNotifyPackets.BuildPickupItem2333(record, ItemPickUpHandler.NoFreeBagCell), ct);
    }

    
 /// <summary>
 /// Returns the Mystical and Bright balances and the top record for the 2906 answer. A character that never explored,
 /// or a failed read, returns zeros.
 /// </summary>
    public static async Task<(ushort Mystical, ushort Bright, byte TopRecord)> GetStateAsync(PlayerState player,
        IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        try
        {
            var s = await Repository(services).GetAsync(player.CharacterId, ct);
            return ((ushort)Math.Clamp(s.Mystical, 0, IllusionPyramidCatalog.MileageCap),
                (ushort)Math.Clamp(s.Bright, 0, IllusionPyramidCatalog.MileageCap), s.TopRecord);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: illusion pyramid state could not be read", player.Name);
            return (0, 0, 0);
        }
    }

    
 /// <summary>
 /// Settles the held reward into <paramref name="state"/>, which the caller saves: charges the entry, then pays the
 /// mileage or lands the item. Returns false, with nothing paid, when the entry cannot be charged.
 /// </summary>
    private static async Task<bool> SettleAsync(ProudSession session, PlayerState player, IllusionPyramidRunStore.Run run,
        IllusionPyramidStateDocument state, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var reward = run.Held!;
        bool charged = await ChargeEntryAsync(session, player, run.Entry, state, services, ct);
        
        run.Held = null;
        if (!charged)
        {
            logger.LogInformation("{Name}: illusion pyramid {Entry} entry could not be charged; result {Count}x {Item} is void",
                player.Name, run.Entry, reward.Count, reward.ItemId);
            return false;
        }
        if (reward.IsMileage)
        {
            ApplyMileage(state, reward.MileageId, reward.Count);
            logger.LogInformation("{Name}: illusion pyramid pays {Count} mileage {Mileage} (Mystical {Mystical}, Bright {Bright})",
                player.Name, reward.Count, reward.MileageId, state.Mystical, state.Bright);
            return true;
        }
        await GrantItemAsync(session, player, reward, services, logger, ct);
        return true;
    }

 /// <summary>
 /// Returns the code for a result whose entry cannot be charged: <see cref="ResultNeedPhantomStone"/> for a Phantom
 /// Stone entry, <see cref="ResultAbnormalInformation"/> for a Mystical or Bright entry.
 /// </summary>
    internal static ushort EntryRefusal(IllusionPyramidCatalog.EntryKind entry)
        => entry == IllusionPyramidCatalog.EntryKind.PhantomStone ? ResultNeedPhantomStone : ResultAbnormalInformation;

 /// <summary>
 /// Takes the entry of a run: one Phantom Stone from the bag, or the Mystical or Bright cost from the balance.
 /// Returns false when it cannot be paid.
 /// </summary>
    private static async Task<bool> ChargeEntryAsync(ProudSession session, PlayerState player, IllusionPyramidCatalog.EntryKind entry,
        IllusionPyramidStateDocument state, IServiceProvider services, CancellationToken ct)
    {
        switch (entry)
        {
            case IllusionPyramidCatalog.EntryKind.PhantomStone:
                var inventory = services.GetRequiredService<IInventoryRepository>();
                var stack = (await inventory.GetByCharacterAsync(player.CharacterId))
                    .Where(i => i.StorageType == StorageBag && i.Slot >= 0 && i.ItemId == IllusionPyramidCatalog.PhantomStoneItemId && i.Count > 0)
                    .OrderBy(i => i.Slot)
                    .FirstOrDefault();
                if (stack is null) return false;
                var (bag, slot) = ItemWorkbench.ClientBagSlot(stack);
                int left = stack.Count - 1;
                if (left <= 0) await inventory.RemoveItemAsync(stack.Id);
                else { stack.Count = left; await inventory.UpdateItemAsync(stack); }
                await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
                    CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, stack.Id, (byte)Math.Clamp(left, 0, UseItemCountMax)), ct);
                return true;
            case IllusionPyramidCatalog.EntryKind.Mystical:
                if (state.Mystical < IllusionPyramidCatalog.MileageEntryCost) return false;
                state.Mystical -= IllusionPyramidCatalog.MileageEntryCost;
                return true;
            case IllusionPyramidCatalog.EntryKind.Bright:
                if (state.Bright < IllusionPyramidCatalog.MileageEntryCost) return false;
                state.Bright -= IllusionPyramidCatalog.MileageEntryCost;
                return true;
            default:
                return false;
        }
    }

 /// <summary>Lands an item reward in the bag and sends the bag update for each stack it reached.</summary>
    private static async Task GrantItemAsync(ProudSession session, PlayerState player, IllusionPyramidCatalog.Reward reward,
        IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var data = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var owned = await inventory.GetByCharacterAsync(player.CharacterId);
        var landing = ItemPickUpHandler.PlanLanding(owned, player.CharacterId, reward.ItemId, reward.Count, data.Items);
        if (landing.Refused)
        {
            logger.LogWarning("{Name}: illusion pyramid reward {Count}x {Item} found no room after its entry was charged",
                player.Name, reward.Count, reward.ItemId);
            await ItemPickUpHandler.SendBagFullAsync(session);
            return;
        }
        var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, player, reward.ItemId, logger);
        if (landed.Count == 0) return;

        await services.GetRequiredService<LootService>().AdvanceCollectQuestsAsync(session, player, reward.ItemId, reward.Count);
        await KharaService.OnHeldItemChangedAsync(session, player, reward.ItemId, services, logger, ct);
        foreach (var stack in landed)
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack)), ct);
        logger.LogInformation("{Name}: illusion pyramid pays {Count}x {Item} (floor {Floor} slot {Slot})",
            player.Name, reward.Count, reward.ItemId, reward.Floor, reward.Slot);
        await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, new[] { reward.ItemId }, services, logger);
    }

    private static async Task<bool> BagIsFullForAsync(PlayerState player, IllusionPyramidCatalog.Reward reward, IServiceProvider services)
    {
        var data = services.GetRequiredService<GameDataStore>();
        var owned = await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(player.CharacterId);
        return ItemPickUpHandler.PlanLanding(owned, player.CharacterId, reward.ItemId, reward.Count, data.Items).Refused;
    }

 /// <summary>
 /// Pays <paramref name="count"/> Mystical (mileage id 1) or Bright (mileage id 2) stones into the balance, up to
 /// <see cref="IllusionPyramidCatalog.MileageCap"/>.
 /// </summary>
    internal static void ApplyMileage(IllusionPyramidStateDocument state, byte mileageId, int count)
    {
        if (mileageId == IllusionPyramidCatalog.MysticalMileageId)
            state.Mystical = Math.Min(IllusionPyramidCatalog.MileageCap, state.Mystical + count);
        else if (mileageId == IllusionPyramidCatalog.BrightMileageId)
            state.Bright = Math.Min(IllusionPyramidCatalog.MileageCap, state.Bright + count);
    }

 /// <summary>Raises the top record to <paramref name="floor"/>. Returns true when it rose.</summary>
    internal static bool RaiseTopRecord(IllusionPyramidStateDocument state, byte floor)
    {
        if (floor <= state.TopRecord) return false;
        state.TopRecord = floor;
        return true;
    }

    private static IIllusionPyramidStateRepository Repository(IServiceProvider services)
        => services.GetRequiredService<IIllusionPyramidStateRepository>();

    
 /// <summary>
 /// Builds the 2907 draw answer: <c>[u16 result][u8 0]</c>.
 /// </summary>
    public static byte[] Build2907(ushort result)
    {
        var body = new byte[ResultBodyBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

 /// <summary>
 /// Builds the 2908 result answer: <c>[u16 0][u8 0][u8 kind][u32 id][u8 count][u8 floor][u8 slot][u8 end or broadcast flags]</c>,
 /// where kind is 1 for an item with its id, or 0 for mileage with its mileage id.
 /// </summary>
    public static byte[] Build2908(IllusionPyramidCatalog.Reward reward)
    {
        var body = new byte[GetResultBytes];
        body[ResultKindOffset] = reward.IsMileage ? ResultKindMileage : ResultKindItem;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(ResultIdOffset), reward.IsMileage ? reward.MileageId : reward.ItemId);
        body[ResultCountOffset] = reward.Count;
        body[ResultFloorOffset] = reward.Floor;
        body[ResultSlotOffset] = reward.Slot;
        body[ResultFlagsOffset] = (byte)((reward.End ? ResultFlagEnd : 0) | (reward.Broadcast ? ResultFlagBroadcast : 0));
        return body;
    }

 /// <summary>Builds the 2908 answer with no result, showing <see cref="ResultNotExploring"/>.</summary>
    public static byte[] Build2908NoResult() => Build2908Code(ResultNotExploring);

 /// <summary>
 /// Builds the 2908 answer that carries only a code: <c>[u16 code]</c>, zero-padded to the usual length.
 /// </summary>
    public static byte[] Build2908Code(ushort code)
    {
        var body = new byte[GetResultBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(body, code);
        return body;
    }

 /// <summary>Builds the 2909 finish answer to 1619: <c>[u16 result][u8 0]</c>.</summary>
    public static byte[] Build2909(ushort result) => Build2907(result);

 /// <summary>
 /// Builds the unsolicited 2909 close: <c>[u16 code][u8 1]</c>. The client hides the pyramid window when byte 2 is 1.
 /// </summary>
    public static byte[] Build2909Close(ushort code)
    {
        var body = Build2907(code);
        body[CloseWindowFlagOffset] = CloseWindowFlag;
        return body;
    }
}
