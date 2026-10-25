using RO2.Economy.Domain;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Services;

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

public class ReqCardBookListHandler : Base1442
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCardBookList; 
    public override string Name => "ReqCardBookList";
    public ReqCardBookListHandler(IServiceScopeFactory s, ILogger<ReqCardBookListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name} ({Size}b)", session.SessionId, Name, payload.Length);
        
        
        
        
        
        
        uint actorHandle = 0;
        uint characterId = 0;
        var pm = services.GetRequiredService<PlayerStateManager>();
        if (pm.TryGetPlayer(session.SessionId, out var player) && player != null)
        {
            actorHandle = player.EntityId;
            characterId = player.CharacterId;
        }

        IReadOnlyList<CardRegistryEntry> registered = Array.Empty<CardRegistryEntry>();
        IReadOnlyList<CardBookEntry> copies = Array.Empty<CardBookEntry>();
        Func<uint, byte> gradeTypeOf = _ => 0;
        if (characterId != 0)
        {
            var svc = CardService.From(services, Logger);
            gradeTypeOf = svc.GradeOf;
            var book = await svc.GetBookAsync(characterId, ct);
            copies = book.Cards;
            try
            {
                registered = await RegisterHeldCardsAsync(services, svc, characterId, book, ct);
            }
            catch (Exception ex)
            {
                
                Logger.LogWarning(ex, "Card registry update failed for char {Char}; listing the book's copies only", characterId);
            }
        }

        var body = CardPackets.BuildCardBookList2587(actorHandle, registered, copies, gradeTypeOf);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsCardBookList, writer => writer.WriteBytes(body), ct);

        
        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfyWebMallItems, await BuildWebMallNotifyAsync(services, player, ct), ct);
    }

 /// <summary>
 /// Builds the 2638 web mall box notification for the player's account (see <see cref="WebMallNotifyPackets.Build2638"/>).
 /// A missing player or a failed read yields the empty form.
 /// </summary>
    private async Task<byte[]> BuildWebMallNotifyAsync(IServiceProvider services, RO2.Server.Zone.Models.PlayerState? player,
        CancellationToken ct)
    {
        if (player is null || player.AccountId == 0) return WebMallNotifyPackets.Build2638(0, 0);
        try
        {
            var (purchased, gifts) = await services.GetRequiredService<ICashMallRepository>()
                .GetBoxCountsAsync(player.AccountId, ct);
            return WebMallNotifyPackets.Build2638(purchased, gifts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Web mall box of account {Account} could not be read; 2638 goes out empty", player.AccountId);
            return WebMallNotifyPackets.Build2638(0, 0);
        }
    }

 /// <summary>
 /// Merges the character's listable held and booked cards into its card registry and persists the registry
 /// when it changed.
 /// </summary>
 /// <remarks>
 /// Held cards are every inventory row of the character and the cards socketed into them. Only cards of item
 /// type 13 are listed. A storage error is logged and the in-memory merge is still returned.
 /// </remarks>
    private async Task<IReadOnlyList<CardRegistryEntry>> RegisterHeldCardsAsync(
        IServiceProvider services, CardService cards, uint characterId, CardBookDocument book, CancellationToken ct)
    {
        var data = services.GetRequiredService<GameDataStore>();
        bool Listable(uint itemId) => itemId != 0 && cards.IsCard(itemId)
            && data.GetItem(itemId)?.Item_Type == CardRegistryStore.ListedCardItemType;

        var held = new List<uint>();
        foreach (var it in await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(characterId))
        {
            if (Listable(it.ItemId)) held.Add(it.ItemId);
            foreach (var socketed in ItemSockets.ParseSockets(it.CardsJson))
                if (Listable(socketed)) held.Add(socketed);
        }
        var inBook = new List<uint>();
        foreach (var e in book.Cards)
            if (Listable(e.CardId)) inBook.Add(e.CardId);

        ICardRegistryRepository? store = null;
        CardRegistryDocument doc;
        try
        {
            store = services.GetRequiredService<ICardRegistryRepository>();
            doc = await store.GetAsync(characterId, ct);
        }
        catch (Exception ex)
        {
            
            
            Logger.LogWarning(ex, "Card registry load failed for char {Char}; listing this visit's cards only", characterId);
            store = null;
            doc = new CardRegistryDocument { Id = characterId };
        }

        if (CardRegistryStore.Merge(doc.Cards, inBook, held) && store is not null)
        {
            try { await store.SaveAsync(doc, ct); }
            catch (Exception ex) { Logger.LogWarning(ex, "Card registry save failed for char {Char}", characterId); }
        }
        return doc.Cards;
    }
}
