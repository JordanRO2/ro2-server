using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1586 ReqRenameGuildName: renames the guild, paid for with the rename item named in the request. The request body is the same as for 1585 (<c>GuildRenameRules.TryParse</c>).
/// </summary>
/// <remarks>
/// <para>
/// On success the paying item is consumed (2329 OnUseItem), the result 0 is sent as 2855, the guild members in view receive a 2591 update with the new name and their rank title, and the renamer receives a 2559 guild info with the new name.
/// </para>
/// <para>
/// A refusal is result 44, sent twice, with nothing consumed. The name must also pass the 1585 format rules (<see cref="GuildRenameRules.CheckFormat"/>).
/// </para>
/// </remarks>
public class GuildRenameHandler : ZoneRmiHandlerBase<GuildRenameHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRenameGuildName;
    public override string Name => "GuildRename";
    public GuildRenameHandler(IServiceScopeFactory s, ILogger<GuildRenameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>
 /// ItemInfo Item_Type of the guild rename item. The named item must be a rename item of this type.
 /// </summary>
    private const byte GuildRenameItemType = 71;

 /// <summary>Use source value sent in the 2329 consumption of the rename item.</summary>
    private const byte RenameUseSource = 3;

 /// <summary>
 /// Serialises the item check with its payment, so one rename item cannot pay for two renames sent at once. The name itself is guarded by <c>GuildService.RenameAsync</c>.
 /// </summary>
    private static readonly SemaphoreSlim RenameGate = new(1, 1);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var repo = services.GetRequiredService<IGuildStore>();
        await GuildService.Instance.InitAsync(repo);
        var emblems = services.GetRequiredService<IGuildEmblemRepository>();
        await emblems.InitAsync();   

        if (!GuildRenameRules.TryParse(payload.Span, out var itemUid, out var name))
        {
            await SendRefusalAsync(session);
            return;
        }

        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild == null || guild.MasterCharId != player.CharacterId)
        {
            Logger.LogDebug("{Name}: guild rename to '{GuildName}' refused, not a guild master", player.Name, name);
            await SendRefusalAsync(session);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        
        
        
        
        string oldName;
        RO2.Domain.Entities.InventoryItem scroll;
        int remaining;
        await RenameGate.WaitAsync(ct);
        try
        {
            
            var item = await inventory.GetByIdAsync(itemUid);
            if (item is null || item.CharacterId != player.CharacterId || item.StorageType != 0 || item.Count < 1
                || store.GetItem(item.ItemId) is not { Item_Type: GuildRenameItemType })
            {
                Logger.LogDebug("{Name}: guild rename to '{GuildName}' refused, uid {Uid} is not an owned rename scroll",
                    player.Name, name, itemUid);
                await SendRefusalAsync(session);
                return;
            }
            scroll = item;

            
            var format = GuildRenameRules.CheckFormat(name);
            if (format != GuildRenameRules.Ok)
            {
                Logger.LogDebug("{Name}: guild rename to '{GuildName}' refused, format {Check}", player.Name, name, format);
                await SendRefusalAsync(session);
                return;
            }

            
            
            
            
            remaining = Math.Max(0, scroll.Count - 1);
            var (refusal, previous) = await GuildService.Instance.RenameAsync(guild, name, async () =>
            {
                if (remaining == 0) await inventory.RemoveItemAsync(itemUid);
                else { scroll.Count = remaining; await inventory.UpdateItemAsync(scroll); }
            });
            if (refusal != GuildService.RenameRefusal.None)
            {
                Logger.LogDebug("{Name}: guild rename to '{GuildName}' refused, {Refusal}", player.Name, name, refusal);
                await SendRefusalAsync(session);
                return;
            }
            oldName = previous;
        }
        finally { RenameGate.Release(); }

        
        var (bagIx, slotIx) = ClientBagSlot(scroll);
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            writer => writer.WriteBytes(CombatPackets.BuildUseItem2329(
                actorHandle: player.EntityId,
                slotIndex: slotIx,
                bagId: bagIx,
                itemUid: itemUid,
                remainingCount: (byte)Math.Clamp(remaining, 0, 255),
                useSource: RenameUseSource)));
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsRenameGuildName, writer => writer.WriteUInt16(GuildRenameRules.Ok));

        
        
        
        
        
        
        GuildMember[] members;
        lock (guild.Gate) members = guild.Members.ToArray();
        foreach (var m in members)
        {
            var online = PlayerManager.GetPlayerByCharacterId(m.CharId);
            if (online is not { MapId: not 0 }) continue;
            var view = PcViewInfoPackets.BuildNfyPcViewInfoUpdate(online.EntityId, guild, m.CharId, emblems);
            await Broadcast.BroadcastToMapAsync(online.MapId, ZoneS2COpcodes.NfyPCViewInfoUpdate,
                writer => writer.WriteBytes(view));
        }

        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsMyGuildInfo, (ReadOnlyMemory<byte>)GuildRosterPackets.BuildAnsMyGuildInfo(
            guild, player.CharacterId, cid => GuildRosterPackets.IsOnline(PlayerManager, cid), emblems, store.GuildExpTable));

        Logger.LogInformation("{Name} renamed guild {GuildId} '{Old}' -> '{New}' with item {ItemId} (uid {Uid}), {Remaining} left",
            player.Name, guild.Id, oldName, name, scroll.ItemId, itemUid, remaining);
    }

 /// <summary>Sends the refusal: 2855 with result 44, sent twice.</summary>
    private static async Task SendRefusalAsync(ProudSession session)
    {
        for (int i = 0; i < 2; i++)
            await session.SendRmiAsync(ZoneS2COpcodes.AnsRenameGuildName,
                writer => writer.WriteUInt16(GuildRenameRules.NameNotUsable));
    }

 /// <summary>Returns the client's (bag, slot) cell for a bag item, in the partition used by the item-use consumer.</summary>
    private static (byte bag, byte slot) ClientBagSlot(RO2.Domain.Entities.InventoryItem it)
    {
        var flat = it.Slot < 0 ? 0 : (int)it.Slot;
        if (flat < 20) return (1, (byte)(50 + flat));
        var i = flat - 20;
        return ((byte)Math.Clamp(2 + i / 35, 2, 4), (byte)(i % 35));
    }
}
