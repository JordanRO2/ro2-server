using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1393 ReqMailSend - Send mail to another player (stores it + notifies the recipient).</summary>
public class MailSendHandler : ZoneRmiHandlerBase<MailSendHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMailSend;
    public override string Name => "MailSend";

    /// <summary>Config_Def type 92 "메일로 보낼 수 있는 최대 금액 (단위 루피)": the most zeny one mail may carry.</summary>
    private const ushort ConfigMaxMailZeny = 92;

    /// <summary>InventoryItem.StorageType 0: the item sits in a bag (the only place an attachment may come from).</summary>
    private const byte StorageBag = 0;

    /// <summary>1393 item uid meaning "no attachment".</summary>
    private const ulong NoAttachment = 0;

    /// <summary>Largest left-over count the 2445 u8 field can carry (every ItemInfo Stack_Max fits).</summary>
    private const int MaxLeftCount = byte.MaxValue;

    public MailSendHandler(IServiceScopeFactory s, ILogger<MailSendHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!MailPackets.TryParseSend(payload.Span, out var req))
        {
            Logger.LogWarning("{Name} sent a ReqMailSend body we could not parse ({Len} bytes)", player.Name, payload.Length);
            await RefuseAsync(session, player, MailPackets.SendSystemError);
            return;
        }

        // Everything that can refuse the send is checked BEFORE SendMailAsync takes the item off the
        // sender: the checks here, then the unknown recipient and the full mailbox inside it. A refused
        // send moves nothing and costs nothing (see MailPackets.PostageZeny), and its 2445 stops
        // the client at the result code.
        if (req.Gold < 0 || req.Gold > MaxMailZeny(services))
        {
            Logger.LogWarning("{Name} tried to mail {Gold} zeny (over the Config_Def 92 cap)", player.Name, req.Gold);
            await RefuseAsync(session, player, MailPackets.SendSystemError);
            return;
        }
        // Postage + attached zeny are TAKEN here, atomically (TryDebit), not just compared: the mail is stored
        // and its item taken further down, and a check followed by a later clamped deduct would let a concurrent
        // spender drain the same funds in between. Every refusal below hands the charge back.
        if (req.Gold > long.MaxValue - MailPackets.PostageZeny || !player.TryDebit(req.Gold + MailPackets.PostageZeny))
        {
            await RefuseAsync(session, player, MailPackets.SendNotEnoughZeny);
            return;
        }
        long charged = req.Gold + MailPackets.PostageZeny;

        var inv = services.GetService<IInventoryRepository>();
        InventoryItem? attached = null;
        if (req.ItemUid != NoAttachment)
        {
            // The same ownership test SendMailAsync applies before it takes the item. Without it a stale
            // uid silently sent the mail without its attachment.
            attached = inv == null ? null : await inv.GetByIdAsync(req.ItemUid);
            if (attached == null || attached.CharacterId != player.CharacterId || attached.StorageType != StorageBag)
            {
                player.Credit(charged);
                await RefuseAsync(session, player, MailPackets.SendItemEmpty);
                return;
            }
            // An untradeable (Is_Trade 0) or bound item is not mailed. This is defensive. 16208 is a code the
            // client already renders (string_ui 2307 "Item is empty.").
            if (RefusesAttachment(services.GetService<GameDataStore>()?.Items, attached))
            {
                Logger.LogInformation("{Name} tried to mail untradeable or bound item {ItemId} (uid {Uid}, bound {Bound})",
                    player.Name, attached.ItemId, attached.Id, attached.Bound);
                player.Credit(charged);
                await RefuseAsync(session, player, MailPackets.SendItemEmpty);
                return;
            }
        }

        // SendMailAsync refuses an unknown recipient (16202) and a full mailbox (16204) before it takes
        // the item, and refunds the item if storing the mail fails, so a refusal here has moved nothing.
        var mail = MailService.From(services, PlayerManager, Broadcast, Logger);
        var (result, created) = await mail.SendMailAsync(player, req, inv!);
        if (result != MailService.SendResult.Sent || created == null)
        {
            Logger.LogInformation("{Name} -> {Recipient}: {Result}", player.Name, req.RecipientName, result);
            player.Credit(charged);
            await RefuseAsync(session, player, result switch
            {
                MailService.SendResult.RecipientNotFound => MailPackets.SendTargetNotFound,
                MailService.SendResult.MailboxFull => MailPackets.SendMailboxFull,
                _ => MailPackets.SendSystemError,
            });
            return;
        }

        // SendMailAsync stored the attached zeny (1393 @92) and the body text (@113) on the mail.
        long mailedGold = created.Gold;

        // Postage + attached zeny were taken above. 2445 blob+10 is the sender's wallet after the send, which the
        // client writes straight into its wallet; no separate 2435 is sent for a send.
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);

        // The mailed item as it sat in the bag: uid, its (slot, container) and what is left in that cell.
        // The client re-places the uid with that count, and 0 clears the cell. SendMailAsync takes
        // min(count, stack), and the u8 count covers every stack (ItemInfo Stack_Max <= 255), so a
        // full-stack attach leaves 0.
        ulong sentUid = 0;
        byte slot = 0, container = 0, left = 0;
        if (attached != null && created.AttachedItemId != 0)
        {
            sentUid = created.AttachedItemUid;
            (container, slot) = ItemWorkbench.ClientBagSlot(attached);
            left = (byte)Math.Clamp(attached.Count - created.AttachedItemCount, 0, MaxLeftCount);
        }

        Logger.LogInformation("{Name} -> {Recipient}: mail {Uid:X} (item {Item:X} x{Cnt}, {Gold} zeny), wallet {Wallet}",
            player.Name, req.RecipientName, created.Uid, sentUid, created.AttachedItemCount, mailedGold, player.Gold);

        // The attachment has left the bag, so the held count of its item changed: a Complete_Type 4 Khara
        // row naming it reports the new count (2577) BEFORE the send's 2445 (1393 -> 2577 row 1 item 35000005
        // count 0 -> 2445 in the same millisecond).
        if (created.AttachedItemId != 0)
            await KharaService.OnHeldItemChangedAsync(session, player, created.AttachedItemId, services, Logger, ct);

        await session.SendRmiAsync(ZoneS2COpcodes.OnMailSend,
            MailPackets.BuildMailSend2445(player.EntityId, player.Gold, sentUid, slot, container, left));
    }

    /// <summary>True when the attached item must not be mailed: its template is known and <see cref="TradeGate"/>
    /// refuses it. An item whose template is unknown to the data is not refused here (as TradeMoveHandler).</summary>
    internal static bool RefusesAttachment(IReadOnlyDictionary<uint, ItemData>? items, InventoryItem attached)
        => items != null && items.TryGetValue(attached.ItemId, out var template) && TradeGate.RefusedAsNotTradeable(template, attached);

    private static Task RefuseAsync(ProudSession session, PlayerState player, ushort result) =>
        session.SendRmiAsync(ZoneS2COpcodes.OnMailSend, MailPackets.BuildMailSendFailed2445(player.EntityId, result));

    /// <summary>Config_Def type 92, or no cap when the row is absent or unparseable.</summary>
    private static long MaxMailZeny(IServiceProvider services)
    {
        var row = services.GetService<GameDataStore>()?.ConfigDef.FirstOrDefault(c => c.type == ConfigMaxMailZeny);
        return row != null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap)
            ? cap : long.MaxValue;
    }
}
