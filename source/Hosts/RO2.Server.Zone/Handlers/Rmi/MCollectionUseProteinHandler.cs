using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1623 (ReqMCollectionUseProtein): feeds one monster DNA item into its collection.
/// </summary>
/// <remarks>
/// The first DNA registers the collection (2923 result 30008, level 1, exp 0). Each later DNA adds 1 exp and
/// levels the collection up when it reaches CollectMonster.Exp (result 30009). A collection already at
/// SummonLevel 4 is refused with result 30005 and nothing is consumed. A success replies with 2329, 2914, 2913
/// and 2923, in that order. If the named bag cell no longer holds the DNA, the reply is 2923 with result 30004 alone.
/// </remarks>
public class MCollectionUseProteinHandler : ZoneRmiHandlerBase<MCollectionUseProteinHandler>
{
    
    
    private const byte UseSourceCollection = 3;

 /// <summary>
 /// 2923 result 30004, sent when the named bag cell no longer holds this collection's DNA. The reply is
 /// 2923 {npcId, 30004} alone, with no 2329, 2914 or 2913. Without it the request would go unanswered.
 /// </summary>
    internal const ushort ResultNoDnaInCell = 30004;

    public override ushort Opcode => ZoneC2SOpcodes.ReqMCollectionUseProtein;
    public override string Name => "MCollectionUseProtein";
    public MCollectionUseProteinHandler(IServiceScopeFactory s, ILogger<MCollectionUseProteinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 7) return;
        
        
        
        var span = payload.Span;
        byte bag = span[0], slot = span[1];
        uint npcId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(3));

        var store = services.GetRequiredService<GameDataStore>();
        var first = MonsterCollectionRules.Row(store, npcId, 1);
        if (first is null)
        {
            Logger.LogWarning("{Name}: 1623 for unknown monster collection {Npc}", player.Name, npcId);
            return;
        }

        
        
        
        
        
        var inventory = services.GetRequiredService<IInventoryRepository>();
        int flat = BagSlotToFlat(bag, slot);
        var dna = flat < 0 ? null
            : (await inventory.GetByCharacterAsync(player.CharacterId))
                .FirstOrDefault(i => i.StorageType == 0 && i.Slot == flat);
        if (dna is null || dna.ItemId != first.ItemID_Result || dna.Count <= 0)
        {
            Logger.LogInformation("{Name}: 1623 bag {Bag} slot {Slot} does not hold DNA {Dna} for collection {Npc} (found {Item}); answering 30004",
                player.Name, bag, slot, first.ItemID_Result, npcId, dna?.ItemId);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsMCollectionUseProtein, writer => writer.WriteBytes(
                CollectionPackets.BuildCollectionIdResultAck(npcId, ResultNoDnaInCell)));
            return;
        }

        var svc = CollectionService.From(services, Logger);
        var book = await svc.GetStateAsync(player.CharacterId, ct);
        var progressStore = MonsterCollectionProgressStore.From(services, Logger);
        var progress = await progressStore.GetAsync(player.CharacterId, ct);
        var entry = progress.Find(npcId);
        byte level = entry?.Level ?? 1;
        uint exp = entry?.Exp ?? 0;

        ushort result;
        int registered;
        if (!book.RegisteredMonsterIds.Contains(npcId))
        {
            registered = await svc.RegisterMonsterAsync(player.CharacterId, npcId, ct);
            level = 1;
            exp = 0;
            result = MonsterCollectionRules.ResultRegistered;
        }
        else if (level >= MonsterCollectionRules.MaxLevel(store, npcId))
        {
            
            await session.SendRmiAsync(ZoneS2COpcodes.AnsMCollectionUseProtein, writer => writer.WriteBytes(
                CollectionPackets.BuildCollectionIdResultAck(npcId, MonsterCollectionRules.ResultMaxLevel)));
            return;
        }
        else
        {
            registered = book.RegisteredMonsterIds.Count;
            exp++;
            var row = MonsterCollectionRules.Row(store, npcId, level);
            if (row is not null && exp >= row.Exp)
            {
                level++;
                exp = 0;
            }
            result = MonsterCollectionRules.ResultExpGained;
        }

        if (entry is null)
        {
            entry = new MonsterCollectionEntry { NpcId = npcId };
            progress.Entries.Add(entry);
        }
        entry.Level = level;
        entry.Exp = exp;
        await progressStore.SaveAsync(progress, ct);

        
        int remaining = dna.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(dna.Id); remaining = 0; }
        else { dna.Count = remaining; await inventory.UpdateItemAsync(dna); }

        Logger.LogInformation("{Name} feeds DNA to collection {Npc}: level {Level} exp {Exp} (result {Result}, {N} registered)",
            player.Name, npcId, level, exp, result, registered);

        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, dna.Id,
                (byte)Math.Clamp(remaining, 0, 255), UseSourceCollection)));

        var state = MonsterCollectionRules.State(store, npcId, level, exp);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyMCollectionSkill, writer => writer.WriteBytes(
            CollectionPackets.BuildNfyMCollectionSkill(state)));
        await session.SendRmiAsync(ZoneS2COpcodes.NfyMCollection, writer => writer.WriteBytes(
            CollectionPackets.BuildNfyMCollection(registered, MonsterCollectionRules.RateFor(store, registered))));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsMCollectionUseProtein, writer => writer.WriteBytes(
            CollectionPackets.BuildCollectionIdResultAck(npcId, result)));
    }

 /// <summary>Maps a client (bag, slot) pair to the flat bag slot used by the item record. The main bag (container 1,
 /// slots 50-69) maps to 0-19, bags 2-4 to 20 + (bag - 2) * 35 + slot. Returns -1 otherwise.</summary>
    private static int BagSlotToFlat(byte bag, byte slot)
    {
        if (bag == 1) return slot is >= 50 and < 70 ? slot - 50 : -1;
        if (bag is >= 2 and <= 4 && slot < 35) return 20 + (bag - 2) * 35 + slot;
        return -1;
    }
}
