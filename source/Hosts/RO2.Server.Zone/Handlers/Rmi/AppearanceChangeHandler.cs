using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1705 ReqAppearanceChange: an Appearance Change Ticket re-styles the character.</summary>
/// <remarks>
/// <para>
/// <b>Send path.</b> An NPC with function 46 opens window 221 client-side (no 1323 comes first, the ticket is
/// consumed only here). Its OK handler validates locally (refuses, string_ui 16017, while transformed), pins
/// create[0] to the player's own byte, and sends ReqAppearanceChange_RMI1705. The
/// body is the 15-byte create[0..14] block (<see cref="AppearanceChangePackets"/>).
/// </para>
/// <para>
/// <b>Retail's answer</b> is four frames in one millisecond and in this order: 2329 OnUseItem (the ticket, remaining 0,
/// useSource 3), 2487 OnUpdatePCSaveData (216 B, the 2310 head), 3046 AnsAppearanceChange (31 B, the 15 bytes
/// echoed, result 0) and 2315 OnPCViewData (631 B, the self record carrying the new bytes at rec+26). This handler
/// sends the same four in the same order, each from the builder that already makes it elsewhere.
/// </para>
/// <para>
/// <b>State.</b> The 15 bytes replace create[0..14] of the stored create block (<c>CharacterAppearance.RawBytes</c>),
/// the block the char list [50..64], the 2310 body[54..68], the 2315 rec+26..40 and the 2504 header all read, and
/// the parsed appearance fields are re-derived from it by <see cref="CharacterSerializer.ParseCreateAppearance"/>.
/// <c>Character.Gender</c> / <c>Character.Race</c> take create[0] / create[1], as CharacterCreateHandler stores
/// them. Written with a targeted $set (as RenameChaNameHandler does), and to the live snapshot too, so a later
/// whole-document save of <see cref="PlayerState.Character"/> cannot put the old look back.
/// </para>
/// <para>
/// <b>Refusals</b> (3046 with a result, nothing consumed): 23060 when the bag holds no ticket (ItemInfo.Item_Type
/// 72); 23061 when the gender byte (create[1], see <see cref="AppearanceChangePackets.GenderIndex"/>) changes under
/// a worn gender-specific item. 23062 (transformed) is not sent: the client checks it itself and this server keeps
/// no transform state. The codes are the arms of the consumer's switch. Retail's refusal frame
/// (result 23060) carries not the request but the stored block (<see cref="AppearanceChangePackets.RefusalBlock"/>); the
/// consumer reads only the result then. No 23061 frame is known.
/// </para>
/// <para>
/// <b>Other players.</b> The 3046 consumer resolves blob+8 through CActorRegistry and re-dresses any actor
/// with that handle, and it has branches for an actor that is the local player's target (windows 10 and 29), so
/// it is written to be received about someone else. The 3046 therefore also goes to the other players on the map.
/// That part is supported by the reader but not verified against retail.
/// </para>
/// </remarks>
public class AppearanceChangeHandler : ZoneRmiHandlerBase<AppearanceChangeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAppearanceChange;
    public override string Name => "AppearanceChange";

    /// <summary>InventoryItem.StorageType of a bag stack.</summary>
    private const byte StorageBag = 0;

    /// <summary>InventoryItem.StorageType of a worn (equipped) item.</summary>
    private const byte StorageEquipped = 1;

    public AppearanceChangeHandler(IServiceScopeFactory s, ILogger<AppearanceChangeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != AppearanceChangePackets.AppearanceLength)
        {
            
            Logger.LogWarning("{Name}: 1705 body is {Len} bytes (the client sends {Want})",
                player.Name, payload.Length, AppearanceChangePackets.AppearanceLength);
            return;
        }
        var request = payload.Span.ToArray();

        var characters = services.GetRequiredService<ICharacterRepository>();
        var ch = player.Character ?? await characters.GetByIdAsync(player.CharacterId);
        if (ch is null)
        {
            Logger.LogWarning("{Name}: 1705 with no character row; not answered", player.Name);
            return;
        }

        var store = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var items = await inventory.GetByCharacterAsync(player.CharacterId);

        
        var stored = CharacterSerializer.ParseAppearancePublic(ch.AppearanceJson);
        var refusalBlock = AppearanceChangePackets.RefusalBlock(stored.RawBytes, request);

        var ticket = items
            .Where(i => i.StorageType == StorageBag && i.Count > 0
                        && store.GetItem(i.ItemId)?.Item_Type == AppearanceChangePackets.AppearanceTicketItemType)
            .OrderBy(i => i.Slot)
            .FirstOrDefault();
        if (ticket is null)
        {
            Logger.LogInformation("{Name}: appearance change refused, no Appearance Change Ticket in the bag", player.Name);
            await SendAnswerAsync(session, player, refusalBlock, AppearanceChangePackets.ResultNoTicket, ct);
            return;
        }

        var newRaw = AppearanceChangePackets.ApplyToCreateBlock(stored.RawBytes, ch.Gender, request);
        if (newRaw[0] != request[0])
            Logger.LogWarning("{Name}: 1705 create[0] {Sent} differs from the stored {Kept}; kept (the client pins it)",
                player.Name, request[0], newRaw[0]);

        byte oldGender = stored.RawBytes is { Length: > AppearanceChangePackets.GenderIndex } r
            ? r[AppearanceChangePackets.GenderIndex] : stored.Race;
        byte newGender = newRaw[AppearanceChangePackets.GenderIndex];
        var worn = items.Where(i => i.StorageType == StorageEquipped).ToList();
        if (AppearanceChangePackets.GenderChangeBlockedByWornItems(oldGender, newGender,
                worn.Select(i => store.GetItem(i.ItemId)?.Require_Sex ?? (byte)0)))
        {
            Logger.LogInformation("{Name}: appearance change refused, gender {Old} -> {New} under a gender-specific worn item",
                player.Name, oldGender, newGender);
            await SendAnswerAsync(session, player, refusalBlock, AppearanceChangePackets.ResultGenderSpecificItemWorn, ct);
            return;
        }

        
        var appearance = CharacterSerializer.ParseCreateAppearance(new PacketReader(newRaw));
        string appearanceJson = CharacterSerializer.ToJson(appearance);
        byte gender = appearance.Gender, race = appearance.Race;

        var matched = await services.GetRequiredService<ICharacterRepository>()
            .UpdateAppearanceAsync(player.CharacterId, appearanceJson, gender, race, ct);
        if (!matched)
        {
            Logger.LogWarning("{Name}: appearance change not stored, character row {Id} not found", player.Name, player.CharacterId);
            return;
        }
        ch.AppearanceJson = appearanceJson;
        ch.Gender = gender;
        ch.Race = race;
        if (player.Character is { } live && !ReferenceEquals(live, ch))
        {
            live.AppearanceJson = appearanceJson;
            live.Gender = gender;
            live.Race = race;
        }
        player.Gender = gender;

        
        int remaining = ticket.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(ticket.Id); remaining = 0; }
        else { ticket.Count = remaining; await inventory.UpdateItemAsync(ticket); }
        var (bag, slot) = ItemWorkbench.ClientBagSlot(ticket);

        
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem,
            w => w.WriteBytes(CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, ticket.Id,
                (byte)Math.Clamp(remaining, 0, byte.MaxValue), useSource: AppearanceChangePackets.UseSourceAppearanceTicket)), ct);

        
        
        AnsMapLoadingHandler.SyncLiveLevel(player, ch);
        var talentPoints = StatService.TalentPointsRemaining(store, player.Job, player.Level,
            player.LearnedSkills.Select(kv => (kv.Key, kv.Value)));
        await session.SendRmiAsync(ZoneS2COpcodes.OnUpdatePCSaveData,
            PcSaveDataPackets.BuildOnUpdatePCSaveData2487(player.EntityId, PcSaveDataPackets.BuildLiveHead205(player, ch, talentPoints)), ct);

        
        var applied = newRaw.AsSpan(0, AppearanceChangePackets.AppearanceLength).ToArray();
        var answer = AppearanceChangePackets.BuildAnsAppearanceChange3046(player.EntityId, applied, AppearanceChangePackets.ResultApplied);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsAppearanceChange, answer, ct);

        
        var viewData = ActorStatePackets.BuildOnPCViewData2315(player, ch, worn, services.GetRequiredService<IGuildEmblemRepository>());
        await session.SendRmiAsync(ZoneS2COpcodes.OnPCViewData, viewData, ct);

        
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.AnsAppearanceChange, w => w.WriteBytes(answer));

        Logger.LogInformation("{Name}: appearance changed to {Block} (ticket {Uid}, {Left} left)",
            player.Name, Convert.ToHexString(applied), ticket.Id, remaining);
    }

 /// <summary>A refusal: <paramref name="block"/> is <see cref="AppearanceChangePackets.RefusalBlock"/> (the stored
 /// create[0..14], as retail's refusal carries); the consumer reads only the result then.</summary>
    private static Task SendAnswerAsync(ProudSession session, PlayerState player, byte[] block, ushort result, CancellationToken ct) =>
        session.SendRmiAsync(ZoneS2COpcodes.AnsAppearanceChange,
            AppearanceChangePackets.BuildAnsAppearanceChange3046(player.EntityId, block, result), ct);
}
