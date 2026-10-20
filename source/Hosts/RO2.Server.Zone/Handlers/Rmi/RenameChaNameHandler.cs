using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1563 (ReqRenameChaName): renames the character with a name change scroll.
/// </summary>
/// <remarks>
/// Request: scroll uid (u64), current name and new name (wide strings). On success the server consumes the
/// scroll and answers with the updated save data and the self view record, both carrying the new name.
/// </remarks>
public class RenameChaNameHandler : ZoneRmiHandlerBase<RenameChaNameHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRenameChaName;
    public override string Name => "RenameChaName";

    public RenameChaNameHandler(IServiceScopeFactory s, ILogger<RenameChaNameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Source value sent with the scroll consumption update.</summary>
    private const byte UseSourceRenameScroll = 1;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!CharacterRename.TryParse(payload.Span, out var request))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqRenameChaName ({Len} bytes)", player.Name, payload.Length);
            await SendResultAsync(session, CharacterRename.ResultFailed, ct);
            return;
        }

        
        
        
        var (result, scroll, reason) = await CharacterRename.CheckAsync(player, request, services);
        if (result != 0 || scroll is null)
        {
            uint code = result == 0 ? CharacterRename.ResultRefused : result;
            Logger.LogInformation("{Name} rename to '{New}' refused: {Code} ({Reason})", player.Name, request.NewName, code, reason);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsCanRenameChaName, writer => writer.WriteUInt32(code), ct);
            return;
        }

        string oldName = player.Name;
        string newName = request.NewName;

        
        
        var renamed = await services.GetRequiredService<ICharacterRepository>()
            .RenameAsync(player.CharacterId, oldName, newName, ct);
        if (!renamed)
        {
            Logger.LogWarning("{Name} rename to '{New}': character row not updated", oldName, newName);
            await SendResultAsync(session, CharacterRename.ResultFailed, ct);
            return;
        }
        player.Name = newName;
        if (player.Character is { } live) live.Name = newName;

        
        
        var inventory = services.GetRequiredService<IInventoryRepository>();
        int remaining = scroll.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(scroll.Id); remaining = 0; }
        else { scroll.Count = remaining; await inventory.UpdateItemAsync(scroll); }
        var (bag, slot) = ClientBagSlot(scroll);

        var ch = player.Character
            ?? await services.GetRequiredService<ICharacterRepository>().GetByIdAsync(player.CharacterId);
        if (ch is not null)
        {
            ch.Name = newName;
            AnsMapLoadingHandler.SyncLiveLevel(player, ch);   

            
            
            
            var talentPoints = StatService.TalentPointsRemaining(services.GetRequiredService<GameDataStore>(),
                player.Job, player.Level, player.LearnedSkills.Select(kv => (kv.Key, kv.Value)));
            var saveData = PcSaveDataPackets.BuildOnUpdatePCSaveData2487(player.EntityId,
                PcSaveDataPackets.BuildLiveHead205(player, ch, talentPoints));
            await session.SendRmiAsync(ZoneS2COpcodes.OnUpdatePCSaveData, saveData, ct);

            
            
            
            
            var equipped = await inventory.GetEquipmentAsync(player.CharacterId);
            var viewData = ActorStatePackets.BuildOnPCViewData2315(player, ch, equipped, services.GetRequiredService<IGuildEmblemRepository>());
            await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData, w => w.WriteBytes(viewData), ct);
        }

        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            w => w.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, scroll.Id,
                (byte)Math.Clamp(remaining, 0, 255), useSource: UseSourceRenameScroll)), ct);

        Logger.LogInformation("Character {Id} renamed '{Old}' -> '{New}' (scroll {Uid}, {Left} left)",
            player.CharacterId, oldName, newName, scroll.Id, remaining);

        
        
        await SendResultAsync(session, 0, ct);
    }

 /// <summary>Sends the rename result (RMI 2813): zero on success, or <see cref="CharacterRename.ResultFailed"/>.</summary>
    private static Task SendResultAsync(ProudSession session, uint result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsRenameChaName, writer => writer.WriteUInt32(result), ct);

 /// <summary>
 /// Returns the client (bag, slot) cell of a bag item, matching how the world-entry data places it.
 /// </summary>
    private static (byte bag, byte slot) ClientBagSlot(InventoryItem it)
    {
        if (it.StorageType == 1) return (0, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, 49));
        if (it.StorageType == 2) return (5, (byte)Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, BankExpansion.MaxBankSlots - 1));
        if (it.StorageType == 3) return (1, (byte)(70 + Math.Clamp(it.Slot < 0 ? 0 : (int)it.Slot, 0, 2)));
        var flat = it.Slot < 0 ? 0 : (int)it.Slot;
        if (flat < 20) return (1, (byte)(50 + flat));
        var i = flat - 20;
        return ((byte)Math.Clamp(2 + i / 35, 2, 4), (byte)(i % 35));
    }
}
