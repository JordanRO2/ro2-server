using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1652 ReqGuildSkillLearnBook: the guild master uses a guild skill book, and the guild learns the book's guild skill. The answer is 2959 AnsGuildSkillLearnBook.
/// </summary>
/// <remarks>
/// <para>
/// On success the book is consumed (2329 OnUseItem, 2330 OnUseItemNotify), the 2959 answer carries result 0, every online member receives the 2724 NfyGuildSkillLearn notice, and the user receives a 2488 OnUseSkillList update that includes the guild's link skills (<see cref="GuildSkillBookLearn.SkillListWithGuildLinks"/>).
/// </para>
/// <para>
/// A guild level already held on the book's skill line, or a missing previous level, is refused and the book is kept. Requests that cannot be learned at all are dropped without an answer: a body that is not 8 bytes, a requester who is not the guild master, an item that is not the user's guild skill book in the bag, or a book whose skill has no learnable row (<see cref="GuildSkillBookLearn.Verdict.NotLearnable"/>).
/// </para>
/// </remarks>
public class GuildSkillLearnBookHandler : ZoneRmiHandlerBase<GuildSkillLearnBookHandler>
{
 /// <summary>Storage type of the bag, the only storage the client uses items from.</summary>
    private const byte BagStorageType = 0;

    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildSkillLearnBook;
    public override string Name => "GuildSkillLearnBook";
    public GuildSkillLearnBookHandler(IServiceScopeFactory s, ILogger<GuildSkillLearnBookHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!GuildSkillBookLearn.TryParseRequest(payload.Span, out var itemUid))
        {
            Logger.LogWarning("{Name}: 1652 body is {Len} bytes (the client writes 8)", player.Name, payload.Length);
            return;
        }

        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild is null || guild.MasterCharId != player.CharacterId)
        {
            Logger.LogInformation("{Name}: 1652 from a player who is not a guild master; not answered", player.Name);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var book = await inventory.GetByIdAsync(itemUid);
        var store = services.GetRequiredService<GameDataStore>();
        var bookData = book is null ? null : store.GetItem(book.ItemId);
        if (book is null || book.CharacterId != player.CharacterId || book.StorageType != BagStorageType
            || bookData is not { Item_Type: GuildSkillBookLearn.GuildSkillBookItemType })
        {
            Logger.LogInformation("{Name}: 1652 for uid {Uid:X}, which is not a guild skill book in the user's bag; not answered",
                player.Name, itemUid);
            return;
        }

        var table = GuildSkillLearnPackets.Index(store.GuildSkillLevel);
        var guildLevel = GuildRosterPackets.GuildLevelOf(guild.Exp, store.GuildExpTable).Level;
        var skillId = bookData.Skill_ID_1;

        
        
        GuildSkillBookLearn.Verdict verdict;
        byte unspentAfter;
        lock (guild.Gate)
        {
            verdict = GuildSkillBookLearn.Plan(skillId, guild.Skills, guildLevel, table);
            if (verdict == GuildSkillBookLearn.Verdict.Learn && !guild.Skills.Contains(skillId))
                guild.Skills.Add(skillId);
            unspentAfter = GuildSkillLearnPackets.UnspentPoints(guildLevel, guild.Skills, table);
        }

        if (verdict == GuildSkillBookLearn.Verdict.NotLearnable)
        {
            Logger.LogInformation("{Name}: book {Item} names guild skill {Skill}, which no book learns (guild level {Level}); not answered",
                player.Name, book.ItemId, skillId, guildLevel);
            return;
        }
        if (verdict != GuildSkillBookLearn.Verdict.Learn)
        {
            Logger.LogInformation("{Name}: guild {Guild} cannot learn {Skill} from book {Item}: {Verdict}",
                player.Name, guild.Id, skillId, book.ItemId, verdict);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsGuildSkillLearnBook,
                GuildSkillBookLearn.BuildAnsGuildSkillLearnBook2959(GuildSkillBookLearn.ResultOf(verdict)), ct);
            return;
        }

        
        await GuildService.Instance.AddSkillAsync(guild, skillId);

        
        var (bag, slot) = ItemWorkbench.ClientBagSlot(book);
        var remaining = book.Count - 1;
        if (remaining <= 0) { await inventory.RemoveItemAsync(itemUid); remaining = 0; }
        else { book.Count = remaining; await inventory.UpdateItemAsync(book); }
        await KharaService.OnHeldItemChangedAsync(session, player, book.ItemId, services, Logger, ct);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnUseItem,
            CombatPackets.BuildUseItem2329(player.EntityId, slot, bag, itemUid, (byte)Math.Clamp(remaining, 0, 255), useSource: 1), ct);
        var notify = LootNotifyPackets.BuildUseItemNotify2330(player.EntityId, book.ItemId);
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnUseItemNotify, writer => writer.WriteBytes(notify));

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsGuildSkillLearnBook,
            GuildSkillBookLearn.BuildAnsGuildSkillLearnBook2959(GuildSkillBookLearn.ResultLearned), ct);

        await GuildPackets.BroadcastToGuildAsync(guild, PlayerManager, Broadcast, ZoneS2COpcodes.NfyGuildSkillLearn,
            GuildSkillLearnPackets.BuildNfyGuildSkillLearn2724(unspentAfter, [skillId]));

        
        
        uint[] guildSkills;
        lock (guild.Gate) guildSkills = guild.Skills.ToArray();
        await WorldEntryService.SendUseSkillList2488Async(session,
            GuildSkillBookLearn.SkillListWithGuildLinks(player.LearnedSkills, guildSkills, table));

        Logger.LogInformation("{Name}: guild {Guild} learned {Skill} from book {Item} ({Left} left), {Points} points unspent",
            player.Name, guild.Id, skillId, book.ItemId, remaining, unspentAfter);
    }
}
