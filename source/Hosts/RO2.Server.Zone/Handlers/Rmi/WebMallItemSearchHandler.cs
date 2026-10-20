using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1471 (ReqWebMallItemSearch): lists a page of the account's web mall box.</summary>
/// <remarks>
/// Request: fixed 5 bytes, <c>[u8 box][u32 page]</c>.
/// The box byte selects the answer: box 1 (purchases) is answered by 2639 AnsWebItemSearch and box 2
/// (gifts) by 2640 AnsWebGiftItemSearch. Entries are listed newest first, from the requested page on.
/// An empty box gets the standard empty answer.
/// </remarks>
public class WebMallItemSearchHandler : ZoneRmiHandlerBase<WebMallItemSearchHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqWebMallItemSearch;
    public override string Name => "WebMallItemSearch";
    public WebMallItemSearchHandler(IServiceScopeFactory s, ILogger<WebMallItemSearchHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 5) return;
        var reader = new PacketReader(payload.Span);
        var box = reader.ReadByte();
        var page = reader.ReadUInt32();
        if (box is not (CashMallStore.BoxPurchased or CashMallStore.BoxGift))
        {
            
            
            Logger.LogDebug("{Name}: 1471 for unknown web mall box {Box}, not answered", player.Name, box);
            return;
        }

        IReadOnlyList<CashMallBoxEntry> boxEntries = Array.Empty<CashMallBoxEntry>();
        if (player.AccountId != 0)
        {
            try
            {
                boxEntries = await services.GetRequiredService<ICashMallRepository>().GetBoxAsync(player.AccountId, box, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                
                Logger.LogWarning(ex, "{Name}: web mall box of account {Account} could not be read; listing it empty",
                    player.Name, player.AccountId);
            }
        }

        
        var all = boxEntries.Select(e => new CashMallPackets.ListedEntry(
            e.EntryId, (byte)Math.Clamp(e.Count, 0, byte.MaxValue), e.ItemId,
            CashMallPackets.WallClockOleDate(e.FiledUtc), e.SenderName)).ToList();
        var body = CashMallPackets.BuildBoxListAnswer(box, page, all);

        Logger.LogDebug("{Name} lists web mall box {Box} page {Page} of a {Total}-entry box", player.Name, box, page, all.Count);

        await session.SendRmiAsync(
            box == CashMallStore.BoxGift ? ZoneS2COpcodes.AnsWebGiftItemSearch : ZoneS2COpcodes.AnsWebItemSearch, body, ct);
    }
}
