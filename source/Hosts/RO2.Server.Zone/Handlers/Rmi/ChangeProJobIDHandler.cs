using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1494 ReqChangeProJobID (<c>[u64 itemUid][u8 profession]</c>): the player changes profession with a
/// profession-change item, answered by 2683 AnsChangeProJobInfo.</summary>
/// <remarks>
/// <para>
/// <b>When the client sends it.</b> Using a ProJob_Change item (21330007 / 21330008 / 70000019, "가디언의 룬") opens the
/// profession-change window on the client alone (no 1323 goes out). Its confirm callback sends, on OK (4096) and
/// when Can_KharaTransform(1) and the chosen profession is above 0, a 1494 with
/// the item's uid and the profession. NONE family, fixed 9 bytes.
/// </para>
/// <para>
/// <b>The answer</b>, sent back to back: 2379 OnDestroyItem of that uid (the stack as it now stands,
/// count 0, item 21330008), then 2687 x3 (only when the character held the old profession's guide
/// quests), 2686 and 2858, then 2683 (result 0, the new profession, level and exp unchanged: profession at blob+8, level at
/// blob+9, exp 0) and 2682 with the changer's own handle. The client then asks for its skill list (1367 -> 2488).
/// The next 2310 of each character carries the new profession at body[130] and the same level and exp
/// (WorldEntryService.SetCreationProfession). ProJob_Change.ProJobLevel_And_Exp is 0 on every row, which fits
/// keeping them: a character at profession level 50 is still at 50 after the change.
/// </para>
/// <para>
/// <b>2686 NfyChageProJobGuardianSkill</b> is sent after the 2687s, as the original server did: it is what makes the client send
/// that 1367. Its client handler reads nothing from the body and sends 1367.
/// No other message of the burst sends a 1367, so without it the client would not ask for the
/// list. The body is a constant of two empty vectors
/// (QuestProgressPackets.BuildChageProJobGuardianSkill2686).
/// </para>
/// <para>
/// <b>2858 NfyDeleteAcceptedKhara</b> comes next, between the 2686 and the 2683, back to back. Its client
/// handler erases the
/// listed ids from the client's accepted-Khara tree. The list is the accepted Kharas bound to another profession,
/// which the change drops from the stored state too (KharaService.DropProfessionKharasAsync; the 2310 never lists an
/// accepted Khara of a profession other than the holder's). The usual frame carries an empty
/// list, which is what that rule gives a changer holding none; a non-empty
/// list rests on Khara_Info.PC_ProJob and the client's handler. The client sends nothing back.
/// </para>
/// </remarks>
public class ChangeProJobIDHandler : ZoneRmiHandlerBase<ChangeProJobIDHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqChangeProJobID;
    public override string Name => "ChangeProJobID";

 /// <summary>Request layout: <c>[u64 itemUid @0][u8 profession @8]</c>.</summary>
    private const int ProfessionOffset = sizeof(ulong);

 /// <summary>Body length: u64 item uid + u8 profession (9 bytes).</summary>
    private const int BodyLength = ProfessionOffset + sizeof(byte);

 /// <summary>Profession id 0: no profession. Never a valid target of a change.</summary>
    private const byte NoProfession = 0;

 /// <summary>Inventory <c>StorageType</c> of the character's bag (1 = worn equipment, 2 = bank).</summary>
    private const byte BagStorageType = 0;

 /// <summary>Offset of the u8 stack count in a 126-byte item record: <c>[u64 uid @0][u32 itemId @8][u16 durability @12][u8 count @14]</c>.</summary>
    private const int ItemRecordCountOffset = 14;

    public ChangeProJobIDHandler(IServiceScopeFactory s, ILogger<ChangeProJobIDHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength)
        {
            Logger.LogWarning("{Name}: ReqChangeProJobID of {Len} bytes, expected {Expected}; not answered",
                player.Name, payload.Length, BodyLength);
            return;
        }
        if (player.Character is not { } c)
        {
            Logger.LogDebug("{Name}: 1494 with no character snapshot; not answered", player.Name);
            return;
        }
        ulong itemUid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        byte target = payload.Span[ProfessionOffset];

        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var item = await inventory.GetByIdAsync(itemUid);
        byte current = WorldEntryService.CreationProfessionByte(c);

        byte result = ChangeResult(store.ProJobType, store.ProJobChange, player.CharacterId, current, target, item);
        if (result != QuestProgressPackets.ChangeProJobApplied)
        {
            Logger.LogInformation("{Name} profession change {Current} -> {Target} with item {Uid} refused: {Result}",
                player.Name, current, target, itemUid, result);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsChangeProJobInfo, writer => writer.WriteBytes(
                QuestProgressPackets.BuildChangeProJobInfo2683(current, c.JobLevel, c.JobExp, result)));
            return;
        }

        
        
        
        byte jobBefore = c.Job;
        bool healed = WorldEntryService.TryHealStoredCombatClass(c);
        var previousAppearance = WorldEntryService.SetCreationProfession(c, target);
        try
        {
            await services.GetRequiredService<ICharacterRepository>()
                .UpdateAppearanceAndJobAsync(c.Id, c.AppearanceJson, healed ? c.Job : null, ct);
        }
        catch (Exception ex)
        {
            c.AppearanceJson = previousAppearance;
            c.Job = jobBefore;
            Logger.LogWarning(ex, "{Name}: could not store the profession change; nothing consumed or sent", player.Name);
            return;
        }
        if (healed) player.Job = c.Job;

        
        
        int left = item!.Count - 1;
        byte[] record;
        if (left <= 0)
        {
            record = WorldEntryService.BuildItemRecord126(item);
            record[ItemRecordCountOffset] = 0;   // count 0 empties the cell on the client (the record writer clamps to >= 1)
            await inventory.RemoveItemAsync(item.Id);
        }
        else
        {
            item.Count = left;
            await inventory.UpdateItemAsync(item);
            record = WorldEntryService.BuildItemRecord126(item);
        }
        await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
            writer => writer.WriteBytes(ShopItemPackets.BuildDestroyItem2379(player.EntityId, record)));

        
        await SwapGuideQuestsAsync(session, player, store, current, target, services);

        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfyChageProJobGuardianSkill,
            writer => writer.WriteBytes(QuestProgressPackets.BuildChageProJobGuardianSkill2686()));

        
        
        
        var dropped = await KharaService.DropProfessionKharasAsync(player, target, services, Logger, ct);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyDeleteAcceptedKhara, KharaService.BuildNfyDeleteAcceptedKhara2858(dropped), ct);

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsChangeProJobInfo, writer => writer.WriteBytes(
            QuestProgressPackets.BuildChangeProJobInfo2683(target, c.JobLevel, c.JobExp)));

        
        
        var notify = QuestProgressPackets.BuildChangeProJob2682(player.EntityId, target, c.JobLevel);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyChangeProJob, writer => writer.WriteBytes(notify));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyChangeProJob, writer => writer.WriteBytes(notify));

        Logger.LogInformation("{Name} changed profession {Current} -> {Target} with item {Item} (uid {Uid})",
            player.Name, current, target, item.ItemId, itemUid);
    }

 /// <summary>
 /// The 2683 result for a change from <paramref name="current"/> to <paramref name="target"/> with
 /// <paramref name="item"/> (the inventory row of the request's uid, or null), in the order: 1 when no ProJob_Type
 /// row has that id, 3 when it is the current profession, 4 when the uid is not a ProJob_Change item held in this
 /// character's bag; 0 otherwise.
 /// </summary>
 /// <remarks>Only the codes the server can decide from what it holds. Code 2 ("certain actions") and 6 (a job quest
 /// in progress) name conditions this server does not track, so they are never sent.</remarks>
    internal static byte ChangeResult(IEnumerable<ProJobType> professions, IEnumerable<ProJobChange> changeItems,
        uint characterId, byte current, byte target, InventoryItem? item)
    {
        if (target == NoProfession || !professions.Any(r => r.ID == target))
            return QuestProgressPackets.ChangeProJobUnknownProfession;
        if (target == current)
            return QuestProgressPackets.ChangeProJobSameProfession;
        if (item is null || item.CharacterId != characterId || item.StorageType != BagStorageType || item.Count < 1
            || !changeItems.Any(r => r.ItemID == item.ItemId))
            return QuestProgressPackets.ChangeProJobMissingItem;
        return QuestProgressPackets.ChangeProJobApplied;
    }

 /// <summary>
 /// The pairs (old guide quest, new guide quest) of a change from <paramref name="from"/> to <paramref name="to"/>:
 /// ProJob_Type Guide_Quest_ID1..3 of the two rows, column by column, leaving out a column empty on either side.
 /// </summary>
    internal static List<(uint Old, uint New)> GuideQuestPairs(IEnumerable<ProJobType> professions, byte from, byte to)
    {
        var pairs = new List<(uint, uint)>(3);
        var a = professions.FirstOrDefault(r => r.ID == from);
        var b = professions.FirstOrDefault(r => r.ID == to);
        if (a is null || b is null) return pairs;
        foreach (var (o, n) in new[] { (a.Guide_Quest_ID1, b.Guide_Quest_ID1), (a.Guide_Quest_ID2, b.Guide_Quest_ID2),
                     (a.Guide_Quest_ID3, b.Guide_Quest_ID3) })
            if (o != 0 && n != 0 && o != n) pairs.Add((o, n));
        return pairs;
    }

 /// <summary>
 /// Replaces each old-profession guide quest the character holds (in progress) with the new profession's quest of
 /// the same column, in the store and the session, and tells the client with 2687 [old][new]. The quest starts over:
 /// the old one's progress goes with it, as on a give-up (QuestGiveUpHandler).
 /// </summary>
    private async Task SwapGuideQuestsAsync(ProudSession session, PlayerState player, GameDataStore store, byte from, byte to,
        IServiceProvider services)
    {
        var quests = services.GetRequiredService<IQuestRepository>();
        foreach (var (oldQuest, newQuest) in GuideQuestPairs(store.ProJobType, from, to))
        {
            if (!player.ActiveQuests.Remove(oldQuest)) continue;
            player.QuestKillProgress.Remove(oldQuest);
            LootService.ResetExtraKillProgress(player, oldQuest);
            QuestUseItemProgress.Reset(player, oldQuest);
            player.TracedQuests.Remove(oldQuest);
            await quests.GiveUpQuestAsync(player.CharacterId, oldQuest);
            if (player.ActiveQuests.Add(newQuest))
                await quests.AcceptQuestAsync(player.CharacterId, newQuest);
            await session.SendRmiAsync(ZoneS2COpcodes.NfyChangeProJobQuest,
                writer => writer.WriteBytes(QuestProgressPackets.BuildChangeProJobQuest2687(oldQuest, newQuest)));
            Logger.LogInformation("{Name} guide quest {Old} -> {New} with the profession change", player.Name, oldQuest, newQuest);
        }
    }
}
