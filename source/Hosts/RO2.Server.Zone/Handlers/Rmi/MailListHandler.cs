using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1394 ReqMailList - Request mail inbox; answers 2444 OnMailList with the stored mail.</summary>
public class MailListHandler : ZoneRmiHandlerBase<MailListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMailList;
    public override string Name => "MailList";

    /// <summary>Low byte of an actor handle: the actor type tag (0x13 = NPC, 0x11 = PC).</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>Offset of the mailbox NPC handle (u32) inside the 1394 request blob.</summary>
    private const int NpcHandleOffset = 8;

    /// <summary>Smallest 1394 blob that holds the NPC handle: the offset plus the u32.</summary>
    private const int NpcHandleBlobLength = NpcHandleOffset + sizeof(uint);

    /// <summary>First mail kind whose item template id the client reads from the list row (kinds 1..11).</summary>
    private const byte FirstTemplateMailKind = 1;

    /// <summary>Last mail kind whose item template id the client reads from the list row.</summary>
    private const byte LastTemplateMailKind = 11;

    public MailListHandler(IServiceScopeFactory s, ILogger<MailListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // The mailbox is opened at a mailbox NPC. 1394 blob+8 is that NPC's handle (a 0x13-tagged NPC handle).
        // A handle that is not an NPC of this server, or a player who
        // is dead, on another map or out of reach of it, gets the empty list and no mailbox contents.
        // The NPC interaction radius is ActorGate.NpcInteractReach; same-map is the hard rule, distance is a loose guard.
        uint npcHandle = TryReadNpcHandle(payload.Span, out var handle) ? handle : 0;
        var npc = (npcHandle & ActorTagMask) == NpcStateManager.NpcActorTag
            ? services.GetRequiredService<NpcStateManager>().GetNpc(npcHandle)
            : null;
        var refusal = Gate(player, npc);
        if (refusal != ActorRefusal.None)
        {
            Logger.LogInformation("{Name} opens the mailbox at 0x{Npc:X8}: refused ({Refusal}), empty list", player.Name, npcHandle, refusal);
            await session.SendRmiAsync(ZoneS2COpcodes.OnMailList, MailPackets.BuildMailList2444(player.EntityId, new List<byte[]>()));
            return;
        }

        // 2444 OnMailList is compact-BA (msgTag 0xE353): [u16 tag][u32 owner][u16 count] then count
        // 146-byte rows @blob+10. On any
        // failure we fall back to the empty frame (count 0) rather than a wrong body.
        var records = new List<byte[]>();
        try
        {
            var mail = MailService.From(services, PlayerManager, Broadcast, Logger);
            var inbox = await mail.GetInboxAsync(player.CharacterId);
            foreach (var m in inbox)
            {
                // The stored kind: 0 for a player mail (kind 22 system mails have sender/title as string ids, which we
                // never create), or the
                // auction kind AuctionService stamps (AuctionMailKind 2/7/10/11 and AuctionBidMailKind 1/4/6/8).
                // The template id at rec+58
                // is read by the client only for kinds 1..11: it is left 0 for player mail,
                // including those with an item attached, and set on every auction row
                // (kinds 2/7/10/11), where the client builds the text from kind + template.
                records.Add(MailPackets.BuildListRecord146(
                    m.Uid,
                    kind: m.Kind,
                    isRead: m.IsRead,
                    m.SentAt,
                    (m.Gold > 0 && !m.GoldClaimed) ? m.Gold : 0,
                    (m.AttachedItemId != 0 && !m.ItemClaimed) ? m.AttachedItemUid : 0,
                    itemTemplateId: m.Kind is >= FirstTemplateMailKind and <= LastTemplateMailKind ? m.KindItemId : 0,
                    m.SenderName,
                    m.Title));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MailList build failed for {Name}; sending empty inbox", player.Name);
            records.Clear();
        }

        Logger.LogDebug("{Name} opens mailbox: {Count} mails", player.Name, records.Count);
        await session.SendRmiAsync(ZoneS2COpcodes.OnMailList, MailPackets.BuildMailList2444(player.EntityId, records));
    }

    /// <summary>The mailbox NPC handle, u32 at 1394 blob+8; false when the body is too short to hold it.</summary>
    internal static bool TryReadNpcHandle(ReadOnlySpan<byte> payload, out uint npcHandle)
    {
        npcHandle = 0;
        if (!MailPackets.TryGetBlob(payload, out var blob) || blob.Length < NpcHandleBlobLength) return false;
        npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(NpcHandleOffset));
        return true;
    }

    /// <summary>Whether <paramref name="player"/> may open the mailbox at <paramref name="npc"/> (null = the handle
    /// did not resolve to an NPC of this server).</summary>
    internal static ActorRefusal Gate(PlayerState player, NpcRuntimeState? npc)
        => ActorGate.CanTransact(player, npc, ActorGate.NpcInteractReach);
}
